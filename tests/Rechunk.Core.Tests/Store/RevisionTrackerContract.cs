namespace Rechunk.Core.Tests.Store;

/// <summary>Lets a test make a commit fail, or answer it with a result of its own.</summary>
public sealed class HookedRevisionStore(IRevisionStore inner) : IRevisionStore
{
    public Func<CommitRevisionRequest, CommitRevisionResult?>? OnCommit { get; set; }

    public int Commits;

    public ValueTask<DocumentManifest?> GetAsync(string scope, DocumentId document, CancellationToken ct = default) =>
        inner.GetAsync(scope, document, ct);

    public ValueTask<CommitRevisionResult> CommitAsync(CommitRevisionRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Commits);
        return OnCommit?.Invoke(request) is { } answer ? ValueTask.FromResult(answer) : inner.CommitAsync(request, ct);
    }

    public ValueTask<IReadOnlyList<ChunkId>> ListPendingAsync(string scope, DateTimeOffset now, int take, CancellationToken ct = default) =>
        inner.ListPendingAsync(scope, now, take, ct);

    public ValueTask<RetireClaim?> BeginRetireAsync(
        string scope, ChunkId chunk, string holder, TimeSpan lease, DateTimeOffset now, CancellationToken ct = default) =>
        inner.BeginRetireAsync(scope, chunk, holder, lease, now, ct);

    public ValueTask<bool> FinishRetireAsync(string scope, ChunkId chunk, string holder, CancellationToken ct = default) =>
        inner.FinishRetireAsync(scope, chunk, holder, ct);

    public ValueTask<ChunkRef?> GetRefAsync(string scope, ChunkId chunk, CancellationToken ct = default) =>
        inner.GetRefAsync(scope, chunk, ct);

    public ValueTask<IReadOnlyList<ChunkRef>> ListRefsAsync(string scope, CancellationToken ct = default) =>
        inner.ListRefsAsync(scope, ct);

    public ValueTask<IReadOnlyList<DocumentRef>> ListDocumentsAsync(
        string scope, string? source, string? afterDocument, int take, CancellationToken ct = default) =>
        inner.ListDocumentsAsync(scope, source, afterDocument, take, ct);

    public ValueTask<IReadOnlyList<DocumentRef>> ListDocumentsUsingAsync(
        string scope, IReadOnlyCollection<ChunkId> chunks, string? afterDocument, int take, CancellationToken ct = default) =>
        inner.ListDocumentsUsingAsync(scope, chunks, afterDocument, take, ct);
}

/// <summary>The three stores a worker uses. Two workers in one test share the same data.</summary>
public sealed record WorkerStores(IFingerprintStore Fingerprints, IRevisionStore Revisions, IDeadLetterStore DeadLetters);

/// <summary>
/// The revision tracker end to end, on every backend. The tests use real stores and a sink that really deletes, and look
/// at what is left in the sink, the stores and the embedder afterwards.
/// </summary>
public abstract partial class RevisionTrackerContract : IDisposable
{
    protected const string Scope = "tenant-a";
    protected readonly ManualTimeProvider Clock = new();

    protected abstract WorkerStores CreateStores(RechunkOptions options, TimeProvider time);

    public virtual void Dispose()
    {
    }

    private WorkerStores? _stores;

    // Every call gives the same underlying data, a new object for stores that keep nothing in the process.
    private WorkerStores Stores(RechunkOptions options) => _stores = CreateStores(options, Clock);

    /// <summary>One worker: its own pipeline and tracker, sharing the stores and the sink with the other workers of a test.</summary>
    private sealed class Worker
    {
        public required RevisionTracker Tracker { get; init; }
        public required ScriptedEmbedder Embedder { get; init; }
        public required HookedRevisionStore Revisions { get; init; }
        public required WorkerStores Stores { get; init; }
        public required RechunkPipeline Pipeline { get; init; }
        public List<string> Embedded { get; } = new();
        public InMemoryAuditLog Audit { get; init; } = new();
    }

    private static RechunkOptions BaseOptions(DuplicatePolicy policy = DuplicatePolicy.Skip)
    {
        var options = new RechunkOptions();
        options.SimHash.Enabled = true;
        options.MinHash.Enabled = true;
        options.OnDuplicate = policy;
        options.Failures.MaxAttempts = 2;
        options.Failures.RetryDelay = TimeSpan.Zero;
        options.Revisions.ConflictDelay = TimeSpan.Zero;
        return options;
    }

    private Worker NewWorker(DuplicatePolicy policy = DuplicatePolicy.Skip, Action<RechunkOptions>? configure = null)
    {
        var options = BaseOptions(policy);
        configure?.Invoke(options);
        return NewWorker(Stores(options), options);
    }

    private Worker NewWorker(WorkerStores stores, RechunkOptions options)
    {
        var piiOptions = options.Pii;
        var pipeline = RechunkPipeline.CreateBuilder()
            .Configure(o =>
            {
                o.SimHash.Enabled = options.SimHash.Enabled;
                o.MinHash.Enabled = options.MinHash.Enabled;
                o.OnDuplicate = options.OnDuplicate;
                o.OnRevision = options.OnRevision;
                o.Failures.MaxAttempts = options.Failures.MaxAttempts;
                o.Failures.RetryDelay = options.Failures.RetryDelay;
                o.Revisions.ConflictDelay = options.Revisions.ConflictDelay;
                o.Revisions.MaxConflictRetries = options.Revisions.MaxConflictRetries;
                o.Revisions.RetireLease = options.Revisions.RetireLease;
                o.Pii.Enabled = piiOptions.Enabled;
                o.Pii.DefaultPolicy = piiOptions.DefaultPolicy;
                o.Pii.Detector = piiOptions.Detector;
                o.Pii.MinConfidence = piiOptions.MinConfidence;
                foreach (var (type, policy) in piiOptions.Policies)
                {
                    o.Pii.Set(type, policy);
                }
            })
            .UseFingerprintStore(stores.Fingerprints)
            .Build();
        var revisions = new HookedRevisionStore(stores.Revisions);
        var audit = new InMemoryAuditLog();
        var embedder = new ScriptedEmbedder();
        var worker = new Worker
        {
            Tracker = new RevisionTracker(pipeline, revisions, stores.DeadLetters, audit, time: Clock),
            Embedder = embedder,
            Revisions = revisions,
            Stores = stores,
            Pipeline = pipeline,
            Audit = audit,
        };
        embedder.Hook = (texts, _) =>
        {
            lock (worker.Embedded)
            {
                worker.Embedded.AddRange(texts);
            }
        };
        return worker;
    }

    // a second worker on the same data
    private Worker Another(Worker first, DuplicatePolicy policy = DuplicatePolicy.Skip) => NewWorker(first.Stores, BaseOptions(policy));

    private static readonly DocumentId One = new("doc-1");
    private static readonly DocumentId Two = new("doc-2");

    private static string Para(int seed) => string.Join(' ', TestText.Words(seed, 200));

    private static string Edit(string text, int word)
    {
        var words = text.Split(' ');
        words[word] = "zqx" + words[word];
        return string.Join(' ', words);
    }

    private static IReadOnlyList<ChunkInput> Doc(params string[] texts) => texts.Select((t, i) => new ChunkInput(i, t)).ToList();

    private static Task<RevisionResult> Revise(Worker w, RecordingSink sink, DocumentId doc, IReadOnlyList<ChunkInput> chunks, string? holder = null) =>
        w.Tracker.ReviseAsync(doc, Scope, chunks, w.Embedder, sink, holder).AsTask();

    private static Task AssertClean(Worker w, RecordingSink sink) => AssertClean(w, sink, Scope);

    private static async Task AssertClean(Worker w, RecordingSink sink, string scope)
    {
        var report = await w.Tracker.VerifyAsync(scope, sink);
        Assert.Empty(report.OrphanVectors);
        Assert.Empty(report.MissingVectors);
    }

    private static async Task<List<string>> Ids(RecordingSink sink, string scope)
    {
        var ids = new List<string>();
        await foreach (var id in sink.ListIdsAsync(scope))
        {
            ids.Add(id.Value);
        }

        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    private static string IdOf(Worker w, string text) => w.Pipeline.IdFor(new ChunkInput(0, text)).Value;

    // ---- the basics ----

    [SkippableFact]
    public async Task A_first_revision_embeds_every_chunk_and_stores_the_manifest()
    {
        var w = NewWorker();
        var sink = new RecordingSink();

        var result = await Revise(w, sink, One, Doc(Para(1), Para(2), Para(3)));

        Assert.Equal(RevisionStatus.Applied, result.Status);
        Assert.Equal(1, result.Revision);
        Assert.Equal(3, result.Embedded);
        Assert.Equal(3, sink.StoredIds.Count);
        var manifest = await w.Tracker.GetManifestAsync(Scope, One);
        Assert.Equal(1, manifest!.Revision);
        Assert.Equal(3, manifest.Entries.Count);
        await AssertClean(w, sink);
    }

    [SkippableFact]
    public async Task The_vectors_carry_the_document_the_revision_and_the_scope()
    {
        var w = NewWorker();
        var sink = new RecordingSink();

        await Revise(w, sink, One, Doc(Para(1)));
        var stored = sink.Get(sink.StoredIds.Single())!;

        Assert.Equal(One, stored.Document);
        Assert.Equal(1, stored.Revision);
        Assert.Equal(Scope, stored.Scope);
    }

    [SkippableFact]
    public async Task Revising_with_the_same_chunks_does_not_embed_and_does_not_commit()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await Revise(w, sink, One, Doc(Para(1), Para(2)));
        var commits = w.Revisions.Commits;
        var calls = w.Embedder.Calls;

        var again = await Revise(w, sink, One, Doc(Para(1), Para(2)));

        Assert.Equal(RevisionStatus.Unchanged, again.Status);
        Assert.Equal(1, again.Revision);
        Assert.Equal(0, again.EmbeddingCalls);
        Assert.Equal(calls, w.Embedder.Calls);
        Assert.Equal(commits, w.Revisions.Commits);
        Assert.Equal(2, sink.StoredIds.Count);
    }

    [SkippableFact]
    public async Task An_edited_chunk_is_the_only_one_embedded_and_its_old_vector_and_fingerprints_go()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await Revise(w, sink, One, Doc(Para(1), Para(2), Para(3)));
        w.Embedded.Clear();
        var oldId = IdOf(w, Para(2));

        var edited = Edit(Para(2), 100);
        var result = await Revise(w, sink, One, Doc(Para(1), edited, Para(3)));

        Assert.Equal(RevisionStatus.Applied, result.Status);
        Assert.Equal(2, result.Revision);
        Assert.Equal(new[] { edited }, w.Embedded);
        Assert.Equal(1, result.Embedded);
        Assert.Equal(2, result.Kept);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(3, sink.StoredIds.Count);
        Assert.DoesNotContain(oldId, sink.StoredIds);
        Assert.Contains(IdOf(w, edited), sink.StoredIds);
        await AssertClean(w, sink);

        // the old text is gone from every stage, so it counts as new if it comes back
        w.Embedded.Clear();
        var back = await Revise(w, sink, One, Doc(Para(1), Para(2), Para(3)));
        Assert.Equal(new[] { Para(2) }, w.Embedded);
        Assert.Equal(1, back.Embedded);
        await AssertClean(w, sink);
    }

    [SkippableFact]
    public async Task The_manifest_remembers_which_chunk_a_new_one_replaced()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await Revise(w, sink, One, Doc(Para(1), Para(2)));
        var oldId = IdOf(w, Para(2));

        await Revise(w, sink, One, Doc(Para(1), Edit(Para(2), 10)));
        var entries = (await w.Tracker.GetManifestAsync(Scope, One))!.Entries;

        Assert.Equal(1, entries[0].FirstRevision);
        Assert.Null(entries[0].Replaces);
        Assert.Equal(2, entries[1].FirstRevision);
        Assert.Equal(oldId, entries[1].Replaces!.Value.Value);
    }

    [SkippableFact]
    public async Task A_removed_chunk_leaves_no_vector_and_no_fingerprint()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await Revise(w, sink, One, Doc(Para(1), Para(2)));

        var result = await Revise(w, sink, One, Doc(Para(1)));

        Assert.Equal(1, result.Deleted);
        Assert.Equal(0, result.Embedded);
        Assert.Equal(new[] { IdOf(w, Para(1)) }, sink.StoredIds);
        await AssertClean(w, sink);
        Assert.DoesNotContain(await w.Stores.Revisions.ListRefsAsync(Scope), r => r.Chunk.Value == IdOf(w, Para(2)));

        // the same text can be added again and is embedded again
        w.Embedded.Clear();
        await Revise(w, sink, One, Doc(Para(1), Para(2)));
        Assert.Equal(new[] { Para(2) }, w.Embedded);
        Assert.Equal(2, sink.StoredIds.Count);
    }

    [SkippableFact]
    public async Task A_paragraph_that_only_moves_is_not_embedded_again()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await Revise(w, sink, One, Doc(Para(1), Para(2), Para(3)));
        w.Embedded.Clear();

        var result = await Revise(w, sink, One, Doc(Para(3), Para(1), Para(2)));

        Assert.Equal(RevisionStatus.Applied, result.Status);
        Assert.Equal(0, result.EmbeddingCalls);
        Assert.Empty(w.Embedded);
        Assert.Equal(3, result.Moved);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(3, sink.StoredIds.Count);
        var entries = (await w.Tracker.GetManifestAsync(Scope, One))!.Entries;
        Assert.Equal(IdOf(w, Para(3)), entries[0].Id.Value);
        Assert.Equal(2, entries[0].PreviousOrdinal);
        Assert.Equal(1, entries[0].FirstRevision);
        await AssertClean(w, sink);
    }

    [SkippableFact]
    public async Task A_moved_paragraph_next_to_a_new_one_embeds_only_the_new_one()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await Revise(w, sink, One, Doc(Para(1), Para(2)));
        w.Embedded.Clear();

        await Revise(w, sink, One, Doc(Para(9), Para(2), Para(1)));

        Assert.Equal(new[] { Para(9) }, w.Embedded);
        Assert.Equal(3, sink.StoredIds.Count);
    }

    [SkippableFact]
    public async Task A_chunk_that_repeats_in_a_document_is_stored_once_and_survives_while_one_copy_is_left()
    {
        var w = NewWorker();
        var sink = new RecordingSink();

        await Revise(w, sink, One, Doc(Para(1), Para(2), Para(1)));
        Assert.Equal(2, sink.StoredIds.Count);
        Assert.Equal(3, (await w.Tracker.GetManifestAsync(Scope, One))!.Entries.Count);

        var result = await Revise(w, sink, One, Doc(Para(1), Para(2)));
        Assert.Equal(0, result.Deleted);
        Assert.Equal(2, sink.StoredIds.Count);
    }

    [SkippableFact]
    public async Task Planning_shows_what_would_happen_and_changes_nothing()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await Revise(w, sink, One, Doc(Para(1), Para(2), Para(3)));

        var plan = await w.Tracker.PlanAsync(One, Scope, Doc(Para(3), Edit(Para(2), 5), Para(7)));

        Assert.Equal(1, plan.FromRevision);
        Assert.Equal(2, plan.ToRevision);
        Assert.Equal(ChunkAction.Keep, plan.Chunks[0].Action);
        Assert.Contains("moved from position 2", plan.Chunks[0].Reason);
        Assert.Equal(ChunkAction.Replace, plan.Chunks[1].Action);
        Assert.Equal(ChunkAction.Embed, plan.Chunks[2].Action);
        Assert.Equal(new[] { IdOf(w, Para(1)), IdOf(w, Para(2)) }.OrderBy(x => x), plan.ToDelete.Select(x => x.Value).OrderBy(x => x));
        Assert.Equal(1, (await w.Tracker.GetManifestAsync(Scope, One))!.Revision);
        Assert.Equal(3, sink.StoredIds.Count);
    }

    [SkippableFact]
    public async Task A_plan_can_be_applied_later()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await Revise(w, sink, One, Doc(Para(1), Para(2)));
        var plan = await w.Tracker.PlanAsync(One, Scope, Doc(Para(1)));

        var result = await w.Tracker.ApplyAsync(plan, w.Embedder, sink);

        Assert.Equal(RevisionStatus.Applied, result.Status);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(new[] { IdOf(w, Para(1)) }, sink.StoredIds);
    }

    // ---- documents that share chunks ----

    [SkippableFact]
    public async Task A_chunk_two_documents_share_is_embedded_once_and_kept_until_both_let_go()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await Revise(w, sink, One, Doc(Para(1), Para(2)));
        w.Embedded.Clear();

        var second = await Revise(w, sink, Two, Doc(Para(2), Para(5)));
        Assert.Equal(new[] { Para(5) }, w.Embedded);
        Assert.Equal(1, second.Skipped);

        var drop = await Revise(w, sink, One, Doc(Para(1)));
        Assert.Equal(0, drop.Deleted);
        Assert.Contains(IdOf(w, Para(2)), sink.StoredIds);

        var last = await Revise(w, sink, Two, Doc(Para(5)));
        Assert.Equal(1, last.Deleted);
        Assert.DoesNotContain(IdOf(w, Para(2)), sink.StoredIds);
        await AssertClean(w, sink);
    }

    [SkippableFact]
    public async Task Deleting_a_document_removes_what_only_it_used()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await Revise(w, sink, One, Doc(Para(1), Para(2)));
        await Revise(w, sink, Two, Doc(Para(2)));

        var result = await w.Tracker.DeleteDocumentAsync(One, Scope, w.Embedder, sink);

        Assert.Equal(RevisionStatus.Applied, result.Status);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(new[] { IdOf(w, Para(2)) }, sink.StoredIds);
        Assert.True((await w.Tracker.GetManifestAsync(Scope, One))!.IsDeleted);
        await AssertClean(w, sink);
    }

    [SkippableFact]
    public async Task A_document_that_was_deleted_can_come_back()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await Revise(w, sink, One, Doc(Para(1)));
        await w.Tracker.DeleteDocumentAsync(One, Scope, w.Embedder, sink);
        w.Embedded.Clear();

        var result = await Revise(w, sink, One, Doc(Para(1)));

        Assert.Equal(RevisionStatus.Applied, result.Status);
        Assert.Equal(3, result.Revision);
        Assert.Equal(new[] { Para(1) }, w.Embedded);
        await AssertClean(w, sink);
    }

    [SkippableFact]
    public async Task Deleting_a_document_that_never_existed_does_nothing()
    {
        var w = NewWorker();
        var sink = new RecordingSink();

        var result = await w.Tracker.DeleteDocumentAsync(One, Scope, w.Embedder, sink);

        Assert.Equal(RevisionStatus.Unchanged, result.Status);
        Assert.Null(await w.Tracker.GetManifestAsync(Scope, One));
    }

    // ---- aliases ----

    [SkippableFact]
    public async Task A_near_copy_stored_as_an_alias_goes_with_its_chunk_and_the_text_is_new_again_afterwards()
    {
        var w = NewWorker(DuplicatePolicy.Alias);
        var sink = new RecordingSink();
        await Revise(w, sink, One, Doc(Para(1)));
        w.Embedded.Clear();
        var near = Edit(Para(1), 120);

        var aliased = await Revise(w, sink, Two, Doc(near));

        Assert.Empty(w.Embedded);
        Assert.Equal(1, aliased.Skipped);
        var entry = (await w.Tracker.GetManifestAsync(Scope, Two))!.Entries.Single();
        Assert.NotEqual(entry.Id, entry.Target);
        Assert.Equal(IdOf(w, Para(1)), entry.Target.Value);
        Assert.Single(sink.StoredIds);

        // the first document lets go, the second still uses the chunk through the alias
        await w.Tracker.DeleteDocumentAsync(One, Scope, w.Embedder, sink);
        Assert.Single(sink.StoredIds);

        await w.Tracker.DeleteDocumentAsync(Two, Scope, w.Embedder, sink);
        Assert.Empty(sink.StoredIds);
        await AssertClean(w, sink);

        var fresh = await Revise(w, sink, One, Doc(near));
        Assert.Equal(new[] { near }, w.Embedded);
        Assert.Equal(1, fresh.Embedded);
    }

    // ---- languages ----

    [SkippableFact]
    public async Task The_same_text_in_two_languages_gives_two_vectors_and_each_goes_on_its_own()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        var text = Para(4);
        var chunks = new[] { new ChunkInput(0, text, "en"), new ChunkInput(1, text, "fr") };

        var result = await Revise(w, sink, One, chunks);

        Assert.Equal(2, result.Embedded);
        Assert.Equal(2, sink.StoredIds.Count);
        var entries = (await w.Tracker.GetManifestAsync(Scope, One))!.Entries;
        Assert.Equal(new[] { "en", "fr" }, entries.Select(e => e.Language));
        Assert.Equal(new[] { "en", "fr" }, sink.StoredIds.Select(i => sink.Get(i)!.Language).OrderBy(x => x));

        await Revise(w, sink, One, new[] { new ChunkInput(0, text, "en") });
        Assert.Single(sink.StoredIds);
        Assert.Equal("en", sink.Get(sink.StoredIds.Single())!.Language);
        await AssertClean(w, sink);

        // the French copy was forgotten, so it is embedded again
        w.Embedded.Clear();
        await Revise(w, sink, One, chunks);
        Assert.Single(w.Embedded);
        Assert.Equal(2, sink.StoredIds.Count);
    }

    // ---- races between workers ----

    [SkippableFact]
    public async Task A_revision_that_loses_a_race_plans_again_and_still_lands()
    {
        var a = NewWorker();
        var b = Another(a);
        var sink = new RecordingSink();
        await Revise(a, sink, One, Doc(Para(1)));
        var raced = false;
        a.Embedder.Hook = (texts, _) =>
        {
            if (raced)
            {
                return;
            }

            raced = true;
            // another worker revises the same document while this one is embedding
            Revise(b, sink, One, Doc(Para(1), Para(8))).GetAwaiter().GetResult();
        };

        var result = await Revise(a, sink, One, Doc(Para(1), Para(2)));

        Assert.Equal(RevisionStatus.Applied, result.Status);
        Assert.Equal(3, result.Revision);
        var entries = (await a.Tracker.GetManifestAsync(Scope, One))!.Entries;
        Assert.Equal(new[] { IdOf(a, Para(1)), IdOf(a, Para(2)) }, entries.Select(e => e.Id.Value));
        Assert.Equal(2, sink.StoredIds.Count);
        Assert.DoesNotContain(IdOf(a, Para(8)), sink.StoredIds);
        await AssertClean(a, sink);
    }

    [SkippableFact]
    public async Task A_revision_that_keeps_losing_reports_a_conflict_and_changes_nothing()
    {
        var w = NewWorker(configure: o => o.Revisions.MaxConflictRetries = 2);
        var sink = new RecordingSink();
        await Revise(w, sink, One, Doc(Para(1)));
        w.Revisions.OnCommit = request => new CommitRevisionResult(CommitRevisionStatus.Conflict, 1, Array.Empty<ChunkId>(), Array.Empty<ChunkId>());

        var result = await Revise(w, sink, One, Doc(Para(1), Para(2)));

        Assert.Equal(RevisionStatus.Conflict, result.Status);
        Assert.Equal(1, (await w.Tracker.GetManifestAsync(Scope, One))!.Revision);
    }

    [SkippableFact]
    public async Task Several_workers_revising_one_document_leave_it_consistent()
    {
        var first = NewWorker();
        var sink = new RecordingSink();
        await Revise(first, sink, One, Doc(Para(1)));
        var workers = Enumerable.Range(0, 4).Select(_ => Another(first)).ToList();

        var results = await Task.WhenAll(workers.Select((w, i) =>
            Task.Run(async () => await Revise(w, sink, One, Doc(Para(1), Para(20 + i))))));

        Assert.Contains(results, r => r.Status == RevisionStatus.Applied);
        var manifest = (await first.Tracker.GetManifestAsync(Scope, One))!;
        Assert.Equal(2, manifest.Entries.Count);
        Assert.Equal(
            manifest.Entries.Select(e => e.Target.Value).OrderBy(x => x),
            sink.StoredIds.OrderBy(x => x));
        await AssertClean(first, sink);
    }

    [SkippableFact]
    public async Task A_chunk_another_worker_is_still_embedding_makes_the_revision_incomplete_and_a_rerun_finishes_it()
    {
        var a = NewWorker();
        var b = Another(a);
        var sink = new RecordingSink();
        RevisionResult? inner = null;
        var once = false;
        a.Embedder.Hook = (texts, _) =>
        {
            if (once)
            {
                return;
            }

            once = true;
            // while a is embedding this text, b asks for the same text for another document
            inner = Revise(b, sink, Two, Doc(Para(1)), "b-message").GetAwaiter().GetResult();
        };

        var outer = await Revise(a, sink, One, Doc(Para(1)), "a-message");

        Assert.Equal(RevisionStatus.Incomplete, inner!.Status);
        Assert.Equal(1, inner.InFlight);
        Assert.Null(await b.Tracker.GetManifestAsync(Scope, Two));
        Assert.Equal(RevisionStatus.Applied, outer.Status);

        var rerun = await Revise(b, sink, Two, Doc(Para(1)), "b-message");
        Assert.Equal(RevisionStatus.Applied, rerun.Status);
        Assert.Equal(0, rerun.Embedded);
        Assert.Equal(1, rerun.Skipped);
        Assert.Single(sink.StoredIds);
        await AssertClean(a, sink);
    }

    // ---- failures ----

    [SkippableFact]
    public async Task A_chunk_that_can_not_be_embedded_leaves_the_document_on_its_old_revision_and_a_rerun_repairs_it()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await Revise(w, sink, One, Doc(Para(1)));
        var fail = true;
        w.Embedder.Hook = (texts, _) =>
        {
            if (fail)
            {
                throw new TimeoutException("the model did not answer");
            }
        };

        var broken = await Revise(w, sink, One, Doc(Para(1), Para(2)), "msg-1");

        Assert.Equal(RevisionStatus.Incomplete, broken.Status);
        Assert.Equal(1, broken.DeadLettered);
        Assert.Equal(1, (await w.Tracker.GetManifestAsync(Scope, One))!.Revision);
        Assert.Single(sink.StoredIds);

        fail = false;
        var fixedRun = await Revise(w, sink, One, Doc(Para(1), Para(2)), "msg-1");

        Assert.Equal(RevisionStatus.Applied, fixedRun.Status);
        Assert.Equal(2, sink.StoredIds.Count);
        await AssertClean(w, sink);
    }

    [SkippableFact]
    public async Task A_crash_before_the_switch_leaves_vectors_that_verify_reports_and_a_retry_repairs()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await Revise(w, sink, One, Doc(Para(1)));
        w.Revisions.OnCommit = _ => throw new InvalidOperationException("the worker died");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Revise(w, sink, One, Doc(Para(1), Para(2)), "msg-1"));

        Assert.Equal(1, (await w.Tracker.GetManifestAsync(Scope, One))!.Revision);
        Assert.Equal(2, sink.StoredIds.Count);
        var report = await w.Tracker.VerifyAsync(Scope, sink);
        Assert.Equal(new[] { IdOf(w, Para(2)) }, report.OrphanVectors.Select(i => i.Value));

        w.Revisions.OnCommit = null;
        w.Embedded.Clear();
        var retry = await Revise(w, sink, One, Doc(Para(1), Para(2)), "msg-1");

        Assert.Equal(RevisionStatus.Applied, retry.Status);
        Assert.Empty(w.Embedded);
        await AssertClean(w, sink);
    }

    [SkippableFact]
    public async Task A_delete_that_fails_does_not_undo_the_revision_and_a_later_sweep_finishes_it()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await Revise(w, sink, One, Doc(Para(1), Para(2)));
        var down = true;
        sink.OnDelete = _ => down ? new TimeoutException("the vector store is down") : null;

        var result = await Revise(w, sink, One, Doc(Para(1)));

        Assert.Equal(RevisionStatus.Applied, result.Status);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(2, sink.StoredIds.Count);
        Assert.Single((await w.Tracker.GetManifestAsync(Scope, One))!.Entries);

        // the lease of the failed sweep has to run out, then any worker can finish it
        down = false;
        Clock.Advance(TimeSpan.FromMinutes(3));
        var sweep = await w.Tracker.SweepAsync(Scope, sink);

        Assert.Single(sweep.Retired);
        Assert.Equal(new[] { IdOf(w, Para(1)) }, sink.StoredIds);
        Assert.DoesNotContain(await w.Stores.Revisions.ListRefsAsync(Scope), r => r.Chunk.Value == IdOf(w, Para(2)));
        await AssertClean(w, sink);

        // and its text is forgotten for good
        w.Embedded.Clear();
        await Revise(w, sink, One, Doc(Para(1), Para(2)));
        Assert.Equal(new[] { Para(2) }, w.Embedded);
    }

    [SkippableFact]
    public async Task A_sweep_while_another_sweeper_holds_the_chunk_does_nothing()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await Revise(w, sink, One, Doc(Para(1), Para(2)));
        sink.OnDelete = _ => new TimeoutException("down");
        await Revise(w, sink, One, Doc(Para(1)));
        sink.OnDelete = null;

        var early = await w.Tracker.SweepAsync(Scope, sink);

        Assert.Empty(early.Retired);
        Assert.Equal(2, sink.StoredIds.Count);
    }

    [SkippableFact]
    public async Task A_revision_that_wants_a_chunk_being_deleted_waits_for_the_lease_and_then_embeds_it_fresh()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await Revise(w, sink, One, Doc(Para(1), Para(2)));
        sink.OnDelete = _ => new TimeoutException("down");
        await Revise(w, sink, One, Doc(Para(1)));
        sink.OnDelete = null;
        w.Embedded.Clear();

        // the chunk is held by a sweeper that failed, the text is still in the fingerprint store
        var blocked = await Revise(w, sink, Two, Doc(Para(2)));
        Assert.Equal(RevisionStatus.Blocked, blocked.Status);
        Assert.Null(await w.Tracker.GetManifestAsync(Scope, Two));

        Clock.Advance(TimeSpan.FromMinutes(3));
        var again = await Revise(w, sink, Two, Doc(Para(2)));

        Assert.Equal(RevisionStatus.Applied, again.Status);
        Assert.Equal(new[] { Para(2) }, w.Embedded);
        Assert.Contains(IdOf(w, Para(2)), sink.StoredIds);
        await AssertClean(w, sink);
    }

    // ---- erase ----

    [SkippableFact]
    public async Task Erasing_a_document_removes_vectors_fingerprints_and_dead_letters()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await Revise(w, sink, One, Doc(Para(1), Para(2)));
        await Revise(w, sink, Two, Doc(Para(2)));
        await w.Stores.DeadLetters.AddAsync(new DeadLetter(
            "dl-1", Scope, new ChunkId("x"), One, 0, DeadLetterStage.Embed, "failed", 2, Clock.GetUtcNow()));
        await w.Stores.DeadLetters.AddAsync(new DeadLetter(
            "dl-2", Scope, new ChunkId("y"), Two, 0, DeadLetterStage.Embed, "failed", 2, Clock.GetUtcNow()));

        var result = await w.Tracker.EraseAsync(new EraseRequest(Scope, One, null, "user asked", "support"), sink);

        Assert.Equal(1, result.VectorsDeleted);
        Assert.True(result.StateRowsDeleted >= 2);
        Assert.Equal(new[] { IdOf(w, Para(2)) }, sink.StoredIds);
        Assert.Null(await w.Stores.DeadLetters.GetAsync("dl-1"));
        Assert.NotNull(await w.Stores.DeadLetters.GetAsync("dl-2"));
        var tombstone = await w.Tracker.GetManifestAsync(Scope, One);
        Assert.True(tombstone!.IsDeleted);
        await AssertClean(w, sink);
    }

    [SkippableFact]
    public async Task Erasing_without_a_document_is_refused()
    {
        var w = NewWorker();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await w.Tracker.EraseAsync(new EraseRequest(Scope, null, null, "user asked", "support"), new RecordingSink()));
    }

    [SkippableFact]
    public async Task Erasing_a_document_that_does_not_exist_is_fine()
    {
        var w = NewWorker();

        var result = await w.Tracker.EraseAsync(new EraseRequest(Scope, One, null, "user asked", "support"), new RecordingSink());

        Assert.Equal(0, result.VectorsDeleted);
    }

    [SkippableFact]
    public async Task An_erase_in_one_scope_leaves_the_other_scope_and_repeating_it_does_nothing()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        var text = Para(1);
        var id = IdOf(w, text);
        await w.Tracker.ReviseAsync(One, "scope-a", Doc(text), w.Embedder, sink);
        await w.Tracker.ReviseAsync(One, "scope-b", Doc(text), w.Embedder, sink);
        Assert.Equal(new[] { id }, await Ids(sink, "scope-a"));
        Assert.Equal(new[] { id }, await Ids(sink, "scope-b"));

        var erased = await w.Tracker.EraseAsync(new EraseRequest("scope-a", One, null, "user asked", "support"), sink);

        Assert.Equal(1, erased.VectorsDeleted);
        Assert.Empty(await Ids(sink, "scope-a"));
        Assert.Equal(new[] { id }, await Ids(sink, "scope-b"));
        await AssertClean(w, sink, "scope-b");

        var again = await w.Tracker.EraseAsync(new EraseRequest("scope-a", One, null, "user asked", "support"), sink);

        Assert.Equal(0, again.VectorsDeleted);
        Assert.Equal(0, again.Documents);
        Assert.Empty(await Ids(sink, "scope-a"));
        Assert.Equal(new[] { id }, await Ids(sink, "scope-b"));
        await AssertClean(w, sink, "scope-b");
    }

    [SkippableFact]
    public async Task A_revision_that_drops_a_shared_paragraph_leaves_the_other_scope()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        var kept = Para(1);
        var shared = Para(2);
        await w.Tracker.ReviseAsync(One, "scope-a", Doc(kept, shared), w.Embedder, sink);
        await w.Tracker.ReviseAsync(One, "scope-b", Doc(kept, shared), w.Embedder, sink);

        var revised = await w.Tracker.ReviseAsync(One, "scope-a", Doc(kept), w.Embedder, sink);

        Assert.Equal(1, revised.Deleted);
        Assert.Equal(new[] { IdOf(w, kept) }, await Ids(sink, "scope-a"));
        Assert.Equal(new[] { IdOf(w, kept), IdOf(w, shared) }.OrderBy(x => x, StringComparer.Ordinal), await Ids(sink, "scope-b"));
        await AssertClean(w, sink, "scope-a");
        await AssertClean(w, sink, "scope-b");
    }

    // ---- verify ----

    [SkippableFact]
    public async Task Verify_reports_a_vector_that_nobody_refers_to_and_a_reference_without_a_vector()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await Revise(w, sink, One, Doc(Para(1), Para(2)));
        await sink.UpsertAsync(new[]
        {
            new EmbeddedChunk(new ChunkId("stray"), One, 1, "text", new float[] { 1, 1 }, "m", Scope),
        });
        await sink.DeleteAsync(Scope, new[] { new ChunkId(IdOf(w, Para(2))) });

        var report = await w.Tracker.VerifyAsync(Scope, sink);

        Assert.False(report.IsClean);
        Assert.Equal(new[] { "stray" }, report.OrphanVectors.Select(i => i.Value));
        Assert.Equal(new[] { IdOf(w, Para(2)) }, report.MissingVectors.Select(i => i.Value));
    }

    // ---- audit ----

    [SkippableFact]
    public async Task The_audit_log_records_what_happened_to_each_chunk()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await Revise(w, sink, One, Doc(Para(1), Para(2)));
        await Revise(w, sink, One, Doc(Para(1), Edit(Para(2), 30), Para(3)));
        await Revise(w, sink, One, Doc(Para(1)));

        var events = w.Audit.Events;

        Assert.Equal(2, events.Count(e => e.Action == ChunkAction.Embed && e.Revision == 1));
        Assert.Contains(events, e => e.Action == ChunkAction.Replace && e.Revision == 2 && e.Chunk.Value == IdOf(w, Edit(Para(2), 30)));
        Assert.Contains(events, e => e.Action == ChunkAction.Embed && e.Revision == 2 && e.Chunk.Value == IdOf(w, Para(3)));
        Assert.Contains(events, e => e.Action == ChunkAction.Delete && e.Revision == 2 && e.Chunk.Value == IdOf(w, Para(2)));
        Assert.Equal(2, events.Count(e => e.Action == ChunkAction.Delete && e.Revision == 3));
        Assert.All(events, e => Assert.Equal(One, e.Document));
        Assert.All(events, e => Assert.Equal("test-model", e.EmbeddingModel));
    }

    [SkippableFact]
    public async Task A_failing_audit_log_does_not_undo_a_revision()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        var tracker = new RevisionTracker(w.Pipeline, w.Revisions, w.Stores.DeadLetters, new BrokenAuditLog(), time: Clock);

        var result = await tracker.ReviseAsync(One, Scope, Doc(Para(1)), w.Embedder, sink);

        Assert.Equal(RevisionStatus.Applied, result.Status);
        Assert.Equal(1, (await tracker.GetManifestAsync(Scope, One))!.Revision);
    }

    private sealed class BrokenAuditLog : IAuditLog
    {
        public ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken ct = default) => throw new IOException("the log is down");

        public async IAsyncEnumerable<AuditEvent> QueryAsync(AuditQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    // ---- setup ----

    [SkippableFact]
    public void A_tracker_needs_a_fingerprint_store_on_the_pipeline()
    {
        var pipeline = RechunkPipeline.CreateBuilder().Build();

        Assert.Throws<InvalidOperationException>(() =>
            new RevisionTracker(pipeline, new InMemoryRevisionStore(), new InMemoryDeadLetterStore()));
    }

    [SkippableFact]
    public async Task The_swept_chunks_do_not_touch_vectors_of_other_scopes()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await Revise(w, sink, One, Doc(Para(1)));
        var other = await w.Tracker.ReviseAsync(One, "tenant-b", Doc(Para(1)), w.Embedder, sink);
        Assert.Equal(RevisionStatus.Applied, other.Status);

        await w.Tracker.DeleteDocumentAsync(One, Scope, w.Embedder, sink);

        // the sink keys vectors by id only, so the other scope's refs are what keeps the report honest
        Assert.Single(await w.Stores.Revisions.ListRefsAsync("tenant-b"));
        Assert.Empty(await w.Stores.Revisions.ListRefsAsync(Scope));
    }
}
