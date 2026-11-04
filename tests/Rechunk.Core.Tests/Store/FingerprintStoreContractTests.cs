using Rechunk.Storage.Redis;
using Rechunk.Storage.Sqlite;

namespace Rechunk.Core.Tests.Store;

public sealed class InMemoryFingerprintStoreTests : FingerprintStoreContract
{
    private IFingerprintStore? _store;

    // one process, so every worker in a test has to get the very same store
    protected override IFingerprintStore Create(RechunkOptions options, TimeProvider time) =>
        _store ??= new InMemoryFingerprintStore(options, time);
}

public sealed class SqliteFingerprintStoreTests : FingerprintStoreContract
{
    private readonly TempSqlite _file = new();

    protected override IFingerprintStore Create(RechunkOptions options, TimeProvider time) =>
        new BackedFingerprintStore(new SqliteFingerprintBackend(_file.ConnectionString), options, time);

    public override void Dispose() => _file.Dispose();
}

public sealed class RedisFingerprintStoreTests : FingerprintStoreContract
{
    private RedisTestEnvironment? _redis;

    protected override IFingerprintStore Create(RechunkOptions options, TimeProvider time)
    {
        _redis ??= new RedisTestEnvironment();
        return new BackedFingerprintStore(
            new RedisFingerprintBackend(_redis.Redis, new RedisStorageOptions { KeyPrefix = _redis.Prefix }), options, time);
    }

    public override void Dispose() => _redis?.Dispose();
}
