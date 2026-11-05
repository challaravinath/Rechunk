using Rechunk.Storage.Redis;
using Rechunk.Storage.Sqlite;

namespace Rechunk.Core.Tests.Store;

public sealed class InMemoryDeadLetterStoreTests : DeadLetterStoreContract
{
    protected override IDeadLetterStore Create() => new InMemoryDeadLetterStore();
}

public sealed class SqliteDeadLetterStoreTests : DeadLetterStoreContract
{
    private readonly TempSqlite _file = new();

    protected override IDeadLetterStore Create() => new SqliteDeadLetterStore(_file.ConnectionString);

    public override void Dispose() => _file.Dispose();
}

public sealed class RedisDeadLetterStoreTests : DeadLetterStoreContract
{
    private RedisTestEnvironment? _redis;

    protected override IDeadLetterStore Create()
    {
        _redis ??= new RedisTestEnvironment();
        return new RedisDeadLetterStore(_redis.Redis, new RedisStorageOptions { KeyPrefix = _redis.Prefix });
    }

    public override void Dispose() => _redis?.Dispose();
}
