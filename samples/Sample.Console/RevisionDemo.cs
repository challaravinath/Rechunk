using Rechunk;

/// <summary>
/// A document is ingested, then a new version of it comes in. Only the changed paragraph is embedded, and the old vector
/// of that paragraph is deleted. The embedder and the vector store here are small stand ins that keep everything in memory.
/// </summary>
internal static class RevisionDemo
{
    public static async Task RunAsync()
    {
        var options = new RechunkOptions();
        options.MinHash.Enabled = true;
        var pipeline = RechunkPipeline.CreateBuilder()
            .Configure(o => o.MinHash.Enabled = true)
            .UseFingerprintStore(new InMemoryFingerprintStore(options))
            .Build();
        var tracker = new RevisionTracker(pipeline, new InMemoryRevisionStore(), new InMemoryDeadLetterStore());
        var embedder = new DemoEmbedder();
        var sink = new DemoSink();
        var handbook = new DocumentId("handbook");

        var intro = "Welcome to the team. This handbook explains how we work, who to ask for help and where to find the tools you need in your first weeks.";
        var leave = "Employees get 30 days of paid leave per year. Requests for more than five days in a row need approval from the team lead.";
        var expenses = "Expenses must be filed within two weeks of the purchase date, with the receipt attached to the claim form.";
        var travel = "Travel has to be booked through the company portal. Flights over four hours may be booked in premium economy.";

        Console.WriteLine();
        Console.WriteLine("Revisions");
        Console.WriteLine("First version, three paragraphs:");
        Show(await tracker.ReviseAsync(handbook, "handbook", Chunks(intro, leave, expenses), embedder, sink));

        Console.WriteLine("Second version, the leave paragraph edited and a new one added at the end:");
        var leaveEdited = leave.Replace("30 days", "28 days");
        Show(await tracker.ReviseAsync(handbook, "handbook", Chunks(intro, leaveEdited, expenses, travel), embedder, sink));

        Console.WriteLine("Third version, the expenses paragraph moved to the top:");
        Show(await tracker.ReviseAsync(handbook, "handbook", Chunks(expenses, intro, leaveEdited, travel), embedder, sink));

        Console.WriteLine("Fourth version, the document is deleted:");
        Show(await tracker.DeleteDocumentAsync(handbook, "handbook", embedder, sink));

        var report = await tracker.VerifyAsync("handbook", sink);
        Console.WriteLine($"Vectors left in the store: {sink.Count}. Orphans: {report.OrphanVectors.Count}, missing: {report.MissingVectors.Count}.");
    }

    private static List<ChunkInput> Chunks(params string[] texts) =>
        texts.Select((t, i) => new ChunkInput(i, t)).ToList();

    private static void Show(RevisionResult r) =>
        Console.WriteLine($"  revision {r.Revision}: {r.Embedded} embedded, {r.Kept} kept ({r.Moved} moved), {r.Deleted} vectors deleted, status {r.Status}");

    private sealed class DemoEmbedder : IChunkEmbedder
    {
        public string ModelId => "demo-model";

        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            new(texts.Select(t => new ReadOnlyMemory<float>(new float[] { t.Length })).ToList());
    }

    private sealed class DemoSink : IVectorSink
    {
        private readonly Dictionary<string, EmbeddedChunk> _rows = new();

        public int Count => _rows.Count;

        public ValueTask UpsertAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken ct = default)
        {
            foreach (var chunk in chunks)
            {
                _rows[chunk.Id.Value] = chunk;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(IReadOnlyList<ChunkId> ids, CancellationToken ct = default)
        {
            foreach (var id in ids)
            {
                _rows.Remove(id.Value);
            }

            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<ChunkId> ListIdsAsync(string scope, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var key in _rows.Keys.ToList())
            {
                yield return new ChunkId(key);
            }
        }
    }
}
