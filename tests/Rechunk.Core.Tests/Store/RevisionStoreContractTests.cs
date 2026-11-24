using Rechunk.Storage.Redis;
using Rechunk.Storage.Sqlite;

namespace Rechunk.Core.Tests.Store;

public sealed class InMemoryRevisionStoreTests : RevisionStoreContract
{
    private readonly object _gate = new();
    private IRevisionStore? _store;

    // one process, so every worker in a test has to get the very same store.
    // The race tests call Create from several threads at once, so the first assignment has to be locked.
    protected override IRevisionStore Create()
    {
        lock (_gate)
        {
            return _store ??= new InMemoryRevisionStore();
        }
    }
}

public sealed class SqliteRevisionStoreTests : RevisionStoreContract
{
    private readonly TempSqlite _file = new();

    protected override IRevisionStore Create() => new SqliteRevisionStore(_file.ConnectionString);

    public override void Dispose() => _file.Dispose();
}

public sealed class RedisRevisionStoreTests : RevisionStoreContract
{
    // The race tests call Create on several threads before any store exists, so the prefix is chosen once.
    private readonly Lazy<RedisTestEnvironment> _redis = new(() => new RedisTestEnvironment());

    protected override IRevisionStore Create()
    {
        var env = _redis.Value;
        return new RedisRevisionStore(env.Redis, new RedisStorageOptions { KeyPrefix = env.Prefix });
    }

    public override void Dispose()
    {
        if (_redis.IsValueCreated)
        {
            _redis.Value.Dispose();
        }
    }
}
