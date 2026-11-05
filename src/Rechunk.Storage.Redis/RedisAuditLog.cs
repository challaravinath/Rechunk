using StackExchange.Redis;

namespace Rechunk.Storage.Redis;

/// <summary>
/// The audit log in Redis. An event is a string key with its JSON (no chunk text is in it), and its id goes into sorted
/// sets scored by time: one for everything, one for the scope, and one each for its document, chunk, source and finding
/// types. An append is one script that does nothing when the event id exists, so writing an event twice stores it once.
/// A query reads the smallest set that is sure to hold every match, in time order, and filters the events it reads.
/// Redis orders ids with the same millisecond by name, which is the tie break the other logs use too.
/// </summary>
public sealed class RedisAuditLog : IAuditLog
{
    private const int Batch = 500;

    // KEYS: 1 event, then the index sets. ARGV: 1 json, 2 time in ms, 3 event id.
    private const string AppendScript = """
        if not redis.call('SET', KEYS[1], ARGV[1], 'NX') then
          return 0
        end
        for i = 2, #KEYS do
          redis.call('ZADD', KEYS[i], ARGV[2], ARGV[3])
        end
        return 1
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly int _database;
    private readonly RedisKeys _keys;

    public RedisAuditLog(IConnectionMultiplexer redis, RedisStorageOptions? options = null)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        options ??= new RedisStorageOptions();
        _database = options.Database;
        _keys = new RedisKeys(options.KeyPrefix);
    }

    private IDatabase Db => _redis.GetDatabase(_database);

    public async ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        ct.ThrowIfCancellationRequested();
        var keys = new List<RedisKey> { _keys.AuditEvent(auditEvent.Id), _keys.AuditAll };
        if (auditEvent.Scope.Length > 0)
        {
            keys.Add(_keys.AuditScope(auditEvent.Scope));
        }

        if (!string.IsNullOrEmpty(auditEvent.Document.Value))
        {
            keys.Add(_keys.AuditDocument(auditEvent.Document.Value));
        }

        if (!string.IsNullOrEmpty(auditEvent.Chunk.Value))
        {
            keys.Add(_keys.AuditChunk(auditEvent.Chunk.Value));
        }

        if (auditEvent.Source.Length > 0)
        {
            keys.Add(_keys.AuditSource(auditEvent.Source));
        }

        keys.AddRange(auditEvent.Findings.Select(f => f.Type.Name).Distinct(StringComparer.Ordinal).Select(t => _keys.AuditFinding(t)));
        await Db.ScriptEvaluateAsync(
            AppendScript,
            keys.ToArray(),
            [AuditEventJson.Serialize(auditEvent), AuditQuery.Ms(auditEvent.At), auditEvent.Id]).ConfigureAwait(false);
    }

    public async ValueTask AppendManyAsync(IReadOnlyList<AuditEvent> events, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var e in events)
        {
            await AppendAsync(e, ct).ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<AuditEvent> QueryAsync(
        AuditQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var db = Db;
        var index = Index(query);
        double min = query.From is { } from ? AuditQuery.Ms(from) : double.NegativeInfinity;
        double max = query.To is { } to ? AuditQuery.Ms(to) : double.PositiveInfinity;

        var skip = Math.Max(0, query.Skip);
        var left = query.Take ?? int.MaxValue;
        long offset = 0;
        while (left > 0)
        {
            ct.ThrowIfCancellationRequested();
            var ids = await db.SortedSetRangeByScoreAsync(index, min, max, Exclude.None, Order.Ascending, offset, Batch).ConfigureAwait(false);
            if (ids.Length == 0)
            {
                yield break;
            }

            offset += ids.Length;
            var bodies = await db.StringGetAsync(ids.Select(i => _keys.AuditEvent((string)i!)).ToArray()).ConfigureAwait(false);
            foreach (var body in bodies)
            {
                if (!body.HasValue)
                {
                    continue;
                }

                var e = AuditEventJson.Deserialize((string)body!);
                if (!query.Matches(e))
                {
                    continue;
                }

                if (skip > 0)
                {
                    skip--;
                    continue;
                }

                yield return e;
                if (--left == 0)
                {
                    yield break;
                }
            }

            if (ids.Length < Batch)
            {
                yield break;
            }
        }
    }

    // The most selective set that holds every event the query can match. Events with an empty field are not in that
    // field's set, so a query for the empty value falls back to a wider one.
    private RedisKey Index(AuditQuery query)
    {
        if (query.Chunk is { Value: { Length: > 0 } chunk })
        {
            return _keys.AuditChunk(chunk);
        }

        if (query.Document is { Value: { Length: > 0 } document })
        {
            return _keys.AuditDocument(document);
        }

        if (query.FindingType is { } type)
        {
            return _keys.AuditFinding(type.Name);
        }

        if (!string.IsNullOrEmpty(query.Source))
        {
            return _keys.AuditSource(query.Source);
        }

        if (!string.IsNullOrEmpty(query.Scope))
        {
            return _keys.AuditScope(query.Scope);
        }

        return _keys.AuditAll;
    }
}

/// <summary>The audit outbox in Redis: a hash of the events by id and a queue that keeps their order.</summary>
public sealed class RedisAuditOutbox : IAuditOutbox
{
    // KEYS: 1 hash, 2 queue, 3 counter. ARGV: id, json, id, json ...
    private const string EnqueueScript = """
        for i = 1, #ARGV, 2 do
          if redis.call('HSETNX', KEYS[1], ARGV[i], ARGV[i + 1]) == 1 then
            redis.call('ZADD', KEYS[2], redis.call('INCR', KEYS[3]), ARGV[i])
          end
        end
        return 1
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly int _database;
    private readonly RedisKeys _keys;

    public RedisAuditOutbox(IConnectionMultiplexer redis, RedisStorageOptions? options = null)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        options ??= new RedisStorageOptions();
        _database = options.Database;
        _keys = new RedisKeys(options.KeyPrefix);
    }

    private IDatabase Db => _redis.GetDatabase(_database);

    public async ValueTask EnqueueAsync(IReadOnlyList<AuditEvent> events, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ct.ThrowIfCancellationRequested();
        if (events.Count == 0)
        {
            return;
        }

        var args = new List<RedisValue>(events.Count * 2);
        foreach (var e in events)
        {
            args.Add(e.Id);
            args.Add(AuditEventJson.Serialize(e));
        }

        await Db.ScriptEvaluateAsync(
            EnqueueScript, [_keys.AuditOutbox, _keys.AuditOutboxQueue, _keys.AuditOutboxSeq], args.ToArray()).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<AuditEvent>> PeekAsync(int max, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (max <= 0)
        {
            return Array.Empty<AuditEvent>();
        }

        var db = Db;
        var ids = await db.SortedSetRangeByRankAsync(_keys.AuditOutboxQueue, 0, max - 1).ConfigureAwait(false);
        if (ids.Length == 0)
        {
            return Array.Empty<AuditEvent>();
        }

        var bodies = await db.HashGetAsync(_keys.AuditOutbox, ids).ConfigureAwait(false);
        return bodies.Where(b => b.HasValue).Select(b => AuditEventJson.Deserialize((string)b!)).ToList();
    }

    public async ValueTask RemoveAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ct.ThrowIfCancellationRequested();
        if (ids.Count == 0)
        {
            return;
        }

        var values = ids.Select(i => (RedisValue)i).ToArray();
        var db = Db;
        await db.SortedSetRemoveAsync(_keys.AuditOutboxQueue, values).ConfigureAwait(false);
        await db.HashDeleteAsync(_keys.AuditOutbox, values).ConfigureAwait(false);
    }

    public async ValueTask<int> CountAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return (int)await Db.SortedSetLengthAsync(_keys.AuditOutboxQueue).ConfigureAwait(false);
    }
}
