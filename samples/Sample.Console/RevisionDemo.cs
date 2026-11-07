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

    /// <summary>
    /// The audit trail, PII scanning and erasure. An email address is masked, a document with an access key is kept out,
    /// every step is audited without text, and at the end everything that came from one source is erased.
    /// </summary>
    public static async Task RunAuditAndErasureAsync()
    {
        var pipeline = RechunkPipeline.CreateBuilder()
            .Configure(o =>
            {
                o.Pii.Enabled = true;
                o.Pii.Set(PiiType.Email, PiiPolicy.Mask);
                o.Pii.Set(PiiType.ApiKey, PiiPolicy.Block);
            })
            .UseFingerprintStore(new InMemoryFingerprintStore(new RechunkOptions()))
            .Build();
        var audit = new InMemoryAuditLog();
        var tracker = new RevisionTracker(pipeline, new InMemoryRevisionStore(), new InMemoryDeadLetterStore(), audit);
        var embedder = new DemoEmbedder();
        var sink = new DemoSink();

        // Built from pieces so this file does not hold something that looks like a real key.
        var key = "AKIA" + "Q3XJ7RM2WKD5" + "TZ4B";

        Console.WriteLine();
        Console.WriteLine("Audit, PII and erasure");

        var contact = new DocumentId("contacts");
        var runbook = new DocumentId("runbook");
        var policy = new DocumentId("policy");

        var first = await tracker.ReviseAsync(contact, "demo", Chunks("To reach the payroll team write to anita.rao@example.com during office hours."), embedder, sink, source: "wiki");
        Console.WriteLine($"  contacts (wiki): {first.Embedded} embedded, the email address is masked before it is embedded");

        var second = await tracker.ReviseAsync(runbook, "demo", Chunks("Deploy with the access key " + key + " that is stored in the build settings."), embedder, sink, source: "wiki");
        Console.WriteLine($"  runbook (wiki): {second.Embedded} embedded, {second.PiiBlocked} kept out because of the access key");

        await tracker.ReviseAsync(policy, "demo", Chunks("Expenses must be filed within two weeks of the purchase date, with the receipt attached."), embedder, sink, source: "drive");

        Console.WriteLine("  What the audit log has for the wiki (no chunk text, only ids, kinds and spans):");
        await foreach (var e in audit.QueryAsync(new AuditQuery { Source = "wiki" }))
        {
            var spans = e.Findings.Count == 0 ? string.Empty : "  " + string.Join(", ", e.Findings.Select(f => $"{f.Type} at {f.Start} length {f.Length} ({f.Applied})"));
            Console.WriteLine($"    {e.Kind,-8} {e.Document.Value,-9} {e.Stage}{spans}");
        }

        var erased = await tracker.EraseAsync(new EraseRequest("demo", null, "wiki", "demo request", "demo"), sink);
        Console.WriteLine($"  Erased everything from the wiki: {erased.Documents} documents, {erased.VectorsDeleted} vectors, {erased.StateRowsDeleted} fingerprint rows.");
        Console.WriteLine($"  Vectors left in the store: {sink.Count} (the policy document from the drive).");

        await foreach (var receipt in audit.QueryAsync(new AuditQuery { Kind = AuditKind.ErasureReceipt }))
        {
            Console.WriteLine($"  Receipt: {string.Join(", ", receipt.Counts.Select(c => $"{c.Key} {c.Value}"))}, asked by {receipt.Actor}.");
        }
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
                _rows[Key(chunk.Scope, chunk.Id)] = chunk;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(string scope, IReadOnlyList<ChunkId> ids, CancellationToken ct = default)
        {
            foreach (var id in ids)
            {
                _rows.Remove(Key(scope, id));
            }

            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<ChunkId> ListIdsAsync(string scope, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var chunk in _rows.Values.Where(c => c.Scope == scope).ToList())
            {
                yield return chunk.Id;
            }
        }

        private static string Key(string? scope, ChunkId id) => (scope ?? "") + ":" + id.Value;
    }
}
