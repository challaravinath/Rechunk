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

    [Fact]
    public void Redis_storage_registers_the_stores_without_connecting_until_they_are_used()
    {
        var services = Services(b => b.AddRedisStorage("localhost:1,abortConnect=false"));

        Assert.Contains(services, d => d.ServiceType == typeof(IFingerprintStore));
        Assert.Contains(services, d => d.ServiceType == typeof(IDeadLetterStore));
    }
}
