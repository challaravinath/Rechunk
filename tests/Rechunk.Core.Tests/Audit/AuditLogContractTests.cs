using Rechunk.Core.Tests.Store;
using Rechunk.Storage.Redis;
using Rechunk.Storage.Sqlite;
using Xunit;

namespace Rechunk.Core.Tests.Audit;

public sealed class InMemoryAuditLogTests : AuditLogContract
{
    protected override IAuditLog CreateLog() => new InMemoryAuditLog();

    protected override IAuditOutbox CreateOutbox() => new InMemoryAuditOutbox();

    [Fact]
    public async Task The_oldest_events_are_dropped_when_the_limit_is_reached()
    {
        var log = new InMemoryAuditLog(limit: 5);
        for (var i = 0; i < 8; i++)
        {
            await log.AppendAsync(Event(chunk: "c" + i, seconds: i));
        }

        var read = await Query(log, new AuditQuery());

        Assert.Equal(5, read.Count);
        Assert.Equal("c3", read[0].Chunk.Value);
    }
}

public sealed class SqliteAuditLogTests : AuditLogContract
{
    private readonly TempSqlite _file = new();

    protected override IAuditLog CreateLog() => new SqliteAuditLog(_file.ConnectionString);

    protected override IAuditOutbox CreateOutbox() => new SqliteAuditOutbox(_file.ConnectionString);

    public override void Dispose() => _file.Dispose();

    [Fact]
    public async Task Two_log_objects_on_one_file_see_each_others_events()
    {
        await new SqliteAuditLog(_file.ConnectionString).AppendAsync(Event(chunk: "from-a"));
        var other = new SqliteAuditLog(_file.ConnectionString);

        Assert.Equal("from-a", Assert.Single(await Query(other, new AuditQuery())).Chunk.Value);
    }
}

public sealed class RedisAuditLogTests : AuditLogContract
{
    private RedisTestEnvironment? _redis;

    private RedisTestEnvironment Env => _redis ??= new RedisTestEnvironment();

    protected override IAuditLog CreateLog() => new RedisAuditLog(Env.Redis, new RedisStorageOptions { KeyPrefix = Env.Prefix });

    protected override IAuditOutbox CreateOutbox() => new RedisAuditOutbox(Env.Redis, new RedisStorageOptions { KeyPrefix = Env.Prefix });

    public override void Dispose() => _redis?.Dispose();
}
