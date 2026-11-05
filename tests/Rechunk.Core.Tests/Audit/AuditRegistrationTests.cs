using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Rechunk.Core.Tests.Store;
using Rechunk.Storage.Sqlite;
using Xunit;

namespace Rechunk.Core.Tests.Audit;

public class AuditRegistrationTests
{
    private static ServiceProvider Provider(Action<IRechunkBuilder>? storage = null, Action<RechunkOptions>? configure = null)
    {
        var services = new ServiceCollection();
        var builder = services.AddRechunk(o =>
        {
            o.SimHash.Enabled = true;
            configure?.Invoke(o);
        });
        (storage ?? (b => b.UseInMemoryFingerprintStore()))(builder);
        services.AddSingleton<IChunkEmbedder>(new ScriptedEmbedder());
        services.AddSingleton<IVectorSink>(new RecordingSink());
        return services.BuildServiceProvider();
    }

    private static List<ChunkInput> Doc(params int[] seeds) =>
        seeds.Select((seed, i) => new ChunkInput(i, string.Join(' ', TestText.Words(seed, 150)))).ToList();

    private static async Task<List<AuditEvent>> All(IAuditLog log, AuditQuery? query = null)
    {
        var list = new List<AuditEvent>();
        await foreach (var e in log.QueryAsync(query ?? new AuditQuery()))
        {
            list.Add(e);
        }

        return list;
    }

    [Fact]
    public async Task The_audit_log_is_registered_by_default_and_the_tracker_writes_to_it()
    {
        await using var provider = Provider();
        var tracker = provider.GetRequiredService<RevisionTracker>();

        Assert.IsType<InMemoryAuditLog>(provider.GetRequiredService<IAuditLog>());
        Assert.IsType<InMemoryAuditOutbox>(provider.GetRequiredService<IAuditOutbox>());
        Assert.NotNull(provider.GetRequiredService<AuditWriter>());
        Assert.True(tracker.Audited);

        await tracker.ReviseAsync(
            new DocumentId("d"), "t", Doc(1, 2), provider.GetRequiredService<IChunkEmbedder>(), provider.GetRequiredService<IVectorSink>(),
            source: "wiki");

        var events = await All(provider.GetRequiredService<IAuditLog>());
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal("wiki", e.Source));
    }

    [Fact]
    public async Task The_ingestor_writes_to_the_same_audit_log()
    {
        await using var provider = Provider();
        var ingestor = provider.GetRequiredService<ChunkIngestor>();

        await ingestor.IngestAsync(new IngestRequest("t", new DocumentId("d"), Doc(1), "m1"));
        await ingestor.IngestAsync(new IngestRequest("t", new DocumentId("e"), Doc(1), "m2"));

        var events = await All(provider.GetRequiredService<IAuditLog>());
        Assert.Equal(new[] { AuditKind.New, AuditKind.Skipped }.Order(), events.Select(e => e.Kind).Order());
    }

    [Fact]
    public async Task A_log_that_the_application_registers_wins_over_the_default()
    {
        var services = new ServiceCollection();
        var mine = new InMemoryAuditLog();
        services.AddSingleton<IAuditLog>(mine);
        services.AddRechunk().UseInMemoryFingerprintStore();
        services.AddSingleton<IChunkEmbedder>(new ScriptedEmbedder());
        services.AddSingleton<IVectorSink>(new RecordingSink());
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<RevisionTracker>().ReviseAsync(
            new DocumentId("d"), "t", Doc(1), provider.GetRequiredService<IChunkEmbedder>(), provider.GetRequiredService<IVectorSink>());

        Assert.Same(mine, provider.GetRequiredService<IAuditLog>());
        Assert.Single(mine.Events);
    }

    [Fact]
    public async Task Sqlite_storage_keeps_the_audit_trail_between_containers()
    {
        using var file = new TempSqlite();
        var sink = new RecordingSink();

        await using (var first = Provider(b => b.AddSqliteStorage(file.ConnectionString)))
        {
            Assert.IsType<SqliteAuditLog>(first.GetRequiredService<IAuditLog>());
            Assert.IsType<SqliteAuditOutbox>(first.GetRequiredService<IAuditOutbox>());
            await first.GetRequiredService<RevisionTracker>().ReviseAsync(
                new DocumentId("d"), "t", Doc(1, 2), first.GetRequiredService<IChunkEmbedder>(), sink, source: "wiki");
        }

        await using var second = Provider(b => b.AddSqliteStorage(file.ConnectionString));
        var tracker = second.GetRequiredService<RevisionTracker>();
        var before = await All(second.GetRequiredService<IAuditLog>(), new AuditQuery { Source = "wiki" });
        Assert.Equal(2, before.Count);

        var result = await tracker.EraseAsync(new EraseRequest("t", null, "wiki", "customer left", "support"), sink);

        Assert.Equal(1, result.Documents);
        Assert.Empty(sink.StoredIds);
        var kinds = (await All(second.GetRequiredService<IAuditLog>())).Select(e => e.Kind).ToList();
        Assert.Equal(2, kinds.Count(k => k == AuditKind.Erased));
        Assert.Single(kinds, k => k == AuditKind.ErasureReceipt);
    }

    [Fact]
    public async Task A_database_from_before_sources_gets_the_column_and_keeps_its_documents()
    {
        using var file = new TempSqlite();
        await using (var connection = new SqliteConnection(file.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE rechunk_doc (
                    scope TEXT NOT NULL, document TEXT NOT NULL, revision INTEGER NOT NULL, updated_at INTEGER NOT NULL,
                    PRIMARY KEY (scope, document)
                ) WITHOUT ROWID;
                INSERT INTO rechunk_doc VALUES ('t', 'old-doc', 3, 1700000000000);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var store = new SqliteRevisionStore(new SqliteStorageOptions { ConnectionString = file.ConnectionString });

        var old = await store.GetAsync("t", new DocumentId("old-doc"));
        Assert.Equal(3, old!.Revision);
        Assert.Null(old.Source);
        var listed = Assert.Single(await store.ListDocumentsAsync("t", null, null, 10));
        Assert.Null(listed.Source);
        Assert.Empty(await store.ListDocumentsAsync("t", "wiki", null, 10));

        var commit = await store.CommitAsync(new CommitRevisionRequest(
            "t", new DocumentId("old-doc"), 3, Array.Empty<ManifestEntry>(), DateTimeOffset.UtcNow, "wiki"));

        Assert.Equal(CommitRevisionStatus.Committed, commit.Status);
        Assert.Equal("wiki", Assert.Single(await store.ListDocumentsAsync("t", "wiki", null, 10)).Source);

        // opening it a second time must not try to add the column again
        _ = new SqliteRevisionStore(new SqliteStorageOptions { ConnectionString = file.ConnectionString });
    }

    [Fact]
    public async Task PII_scanning_is_off_unless_it_is_turned_on()
    {
        await using var off = Provider();
        await using var on = Provider(configure: o => o.Pii.Enabled = true);
        var text = new[] { new ChunkInput(0, string.Join(' ', TestText.Words(1, 100)) + " card 4111 1111 1111 1111") };

        await off.GetRequiredService<RevisionTracker>().ReviseAsync(new DocumentId("d"), "t", text, off.GetRequiredService<IChunkEmbedder>(), off.GetRequiredService<IVectorSink>());
        await on.GetRequiredService<RevisionTracker>().ReviseAsync(new DocumentId("d"), "t", text, on.GetRequiredService<IChunkEmbedder>(), on.GetRequiredService<IVectorSink>());

        Assert.Empty(Assert.Single(await All(off.GetRequiredService<IAuditLog>())).Findings);
        Assert.Equal(PiiType.Card, Assert.Single(Assert.Single(await All(on.GetRequiredService<IAuditLog>())).Findings).Type);
    }
}
