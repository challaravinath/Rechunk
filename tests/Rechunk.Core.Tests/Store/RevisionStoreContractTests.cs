using Rechunk.Storage.Redis;
using Rechunk.Storage.Sqlite;

namespace Rechunk.Core.Tests.Store;

public sealed class InMemoryRevisionStoreTests : RevisionStoreContract
{
    private IRevisionStore? _store;

    // one process, so every worker in a test has to get the very same store
    protected override IRevisionStore Create() => _store ??= new InMemoryRevisionStore();
}

public sealed class SqliteRevisionStoreTests : RevisionStoreContract
{
    private readonly TempSqlite _file = new();

    protected override IRevisionStore Create() => new SqliteRevisionStore(_file.ConnectionString);

    public override void Dispose() => _file.Dispose();
}

public sealed class RedisRevisionStoreTests : RevisionStoreContract
{
    private RedisTestEnvironment? _redis;

    protected override IRevisionStore Create()
    {
        _redis ??= new RedisTestEnvironment();
        return new RedisRevisionStore(_redis.Redis, new RedisStorageOptions { KeyPrefix = _redis.Prefix });
    }

    public override void Dispose() => _redis?.Dispose();
}
