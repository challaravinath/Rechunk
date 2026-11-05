using StackExchange.Redis;

namespace Rechunk.Storage.Redis;

/// <summary>
/// The fingerprint backend on Redis. The claim, commit and release steps are Lua scripts, and Redis runs a script
/// without letting any other command in, so many workers asking for the same hash get exactly one winner.
///
/// Times (the lease) are written as milliseconds from the clock of the worker that claims, so the workers should
/// have clocks that agree to within a good bit less than the lease.
/// </summary>
public sealed class RedisFingerprintBackend : IFingerprintBackend
{
    // Replies: status code, owner, lease end in unix ms. The codes are the ClaimStatus values.
    private const string ClaimScript = """
        local row = redis.call('HMGET', KEYS[1], 'chunk', 'committed', 'holder', 'lease')
        if not row[1] then
          redis.call('HSET', KEYS[1], 'chunk', ARGV[1], 'committed', '0', 'holder', ARGV[2], 'lease', ARGV[3])
          return {0, ARGV[1], ARGV[3]}
        end
        if row[2] == '1' then
          return {3, row[1], '0'}
        end
        if row[3] == ARGV[2] and row[1] == ARGV[1] then
          redis.call('HSET', KEYS[1], 'lease', ARGV[3])
          return {1, ARGV[1], ARGV[3]}
        end
        if tonumber(row[4]) <= tonumber(ARGV[4]) then
          redis.call('HSET', KEYS[1], 'chunk', ARGV[1], 'holder', ARGV[2], 'lease', ARGV[3])
          return {2, ARGV[1], ARGV[3]}
        end
        return {4, row[1], row[4]}
        """;

    // Replies with the CommitStatus values: 0 committed, 1 already committed, 2 not held.
    private const string CommitScript = """
        local row = redis.call('HMGET', KEYS[1], 'chunk', 'committed', 'holder')
        if not row[1] then
          return 2
        end
        if row[2] == '1' then
          if row[1] == ARGV[3] then return 1 end
          return 2
        end
        if row[3] == ARGV[2] and row[1] == ARGV[1] then
          redis.call('HSET', KEYS[1], 'chunk', ARGV[3], 'committed', '1', 'holder', '', 'lease', '0')
          return 0
        end
        return 2
        """;

    private const string RemoveOwnedScript = """
        local row = redis.call('HMGET', KEYS[1], 'chunk', 'committed')
        if row[1] and row[2] == '1' and row[1] == ARGV[1] then
          redis.call('DEL', KEYS[1])
          return 1
        end
        return 0
        """;

    private const string ReleaseScript = """
        local row = redis.call('HMGET', KEYS[1], 'chunk', 'committed', 'holder')
        if row[1] and row[2] ~= '1' and row[3] == ARGV[2] and row[1] == ARGV[1] then
          redis.call('DEL', KEYS[1])
          return 1
        end
        return 0
        """;

    private const string PutScript = """
        redis.call('SET', KEYS[1], ARGV[1])
        for i = 2, #KEYS do
          redis.call('SADD', KEYS[i], ARGV[2])
        end
        return 1
        """;

    private const string DeleteScript = """
        local removed = redis.call('DEL', KEYS[1])
        for i = 2, #KEYS do
          redis.call('SREM', KEYS[i], ARGV[1])
        end
        return removed
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly int _database;
    private readonly RedisKeys _keys;

    public RedisFingerprintBackend(IConnectionMultiplexer redis, RedisStorageOptions? options = null)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        options ??= new RedisStorageOptions();
        _database = options.Database;
        _keys = new RedisKeys(options.KeyPrefix);
    }

    private IDatabase Db => _redis.GetDatabase(_database);

    public async ValueTask<ClaimResult> ClaimAsync(ClaimRequest request, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var until = (now + request.Lease).ToUnixTimeMilliseconds();
        var reply = (RedisResult[])(await Db.ScriptEvaluateAsync(
            ClaimScript,
            [_keys.Exact(request.Scope, request.ExactHash.Span)],
            [request.Id.Value, request.Holder, until, now.ToUnixTimeMilliseconds()]).ConfigureAwait(false))!;

        var status = (ClaimStatus)(int)reply[0];
        var owner = new ChunkId((string)reply[1]!);
        DateTimeOffset? leaseUntil = status == ClaimStatus.AlreadyCommitted
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(long.Parse((string)reply[2]!, System.Globalization.CultureInfo.InvariantCulture));
        return new ClaimResult(status, owner, leaseUntil);
    }

    public async ValueTask<CommitStatus> CommitAsync(CommitRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var owner = (request.AliasOf ?? request.Id).Value;
        var reply = await Db.ScriptEvaluateAsync(
            CommitScript,
            [_keys.Exact(request.Scope, request.ExactHash.Span)],
            [request.Id.Value, request.Holder, owner]).ConfigureAwait(false);
        return (CommitStatus)(int)reply;
    }

    public async ValueTask<bool> ReleaseAsync(ReleaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var reply = await Db.ScriptEvaluateAsync(
            ReleaseScript,
            [_keys.Exact(request.Scope, request.ExactHash.Span)],
            [request.Id.Value, request.Holder]).ConfigureAwait(false);
        return (int)reply == 1;
    }

    public async ValueTask<ChunkId?> FindCommittedAsync(string scope, ReadOnlyMemory<byte> exactHash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var row = await Db.HashGetAsync(_keys.Exact(scope, exactHash.Span), ["chunk", "committed"]).ConfigureAwait(false);
        return row[1] == "1" && row[0].HasValue ? new ChunkId((string)row[0]!) : null;
    }

    public async ValueTask<bool> RemoveAsync(string scope, ReadOnlyMemory<byte> exactHash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await Db.KeyDeleteAsync(_keys.Exact(scope, exactHash.Span)).ConfigureAwait(false);
    }

    public async ValueTask<bool> RemoveOwnedAsync(string scope, ReadOnlyMemory<byte> exactHash, ChunkId owner, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var reply = await Db.ScriptEvaluateAsync(
            RemoveOwnedScript, [_keys.Exact(scope, exactHash.Span)], [owner.Value]).ConfigureAwait(false);
        return (int)reply == 1;
    }

    public async ValueTask PutAsync(
        string scope, BandKind kind, string id, byte[] payload, IReadOnlyList<BandKey> keys, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var all = new RedisKey[keys.Count + 1];
        all[0] = _keys.Entry(scope, kind, id);
        for (var i = 0; i < keys.Count; i++)
        {
            all[i + 1] = _keys.Band(scope, kind, keys[i]);
        }

        await Db.ScriptEvaluateAsync(PutScript, all, [payload, id]).ConfigureAwait(false);
    }

    public async ValueTask<bool> DeleteAsync(
        string scope, BandKind kind, string id, IReadOnlyList<BandKey> keys, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var all = new RedisKey[keys.Count + 1];
        all[0] = _keys.Entry(scope, kind, id);
        for (var i = 0; i < keys.Count; i++)
        {
            all[i + 1] = _keys.Band(scope, kind, keys[i]);
        }

        var removed = await Db.ScriptEvaluateAsync(DeleteScript, all, [id]).ConfigureAwait(false);
        return (int)removed > 0;
    }

    public async ValueTask<byte[]?> GetAsync(string scope, BandKind kind, string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var value = await Db.StringGetAsync(_keys.Entry(scope, kind, id)).ConfigureAwait(false);
        return value.HasValue ? (byte[])value! : null;
    }

    public async ValueTask<IReadOnlyList<BandEntry>> QueryAsync(
        string scope, BandKind kind, IReadOnlyList<BandKey> keys, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (keys.Count == 0)
        {
            return [];
        }

        var db = Db;
        var sets = keys.Select(k => _keys.Band(scope, kind, k)).ToArray();
        var ids = await db.SetCombineAsync(SetOperation.Union, sets).ConfigureAwait(false);
        if (ids.Length == 0)
        {
            return [];
        }

        var names = ids.Select(i => (string)i!).ToArray();
        var payloads = await db.StringGetAsync(names.Select(n => _keys.Entry(scope, kind, n)).ToArray()).ConfigureAwait(false);

        var found = new List<BandEntry>(names.Length);
        for (var i = 0; i < names.Length; i++)
        {
            // an id can linger in a band set after its entry was deleted, it is skipped here
            if (payloads[i].HasValue)
            {
                found.Add(new BandEntry(names[i], (byte[])payloads[i]!));
            }
        }

        return found;
    }
}
