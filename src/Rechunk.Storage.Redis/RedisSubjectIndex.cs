using StackExchange.Redis;

namespace Rechunk.Storage.Redis;

/// <summary>
/// Subject links in Redis. One set per chunk holds kind and keyed id. One set per subject id holds the chunk ids.
/// The value the id was made from is not stored. Keys for one scope share a hash tag.
/// </summary>
public sealed class RedisSubjectIndex : ISubjectIndex
{
    private const string ReplaceScript = """
        local old = redis.call('SMEMBERS', KEYS[1])
        for i = 1, #old do
          local tab = string.find(old[i], '\t', 1, true)
          if tab then
            redis.call('SREM', ARGV[2] .. string.sub(old[i], tab + 1), ARGV[1])
          end
        end
        redis.call('DEL', KEYS[1])
        local added = 0
        for i = 3, #ARGV do
          if redis.call('SADD', KEYS[1], ARGV[i]) == 1 then
            added = added + 1
          end
          local tab = string.find(ARGV[i], '\t', 1, true)
          redis.call('SADD', ARGV[2] .. string.sub(ARGV[i], tab + 1), ARGV[1])
        end
        if added ~= 0 then
          redis.call('INCRBY', KEYS[2], added - #old)
        elseif #old ~= 0 then
          redis.call('INCRBY', KEYS[2], -#old)
        end
        return added
        """;

    private const string AddScript = """
        local added = 0
        for i = 3, #ARGV do
          if redis.call('SADD', KEYS[1], ARGV[i]) == 1 then
            added = added + 1
            local tab = string.find(ARGV[i], '\t', 1, true)
            redis.call('SADD', ARGV[2] .. string.sub(ARGV[i], tab + 1), ARGV[1])
          end
        end
        if added ~= 0 then
          redis.call('INCRBY', KEYS[2], added)
        end
        return added
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly int _database;
    private readonly RedisKeys _keys;

    public RedisSubjectIndex(IConnectionMultiplexer redis, RedisStorageOptions? options = null)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        options ??= new RedisStorageOptions();
        _database = options.Database;
        _keys = new RedisKeys(options.KeyPrefix);
    }

    private IDatabase Db => _redis.GetDatabase(_database);

    public async ValueTask LinkAsync(string scope, ChunkId chunk, IReadOnlyList<SubjectKey> subjects, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Require(scope, chunk);
        await Db.ScriptEvaluateAsync(ReplaceScript, Keys(scope, chunk), Args(scope, chunk, subjects)).ConfigureAwait(false);
    }

    public async ValueTask AddAsync(string scope, ChunkId chunk, IReadOnlyList<SubjectKey> subjects, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Require(scope, chunk);
        if (subjects is null || subjects.Count == 0)
        {
            return;
        }

        await Db.ScriptEvaluateAsync(AddScript, Keys(scope, chunk), Args(scope, chunk, subjects)).ConfigureAwait(false);
    }

    public ValueTask RemoveAsync(string scope, ChunkId chunk, CancellationToken ct = default) =>
        LinkAsync(scope, chunk, Array.Empty<SubjectKey>(), ct);

    public async ValueTask<int> CountLinksAsync(string scope, ChunkId chunk, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Require(scope, chunk);
        return (int)await Db.SetLengthAsync(_keys.ChunkSubjects(scope, chunk.Value)).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<ChunkId>> ListChunksAsync(string scope, SubjectKey subject, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RequireScope(scope);
        var values = await Db.SetMembersAsync(_keys.SubjectChunks(scope, subject.Id)).ConfigureAwait(false);
        var list = values.Select(v => new ChunkId((string)v!)).OrderBy(id => id.Value, StringComparer.Ordinal).ToList();
        return list;
    }

    public async ValueTask<int> CountAsync(string scope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RequireScope(scope);
        var value = await Db.StringGetAsync(_keys.SubjectCount(scope)).ConfigureAwait(false);
        return value.HasValue ? (int)value : 0;
    }

    public async ValueTask<int> CountAsync(string scope, SubjectKey subject, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RequireScope(scope);
        return (int)await Db.SetLengthAsync(_keys.SubjectChunks(scope, subject.Id)).ConfigureAwait(false);
    }

    private RedisKey[] Keys(string scope, ChunkId chunk) =>
        [_keys.ChunkSubjects(scope, chunk.Value), _keys.SubjectCount(scope)];

    private RedisValue[] Args(string scope, ChunkId chunk, IReadOnlyList<SubjectKey>? subjects)
    {
        var args = new List<RedisValue> { chunk.Value, _keys.SubjectChunkPrefix(scope) };
        if (subjects is not null)
        {
            foreach (var subject in subjects)
            {
                args.Add(subject.Kind + "\t" + subject.Id);
            }
        }

        return args.ToArray();
    }

    private static void Require(string scope, ChunkId chunk)
    {
        RequireScope(scope);
        if (string.IsNullOrEmpty(chunk.Value))
        {
            throw new ArgumentException("Chunk id cannot be empty.", nameof(chunk));
        }
    }

    private static void RequireScope(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException("Scope cannot be empty.", nameof(scope));
        }
    }
}
