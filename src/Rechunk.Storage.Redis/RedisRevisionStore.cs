using System.Globalization;
using System.Text.Json;
using StackExchange.Redis;

namespace Rechunk.Storage.Redis;

/// <summary>
/// The revision store on Redis. A commit is one Lua script, and Redis runs a script without letting any other command
/// in, so the check of the revision, the move of the document and the change of every count happen as one step.
/// Every key of a scope has the scope in its hash tag, so the script can touch them all on a cluster too.
///
/// Times (the sweep lease) are written as milliseconds from the clock of the worker that asks, so the workers should
/// have clocks that agree to within a good bit less than the lease.
/// </summary>
public sealed class RedisRevisionStore : IRevisionStore
{
    // States: 0 active, 1 orphaned, 2 retiring.
    // KEYS: 1 document, 2 set of all chunks, 3 pending set, 4 documents of the scope, 5 set of the source the document is
    // in now (or a placeholder), 6 set of the source it moves to (or a placeholder), then a ref key for each added chunk,
    // for each removed chunk, an alias set for each alias, then the chunk to documents sets to add this document to,
    // then the ones to take it out of.
    // ARGV: 1 expected revision, 2 unused, 3 updated ticks, 4 entries as json, 5 added count, 6 removed count,
    // 7 alias count, 8 chunk sets to add to, 9 chunk sets to leave, 10 document id, 11 new source ('' for none),
    // 12 '1' when KEYS[5] is a real source set, 13 '1' when KEYS[6] is, then the added chunk ids, the removed chunk ids
    // and the aliases.
    // Reply: status (0 committed, 1 conflict, 2 blocked), revision, list of chunk ids (orphaned, or blocking).
    private const string CommitScript = """
        local rev = tonumber(redis.call('HGET', KEYS[1], 'revision') or '0')
        if rev ~= tonumber(ARGV[1]) then
          return {1, rev, {}}
        end
        local nA = tonumber(ARGV[5])
        local nR = tonumber(ARGV[6])
        local nL = tonumber(ARGV[7])
        local nCa = tonumber(ARGV[8])
        local nCd = tonumber(ARGV[9])
        local base = 13
        local blocking = {}
        for i = 1, nA do
          if redis.call('HGET', KEYS[6 + i], 'state') == '2' then
            blocking[#blocking + 1] = ARGV[base + i]
          end
        end
        if #blocking > 0 then
          return {2, rev, blocking}
        end
        for i = 1, nA do
          local k = KEYS[6 + i]
          local id = ARGV[base + i]
          redis.call('HINCRBY', k, 'count', 1)
          redis.call('HSET', k, 'state', '0')
          redis.call('SADD', KEYS[2], id)
          redis.call('ZREM', KEYS[3], id)
        end
        local orphaned = {}
        for j = 1, nR do
          local k = KEYS[6 + nA + j]
          local id = ARGV[base + nA + j]
          local c = tonumber(redis.call('HGET', k, 'count') or '0')
          if c > 0 then
            c = c - 1
            redis.call('HSET', k, 'count', c)
            if c == 0 then
              redis.call('HSET', k, 'state', '1')
              redis.call('ZADD', KEYS[3], 0, id)
              orphaned[#orphaned + 1] = id
            end
          end
        end
        for p = 1, nL do
          redis.call('SADD', KEYS[6 + nA + nR + p], ARGV[base + nA + nR + p])
        end
        local doc = ARGV[10]
        redis.call('ZADD', KEYS[4], 0, doc)
        if ARGV[11] ~= '' then
          if ARGV[12] == '1' and KEYS[5] ~= KEYS[6] then
            redis.call('ZREM', KEYS[5], doc)
          end
          redis.call('ZADD', KEYS[6], 0, doc)
          redis.call('HSET', KEYS[1], 'source', ARGV[11])
        end
        for q = 1, nCa do
          redis.call('ZADD', KEYS[6 + nA + nR + nL + q], 0, doc)
        end
        for q = 1, nCd do
          redis.call('ZREM', KEYS[6 + nA + nR + nL + nCa + q], doc)
        end
        redis.call('HSET', KEYS[1], 'revision', rev + 1, 'updated', ARGV[3], 'entries', ARGV[4])
        return {0, rev + 1, orphaned}
        """;

    // KEYS: 1 ref, 2 pending set, 3 aliases. ARGV: 1 holder, 2 lease end ms, 3 now ms, 4 chunk id.
    // Replies nil when the chunk can not be taken, else the aliases (possibly an empty list).
    private const string BeginRetireScript = """
        local row = redis.call('HMGET', KEYS[1], 'count', 'state', 'holder', 'lease')
        if not row[1] or tonumber(row[1]) ~= 0 then
          return false
        end
        local free = row[2] == '1'
          or (row[2] == '2' and (row[3] == ARGV[1] or tonumber(row[4]) <= tonumber(ARGV[3])))
        if not free then
          return false
        end
        redis.call('HSET', KEYS[1], 'state', '2', 'holder', ARGV[1], 'lease', ARGV[2])
        redis.call('ZADD', KEYS[2], ARGV[2], ARGV[4])
        return redis.call('SMEMBERS', KEYS[3])
        """;

    // KEYS: 1 ref, 2 pending set, 3 aliases, 4 set of all chunks. ARGV: 1 holder, 2 chunk id.
    private const string FinishRetireScript = """
        local row = redis.call('HMGET', KEYS[1], 'count', 'state', 'holder')
        if row[1] and tonumber(row[1]) == 0 and row[2] == '2' and row[3] == ARGV[1] then
          redis.call('DEL', KEYS[1], KEYS[3])
          redis.call('ZREM', KEYS[2], ARGV[2])
          redis.call('SREM', KEYS[4], ARGV[2])
          return 1
        end
        return 0
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly int _database;
    private readonly RedisKeys _keys;

    public RedisRevisionStore(IConnectionMultiplexer redis, RedisStorageOptions? options = null)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        options ??= new RedisStorageOptions();
        _database = options.Database;
        _keys = new RedisKeys(options.KeyPrefix);
    }

    private IDatabase Db => _redis.GetDatabase(_database);

    private sealed record EntryJson(int O, string I, string T, string? L, int F, int? P, string? R);

    public async ValueTask<DocumentManifest?> GetAsync(string scope, DocumentId document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        var fields = await Db.HashGetAsync(_keys.Doc(scope, document.Value), ["revision", "updated", "entries", "source"]).ConfigureAwait(false);
        if (!fields[0].HasValue)
        {
            return null;
        }

        var entries = JsonSerializer.Deserialize<List<EntryJson>>((string)fields[2]!) ?? new List<EntryJson>();
        return new DocumentManifest(
            scope,
            document,
            int.Parse((string)fields[0]!, CultureInfo.InvariantCulture),
            entries.Select(e => new ManifestEntry(
                e.O, new ChunkId(e.I), new ChunkId(e.T), e.L, e.F, e.P, e.R is null ? null : new ChunkId(e.R))).ToList(),
            new DateTimeOffset(long.Parse((string)fields[1]!, CultureInfo.InvariantCulture), TimeSpan.Zero),
            fields[3].HasValue ? (string)fields[3]! : null);
    }

    public async ValueTask<CommitRevisionResult> CommitAsync(CommitRevisionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        RevisionRules.Check(request);

        // the plan is made against the manifest we read here, and the script checks the revision, so a change in
        // between shows up as a conflict and nothing is written
        var current = await GetAsync(request.Scope, request.Document, ct).ConfigureAwait(false);
        var (added, removed) = RevisionRules.Diff(current?.Entries, request.Entries);
        var aliases = request.Entries.Where(e => e.Id != e.Target).Select(e => (Target: e.Target.Value, Alias: e.Id.Value)).Distinct().ToList();

        var oldKeys = (current?.Entries ?? Array.Empty<ManifestEntry>())
            .SelectMany(e => new[] { e.Id.Value, e.Target.Value }).ToHashSet(StringComparer.Ordinal);
        var newKeys = request.Entries.SelectMany(e => new[] { e.Id.Value, e.Target.Value }).ToHashSet(StringComparer.Ordinal);
        var join = newKeys.Where(k => !oldKeys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var leave = oldKeys.Where(k => !newKeys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();

        var docKey = _keys.Doc(request.Scope, request.Document.Value);
        var hasOldSource = current?.Source is not null;
        var hasNewSource = request.Source is not null;
        var keys = new List<RedisKey>
        {
            docKey,
            _keys.RefSet(request.Scope),
            _keys.Pending(request.Scope),
            _keys.Docs(request.Scope),
            hasOldSource ? _keys.SourceDocs(request.Scope, current!.Source!) : docKey,
            hasNewSource ? _keys.SourceDocs(request.Scope, request.Source!) : docKey,
        };
        keys.AddRange(added.Select(t => _keys.Ref(request.Scope, t)));
        keys.AddRange(removed.Select(t => _keys.Ref(request.Scope, t)));
        keys.AddRange(aliases.Select(a => _keys.Aliases(request.Scope, a.Target)));
        keys.AddRange(join.Select(c => _keys.ChunkDocs(request.Scope, c)));
        keys.AddRange(leave.Select(c => _keys.ChunkDocs(request.Scope, c)));

        var json = JsonSerializer.Serialize(request.Entries.Select(e =>
            new EntryJson(e.Ordinal, e.Id.Value, e.Target.Value, e.Language, e.FirstRevision, e.PreviousOrdinal, e.Replaces?.Value)));
        var args = new List<RedisValue>
        {
            request.ExpectedRevision,
            0,
            request.At.UtcTicks,
            json,
            added.Count,
            removed.Count,
            aliases.Count,
            join.Count,
            leave.Count,
            request.Document.Value,
            request.Source ?? string.Empty,
            hasOldSource ? "1" : "0",
            hasNewSource ? "1" : "0",
        };
        args.AddRange(added.Select(t => (RedisValue)t));
        args.AddRange(removed.Select(t => (RedisValue)t));
        args.AddRange(aliases.Select(a => (RedisValue)a.Alias));

        var reply = (RedisResult[])(await Db.ScriptEvaluateAsync(CommitScript, keys.ToArray(), args.ToArray()).ConfigureAwait(false))!;
        var status = (int)reply[0] switch
        {
            0 => CommitRevisionStatus.Committed,
            1 => CommitRevisionStatus.Conflict,
            _ => CommitRevisionStatus.Blocked,
        };
        var list = ((RedisResult[])reply[2]!).Select(r => new ChunkId((string)r!)).ToList();
        return new CommitRevisionResult(
            status,
            (int)reply[1],
            status == CommitRevisionStatus.Committed ? list : Array.Empty<ChunkId>(),
            status == CommitRevisionStatus.Blocked ? list : Array.Empty<ChunkId>());
    }

    public async ValueTask<IReadOnlyList<ChunkId>> ListPendingAsync(string scope, DateTimeOffset now, int take, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        var ids = await Db.SortedSetRangeByScoreAsync(
            _keys.Pending(scope), double.NegativeInfinity, now.ToUnixTimeMilliseconds(), Exclude.None, Order.Ascending, 0, Math.Max(0, take))
            .ConfigureAwait(false);
        return ids.Select(i => new ChunkId((string)i!)).OrderBy(c => c.Value, StringComparer.Ordinal).ToList();
    }

    public async ValueTask<RetireClaim?> BeginRetireAsync(
        string scope, ChunkId chunk, string holder, TimeSpan lease, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(holder);
        ct.ThrowIfCancellationRequested();

        var reply = await Db.ScriptEvaluateAsync(
            BeginRetireScript,
            [_keys.Ref(scope, chunk.Value), _keys.Pending(scope), _keys.Aliases(scope, chunk.Value)],
            [holder, (now + lease).ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds(), chunk.Value]).ConfigureAwait(false);
        if (reply.IsNull)
        {
            return null;
        }

        var aliases = ((RedisResult[])reply!).Select(r => new ChunkId((string)r!)).OrderBy(c => c.Value, StringComparer.Ordinal).ToList();
        return new RetireClaim(chunk, aliases);
    }

    public async ValueTask<bool> FinishRetireAsync(string scope, ChunkId chunk, string holder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(holder);
        ct.ThrowIfCancellationRequested();
        var reply = await Db.ScriptEvaluateAsync(
            FinishRetireScript,
            [_keys.Ref(scope, chunk.Value), _keys.Pending(scope), _keys.Aliases(scope, chunk.Value), _keys.RefSet(scope)],
            [holder, chunk.Value]).ConfigureAwait(false);
        return (int)reply == 1;
    }

    public async ValueTask<ChunkRef?> GetRefAsync(string scope, ChunkId chunk, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        var fields = await Db.HashGetAsync(_keys.Ref(scope, chunk.Value), ["count", "state"]).ConfigureAwait(false);
        return fields[0].HasValue ? ToRef(chunk, fields) : null;
    }

    public async ValueTask<IReadOnlyList<ChunkRef>> ListRefsAsync(string scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        var db = Db;
        var members = await db.SetMembersAsync(_keys.RefSet(scope)).ConfigureAwait(false);
        var ids = members.Select(m => (string)m!).OrderBy(m => m, StringComparer.Ordinal).ToList();
        var reads = ids.Select(i => db.HashGetAsync(_keys.Ref(scope, i), ["count", "state"])).ToArray();
        await Task.WhenAll(reads).ConfigureAwait(false);

        var list = new List<ChunkRef>(ids.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            var fields = reads[i].Result;
            if (fields[0].HasValue)
            {
                list.Add(ToRef(new ChunkId(ids[i]), fields));
            }
        }

        return list;
    }

    public async ValueTask<IReadOnlyList<DocumentRef>> ListDocumentsAsync(
        string scope, string? source, string? afterDocument, int take, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        var key = source is null ? _keys.Docs(scope) : _keys.SourceDocs(scope, source);
        var names = await LexPageAsync(key, afterDocument, take).ConfigureAwait(false);
        return await RefsAsync(scope, names).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<DocumentRef>> ListDocumentsUsingAsync(
        string scope, IReadOnlyCollection<ChunkId> chunks, string? afterDocument, int take, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(chunks);
        ct.ThrowIfCancellationRequested();

        // the first few documents of the union are among the first few of each set
        var pages = await Task.WhenAll(chunks.Select(c => c.Value).Distinct(StringComparer.Ordinal)
            .Select(c => LexPageAsync(_keys.ChunkDocs(scope, c), afterDocument, take))).ConfigureAwait(false);
        var names = pages.SelectMany(p => p).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).Take(Math.Max(0, take)).ToList();
        return await RefsAsync(scope, names).ConfigureAwait(false);
    }

    private async Task<List<string>> LexPageAsync(RedisKey key, string? after, int take)
    {
        if (take <= 0)
        {
            return new List<string>();
        }

        // member names that start with a bracket or a plus or minus are fine, they are escaped by the client as bytes
        var min = after is null ? RedisValue.Null : (RedisValue)after;
        var values = await Db.SortedSetRangeByValueAsync(
            key, min, RedisValue.Null, after is null ? Exclude.None : Exclude.Start, Order.Ascending, 0, take).ConfigureAwait(false);
        return values.Select(v => (string)v!).ToList();
    }

    private async Task<IReadOnlyList<DocumentRef>> RefsAsync(string scope, List<string> names)
    {
        var db = Db;
        var reads = names.Select(n => db.HashGetAsync(_keys.Doc(scope, n), ["revision", "source"])).ToArray();
        await Task.WhenAll(reads).ConfigureAwait(false);
        var list = new List<DocumentRef>(names.Count);
        for (var i = 0; i < names.Count; i++)
        {
            var fields = reads[i].Result;
            if (fields[0].HasValue)
            {
                list.Add(new DocumentRef(
                    new DocumentId(names[i]),
                    int.Parse((string)fields[0]!, CultureInfo.InvariantCulture),
                    fields[1].HasValue ? (string)fields[1]! : null));
            }
        }

        return list;
    }

    private static ChunkRef ToRef(ChunkId chunk, RedisValue[] fields) => new(
        chunk,
        int.Parse((string)fields[0]!, CultureInfo.InvariantCulture),
        (ChunkRefState)int.Parse((string)fields[1]!, CultureInfo.InvariantCulture));
}
