using Rechunk.Storage.Redis;
using Rechunk.Storage.Sqlite;

namespace Rechunk.Core.Tests.Store;

public sealed class InMemoryRevisionTrackerTests : RevisionTrackerContract
{
    private WorkerStores? _stores;

    protected override WorkerStores CreateStores(RechunkOptions options, TimeProvider time) =>
        _stores ??= new WorkerStores(new InMemoryFingerprintStore(options, time), new InMemoryRevisionStore(), new InMemoryDeadLetterStore());
}

public sealed class SqliteRevisionTrackerTests : RevisionTrackerContract
{
    private readonly TempSqlite _file = new();

    protected override WorkerStores CreateStores(RechunkOptions options, TimeProvider time) => new(
        new BackedFingerprintStore(new SqliteFingerprintBackend(_file.ConnectionString), options, time),
        new SqliteRevisionStore(_file.ConnectionString),
        new SqliteDeadLetterStore(_file.ConnectionString));

    public override void Dispose() => _file.Dispose();
}

public sealed class RedisRevisionTrackerTests : RevisionTrackerContract
{
    private RedisTestEnvironment? _redis;

    protected override WorkerStores CreateStores(RechunkOptions options, TimeProvider time)
    {
        _redis ??= new RedisTestEnvironment();
        var storage = new RedisStorageOptions { KeyPrefix = _redis.Prefix };
        return new WorkerStores(
            new BackedFingerprintStore(new RedisFingerprintBackend(_redis.Redis, storage), options, time),
            new RedisRevisionStore(_redis.Redis, storage),
            new RedisDeadLetterStore(_redis.Redis, storage));
    }

    public override void Dispose() => _redis?.Dispose();
}
