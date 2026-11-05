using Microsoft.Extensions.DependencyInjection;
using Rechunk.Storage.Redis;
using Rechunk.Storage.Sqlite;

namespace Rechunk.Core.Tests.Store;

public class StoreRegistrationTests
{
    private static ServiceCollection Services(Action<IRechunkBuilder> storage)
    {
        var services = new ServiceCollection();
        storage(services.AddRechunk(o =>
        {
            o.SimHash.Enabled = true;
            o.MinHash.Enabled = true;
        }));
        services.AddSingleton<IChunkEmbedder>(new ScriptedEmbedder());
        services.AddSingleton<IVectorSink>(new RecordingSink());
        return services;
    }

    [Fact]
    public async Task The_in_memory_store_is_shared_by_the_pipeline_and_the_ingestor()
    {
        await using var provider = Services(b => b.UseInMemoryFingerprintStore()).BuildServiceProvider();

        var ingestor = provider.GetRequiredService<ChunkIngestor>();
        var request = new IngestRequest("t", new DocumentId("d"), new[] { new ChunkInput(0, string.Join(' ', TestText.Words(1, 100))) }, "m1");

        Assert.Equal(1, (await ingestor.IngestAsync(request)).Embedded);
        Assert.Equal(1, (await ingestor.IngestAsync(request with { Holder = "m2" })).Duplicates);
        Assert.IsType<InMemoryDeadLetterStore>(provider.GetRequiredService<IDeadLetterStore>());
    }

    [Fact]
    public async Task Sqlite_storage_replaces_the_in_memory_defaults_and_is_shared_between_containers()
    {
        using var file = new TempSqlite();
        var request = new IngestRequest("t", new DocumentId("d"), new[] { new ChunkInput(0, string.Join(' ', TestText.Words(1, 100))) }, "m1");

        await using (var first = Services(b => b.AddSqliteStorage(file.ConnectionString)).BuildServiceProvider())
        {
            Assert.IsType<SqliteDeadLetterStore>(first.GetRequiredService<IDeadLetterStore>());
            Assert.Equal(1, (await first.GetRequiredService<ChunkIngestor>().IngestAsync(request)).Embedded);
        }

        // a second container stands in for a second worker or a restart
        await using var second = Services(b => b.AddSqliteStorage(file.ConnectionString)).BuildServiceProvider();
        var again = await second.GetRequiredService<ChunkIngestor>().IngestAsync(request with { Holder = "m2" });
        Assert.Equal(1, again.Duplicates);
        Assert.Equal(0, again.Embedded);
    }

    private static List<ChunkInput> Doc(params int[] seeds) =>
        seeds.Select((seed, i) => new ChunkInput(i, string.Join(' ', TestText.Words(seed, 150)))).ToList();

    [Fact]
    public async Task The_revision_tracker_comes_from_the_container_with_the_in_memory_revision_store_by_default()
    {
        await using var provider = Services(b => b.UseInMemoryFingerprintStore()).BuildServiceProvider();
        var tracker = provider.GetRequiredService<RevisionTracker>();
        var sink = provider.GetRequiredService<IVectorSink>();
        var embedder = provider.GetRequiredService<IChunkEmbedder>();

        Assert.IsType<InMemoryRevisionStore>(provider.GetRequiredService<IRevisionStore>());

        var first = await tracker.ReviseAsync(new DocumentId("d"), "t", Doc(1, 2), embedder, sink);
        var second = await tracker.ReviseAsync(new DocumentId("d"), "t", Doc(1, 3), embedder, sink);

        Assert.Equal(2, first.Embedded);
        Assert.Equal(1, second.Embedded);
        Assert.Equal(1, second.Kept);
        Assert.Equal(1, second.Deleted);
    }

    [Fact]
    public async Task Sqlite_storage_carries_the_manifest_between_containers()
    {
        using var file = new TempSqlite();
        var sink = new RecordingSink();

        await using (var first = Services(b => b.AddSqliteStorage(file.ConnectionString)).BuildServiceProvider())
        {
            Assert.IsType<SqliteRevisionStore>(first.GetRequiredService<IRevisionStore>());
            var result = await first.GetRequiredService<RevisionTracker>()
                .ReviseAsync(new DocumentId("d"), "t", Doc(1, 2), first.GetRequiredService<IChunkEmbedder>(), sink);
            Assert.Equal(RevisionStatus.Applied, result.Status);
        }

        // a second container stands in for another stateless worker
        await using var second = Services(b => b.AddSqliteStorage(file.ConnectionString)).BuildServiceProvider();
        var tracker = second.GetRequiredService<RevisionTracker>();
        var manifest = await tracker.GetManifestAsync("t", new DocumentId("d"));
        Assert.Equal(1, manifest!.Revision);

        var again = await tracker.ReviseAsync(new DocumentId("d"), "t", Doc(1), second.GetRequiredService<IChunkEmbedder>(), sink);
        Assert.Equal(0, again.Embedded);
        Assert.Equal(1, again.Deleted);
        Assert.Single(sink.StoredIds);
    }

    [Fact]
    public void Redis_storage_registers_the_stores_without_connecting_until_they_are_used()
    {
        var services = Services(b => b.AddRedisStorage("localhost:1,abortConnect=false"));

        Assert.Contains(services, d => d.ServiceType == typeof(IFingerprintStore));
        Assert.Contains(services, d => d.ServiceType == typeof(IDeadLetterStore));
    }
}
