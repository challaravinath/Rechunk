using System.Globalization;
using StackExchange.Redis;

namespace Rechunk.Storage.Redis;

/// <summary>
/// Dead letters in Redis. Every record is a hash, and sorted sets (by time of failure) keep them in order, one for all
/// records and one for each scope. Listing by scope is cheap. Filtering by stage, chunk or document reads the
/// records of the page range and filters them here, which is fine for the sizes a dead letter queue should have.
/// </summary>
public sealed class RedisDeadLetterStore : IDeadLetterStore
{
    private const int PageSize = 200;

    private const string AddScript = """
        redis.call('DEL', KEYS[1])
        redis.call('HSET', KEYS[1], unpack(ARGV, 3))
        redis.call('ZADD', KEYS[2], ARGV[2], ARGV[1])
        redis.call('ZADD', KEYS[3], ARGV[2], ARGV[1])
        if KEYS[4] ~= KEYS[3] then
          redis.call('ZREM', KEYS[4], ARGV[1])
        end
        return 1
        """;

    private const string RemoveScript = """
        local removed = redis.call('DEL', KEYS[1])
        redis.call('ZREM', KEYS[2], ARGV[1])
        redis.call('ZREM', KEYS[3], ARGV[1])
        return removed
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly int _database;
    private readonly RedisKeys _keys;

    public RedisDeadLetterStore(IConnectionMultiplexer redis, RedisStorageOptions? options = null)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        options ??= new RedisStorageOptions();
        _database = options.Database;
        _keys = new RedisKeys(options.KeyPrefix);
    }

    private IDatabase Db => _redis.GetDatabase(_database);

    public async ValueTask AddAsync(DeadLetter deadLetter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deadLetter);
        ct.ThrowIfCancellationRequested();

        var db = Db;
        var record = _keys.DeadLetter(deadLetter.Id);
        var oldScope = await db.HashGetAsync(record, "scope").ConfigureAwait(false);
        var oldScopeKey = oldScope.HasValue ? _keys.DeadLetterScope((string)oldScope!) : _keys.DeadLetterScope(deadLetter.Scope);

        var args = new List<RedisValue>
        {
            deadLetter.Id,
            deadLetter.FailedAt.ToUnixTimeMilliseconds(),
            "scope", deadLetter.Scope,
            "chunk", deadLetter.Chunk.Value,
            "ordinal", deadLetter.Ordinal,
            "stage", (int)deadLetter.Stage,
            "reason", deadLetter.Reason,
            "attempts", deadLetter.Attempts,
            "failed", deadLetter.FailedAt.UtcTicks,
        };
        if (deadLetter.Document is { } document)
        {
            args.Add("document");
            args.Add(document.Value);
        }

        if (deadLetter.Text is not null)
        {
            args.Add("body");
            args.Add(deadLetter.Text);
        }

        await db.ScriptEvaluateAsync(
            AddScript,
            [record, _keys.DeadLetterAll, _keys.DeadLetterScope(deadLetter.Scope), oldScopeKey],
            args.ToArray()).ConfigureAwait(false);
    }

    public async ValueTask<DeadLetter?> GetAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var fields = await Db.HashGetAllAsync(_keys.DeadLetter(id)).ConfigureAwait(false);
        return fields.Length == 0 ? null : Map(id, fields);
    }

    public async ValueTask<IReadOnlyList<DeadLetter>> ListAsync(DeadLetterQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var skip = Math.Max(0, query.Skip);
        var take = Math.Max(0, query.Take);
        var page = new List<DeadLetter>();
        if (take == 0)
        {
            return page;
        }

        var seen = 0;
        await foreach (var item in ScanAsync(query, ct).ConfigureAwait(false))
        {
            if (seen++ < skip)
            {
                continue;
            }

            page.Add(item);
            if (page.Count >= take)
            {
                break;
            }
        }

        return page;
    }

    public async ValueTask<long> CountAsync(DeadLetterQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Stage is null && query.Chunk is null && query.Document is null)
        {
            var set = query.Scope is null ? _keys.DeadLetterAll : _keys.DeadLetterScope(query.Scope);
            return await Db.SortedSetLengthAsync(set).ConfigureAwait(false);
        }

        long count = 0;
        await foreach (var _ in ScanAsync(query, ct).ConfigureAwait(false))
        {
            count++;
        }

        return count;
    }

    public async ValueTask<bool> RemoveAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var db = Db;
        var record = _keys.DeadLetter(id);
        var scope = await db.HashGetAsync(record, "scope").ConfigureAwait(false);
        var scopeKey = scope.HasValue ? _keys.DeadLetterScope((string)scope!) : _keys.DeadLetterAll;

        var removed = await db.ScriptEvaluateAsync(RemoveScript, [record, _keys.DeadLetterAll, scopeKey], [id]).ConfigureAwait(false);
        return (int)removed > 0;
    }

    private async IAsyncEnumerable<DeadLetter> ScanAsync(
        DeadLetterQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var db = Db;
        var set = query.Scope is null ? _keys.DeadLetterAll : _keys.DeadLetterScope(query.Scope);

        for (long start = 0; ; start += PageSize)
        {
            ct.ThrowIfCancellationRequested();
            var ids = await db.SortedSetRangeByRankAsync(set, start, start + PageSize - 1).ConfigureAwait(false);
            if (ids.Length == 0)
            {
                yield break;
            }

            var reads = ids.Select(i => db.HashGetAllAsync(_keys.DeadLetter((string)i!))).ToArray();
            await Task.WhenAll(reads).ConfigureAwait(false);

            for (var i = 0; i < ids.Length; i++)
            {
                var fields = reads[i].Result;
                if (fields.Length == 0)
                {
                    continue;
                }

                var item = Map((string)ids[i]!, fields);
                if ((query.Stage is null || item.Stage == query.Stage)
                    && (query.Chunk is null || item.Chunk == query.Chunk)
                    && (query.Document is null || item.Document == query.Document))
                {
                    yield return item;
                }
            }

            if (ids.Length < PageSize)
            {
                yield break;
            }
        }
    }

    private static DeadLetter Map(string id, HashEntry[] fields)
    {
        string? Field(string name) => fields.FirstOrDefault(f => f.Name == name).Value is { HasValue: true } v ? (string?)v : null;
        int Int(string name) => int.Parse(Field(name)!, CultureInfo.InvariantCulture);

        var document = Field("document");
        return new DeadLetter(
            id,
            Field("scope")!,
            new ChunkId(Field("chunk")!),
            document is null ? null : new DocumentId(document),
            Int("ordinal"),
            (DeadLetterStage)Int("stage"),
            Field("reason")!,
            Int("attempts"),
            new DateTimeOffset(long.Parse(Field("failed")!, CultureInfo.InvariantCulture), TimeSpan.Zero),
            Field("body"));
    }
}
