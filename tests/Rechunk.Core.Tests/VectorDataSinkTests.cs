using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.VectorData;
using Rechunk.Extensions.VectorData;

namespace Rechunk.Core.Tests;

public class VectorDataSinkTests
{
    private sealed record Doc(string Key, string Scope, string Id, string Text);

    /// <summary>A collection in a dictionary, with a counter and a fault hook for each call.</summary>
    private sealed class FakeCollection : VectorStoreCollection<string, Doc>
    {
        public Dictionary<string, Doc> Rows { get; } = new();

        public List<int> UpsertBatches { get; } = new();

        public List<int> DeleteBatches { get; } = new();

        /// <summary>Called before each upsert or delete with the call number, counting from 1. Throw to fail it.</summary>
        public Action<int>? Fault { get; set; }

        private int _calls;

        public override string Name => "fake";

        public override Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public override Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task UpsertAsync(Doc record, CancellationToken cancellationToken = default) =>
            UpsertAsync(new[] { record }, cancellationToken);

        public override Task UpsertAsync(IEnumerable<Doc> records, CancellationToken cancellationToken = default)
        {
            var list = records.ToList();
            Fault?.Invoke(++_calls);
            UpsertBatches.Add(list.Count);
            foreach (var record in list)
            {
                Rows[record.Key] = record;
            }

            return Task.CompletedTask;
        }

        public override Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            DeleteAsync(new[] { key }, cancellationToken);

        public override Task DeleteAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
        {
            var list = keys.ToList();
            Fault?.Invoke(++_calls);
            DeleteBatches.Add(list.Count);
            foreach (var key in list)
            {
                Rows.Remove(key);
            }

            return Task.CompletedTask;
        }

        public override Task<Doc?> GetAsync(string key, RecordRetrievalOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Rows.GetValueOrDefault(key));

        public override async IAsyncEnumerable<Doc> GetAsync(
            Expression<Func<Doc, bool>> filter,
            int top,
            FilteredRecordRetrievalOptions<Doc>? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var predicate = filter.Compile();
            foreach (var row in Rows.Values.Where(predicate).Take(top).ToList())
            {
                await Task.Yield();
                yield return row;
            }
        }

        public override IAsyncEnumerable<VectorSearchResult<Doc>> SearchAsync<TInput>(
            TInput searchValue, int top, VectorSearchOptions<Doc>? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public override object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private static VectorDataMapping<string, Doc> Mapping(bool listing = true) => new()
    {
        ToKey = VectorDataMapping<string, Doc>.ScopedKey,
        ToRecord = c => new Doc(VectorDataMapping<string, Doc>.ScopedKey(c.Scope ?? "default", c.Id), c.Scope ?? "default", c.Id.Value, c.Text),
        ScopeFilter = listing ? scope => d => d.Scope == scope : null,
        ToId = listing ? d => new ChunkId(d.Id) : null,
    };

    private static EmbeddedChunk Chunk(string id, string? scope = "docs") =>
        new(new ChunkId(id), new DocumentId("doc"), 1, "text " + id, new float[] { 1, 2 }, "test-model", scope);

    private static VectorDataSink<string, Doc> Sink(FakeCollection collection, int batch = 100, int attempts = 3, VectorDataMapping<string, Doc>? mapping = null) =>
        new(collection, mapping ?? Mapping(), batch, attempts, TimeSpan.Zero);

    [Fact]
    public async Task Chunks_are_written_as_records_with_the_key_from_the_mapping()
    {
        var collection = new FakeCollection();
        await Sink(collection).UpsertAsync(new[] { Chunk("a"), Chunk("b") });

        Assert.Equal(new[] { "docs:a", "docs:b" }, collection.Rows.Keys.OrderBy(k => k));
        Assert.Equal("text a", collection.Rows["docs:a"].Text);
    }

    [Fact]
    public async Task A_big_write_goes_in_batches()
    {
        var collection = new FakeCollection();
        var chunks = Enumerable.Range(0, 25).Select(i => Chunk("c" + i)).ToList();

        await Sink(collection, batch: 10).UpsertAsync(chunks);

        Assert.Equal(new[] { 10, 10, 5 }, collection.UpsertBatches);
        Assert.Equal(25, collection.Rows.Count);
    }

    [Fact]
    public async Task Writing_the_same_chunk_again_replaces_it()
    {
        var collection = new FakeCollection();
        var sink = Sink(collection);

        await sink.UpsertAsync(new[] { Chunk("a") });
        await sink.UpsertAsync(new[] { Chunk("a") });

        Assert.Single(collection.Rows);
    }

    [Fact]
    public async Task A_delete_removes_only_the_records_of_that_scope()
    {
        var collection = new FakeCollection();
        var sink = Sink(collection);
        await sink.UpsertAsync(new[] { Chunk("a", "one"), Chunk("a", "two"), Chunk("b", "one") });

        await sink.DeleteAsync("one", new[] { new ChunkId("a") });

        Assert.Equal(new[] { "one:b", "two:a" }, collection.Rows.Keys.OrderBy(k => k));
    }

    [Fact]
    public void A_key_without_the_scope_is_refused()
    {
        var mapping = new VectorDataMapping<string, Doc>
        {
            ToKey = (_, id) => id.Value,
            ToRecord = c => new Doc(c.Id.Value, c.Scope ?? "", c.Id.Value, c.Text),
        };

        var error = Assert.Throws<ArgumentException>(() => new VectorDataSink<string, Doc>(new FakeCollection(), mapping));

        Assert.Contains("scope", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deleting_something_that_is_not_there_is_fine_and_so_is_deleting_twice()
    {
        var collection = new FakeCollection();
        var sink = Sink(collection);
        await sink.UpsertAsync(new[] { Chunk("a") });

        await sink.DeleteAsync("docs", new[] { new ChunkId("a"), new ChunkId("missing") });
        await sink.DeleteAsync("docs", new[] { new ChunkId("a") });

        Assert.Empty(collection.Rows);
    }

    [Fact]
    public async Task A_big_delete_goes_in_batches()
    {
        var collection = new FakeCollection();
        var sink = Sink(collection, batch: 4);
        var ids = Enumerable.Range(0, 9).Select(i => new ChunkId("c" + i)).ToList();

        await sink.DeleteAsync("docs", ids);

        Assert.Equal(new[] { 4, 4, 1 }, collection.DeleteBatches);
    }

    [Fact]
    public async Task A_batch_that_fails_is_tried_again_as_a_whole()
    {
        var collection = new FakeCollection { Fault = call => { if (call == 1 || call == 2) throw new IOException("busy"); } };

        await Sink(collection).UpsertAsync(new[] { Chunk("a"), Chunk("b") });

        Assert.Equal(new[] { "docs:a", "docs:b" }, collection.Rows.Keys.OrderBy(k => k));
        Assert.Equal(new[] { 2 }, collection.UpsertBatches);
    }

    [Fact]
    public async Task A_delete_that_fails_is_tried_again()
    {
        var collection = new FakeCollection();
        var sink = Sink(collection);
        await sink.UpsertAsync(new[] { Chunk("a") });
        collection.Fault = call => { if (call == 2) throw new IOException("busy"); };

        await sink.DeleteAsync("docs", new[] { new ChunkId("a") });

        Assert.Empty(collection.Rows);
    }

    [Fact]
    public async Task The_error_comes_out_when_every_attempt_fails()
    {
        var calls = 0;
        var collection = new FakeCollection { Fault = _ => { calls++; throw new IOException("down"); } };

        await Assert.ThrowsAsync<IOException>(async () =>
            await Sink(collection, attempts: 3).UpsertAsync(new[] { Chunk("a") }));

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task A_cancelled_write_is_not_tried_again()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var collection = new FakeCollection { Fault = _ => { calls++; cts.Cancel(); throw new OperationCanceledException(cts.Token); } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Sink(collection, attempts: 5).UpsertAsync(new[] { Chunk("a") }, cts.Token));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task The_ids_of_a_scope_can_be_listed()
    {
        var collection = new FakeCollection();
        var sink = Sink(collection);
        await sink.UpsertAsync(new[] { Chunk("a", "one"), Chunk("b", "one"), Chunk("c", "two") });

        var ids = new List<string>();
        await foreach (var id in sink.ListIdsAsync("one"))
        {
            ids.Add(id.Value);
        }

        Assert.Equal(new[] { "a", "b" }, ids.OrderBy(i => i));
    }

    [Fact]
    public async Task Listing_without_a_filter_and_an_id_mapping_says_what_is_missing()
    {
        var sink = Sink(new FakeCollection(), mapping: Mapping(listing: false));

        var error = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in sink.ListIdsAsync("docs"))
            {
            }
        });

        Assert.Contains("ScopeFilter", error.Message);
    }

    [Fact]
    public void Bad_arguments_are_refused()
    {
        var collection = new FakeCollection();
        Assert.Throws<ArgumentNullException>(() => new VectorDataSink<string, Doc>(null!, Mapping()));
        Assert.Throws<ArgumentNullException>(() => new VectorDataSink<string, Doc>(collection, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sink(collection, batch: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sink(collection, attempts: 0));
    }
}
