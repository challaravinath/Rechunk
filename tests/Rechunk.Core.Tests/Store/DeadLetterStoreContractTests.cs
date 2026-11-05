using Microsoft.Data.Sqlite;
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

    [Fact]
    public async Task A_database_from_before_the_language_column_is_upgraded_and_its_letters_still_read()
    {
        var connectionString = _file.ConnectionString;
        using (var old = new SqliteConnection(connectionString))
        {
            old.Open();
            using var create = old.CreateCommand();
            create.CommandText = """
                CREATE TABLE rechunk_dead_letter (
                    id        TEXT    NOT NULL PRIMARY KEY,
                    scope     TEXT    NOT NULL,
                    chunk     TEXT    NOT NULL,
                    document  TEXT,
                    ordinal   INTEGER NOT NULL,
                    stage     INTEGER NOT NULL,
                    reason    TEXT    NOT NULL,
                    attempts  INTEGER NOT NULL,
                    failed_at INTEGER NOT NULL,
                    body      TEXT
                );
                INSERT INTO rechunk_dead_letter VALUES ('old-1', 'tenant-a', 'chunk-1', 'doc-1', 2, 0, 'it failed', 3, 639000000000000000, NULL);
                """;
            create.ExecuteNonQuery();
        }

        var store = new SqliteDeadLetterStore(connectionString);

        var letter = await store.GetAsync("old-1");
        Assert.NotNull(letter);
        Assert.Null(letter!.Language);
        Assert.Equal("it failed", letter.Reason);

        await store.AddAsync(new DeadLetter(
            "new-1", "tenant-a", new ChunkId("chunk-2"), null, 0, DeadLetterStage.Embed, "again", 1,
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), null, "th"));
        Assert.Equal("th", (await store.GetAsync("new-1"))!.Language);
    }
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
