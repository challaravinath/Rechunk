using Xunit;

namespace Rechunk.Core.Tests.Store;

// Audit trail, PII and erasure through the revision tracker, on every backend.
public abstract partial class RevisionTrackerContract
{
    private static readonly string[] Wiki = { "wiki" };

    private static Task<RevisionResult> ReviseFrom(Worker w, RecordingSink sink, DocumentId doc, string source, params string[] texts) =>
        w.Tracker.ReviseAsync(doc, Scope, Doc(texts), w.Embedder, sink, null, default, source).AsTask();

    private static async Task<List<AuditEvent>> Trail(Worker w, AuditQuery? query = null)
    {
        await w.Tracker.FlushAuditAsync();
        var list = new List<AuditEvent>();
        await foreach (var e in w.Audit.QueryAsync(query ?? new AuditQuery()))
        {
            list.Add(e);
        }

        return list;
    }

    private static string AllJson(IEnumerable<AuditEvent> events) => string.Join("\n", events.Select(AuditEventJson.Serialize));

    private static void AssertNoText(IEnumerable<AuditEvent> events, params string[] texts)
    {
        var json = AllJson(events);
        foreach (var text in texts)
        {
            // a few words from the middle are enough to show a chunk got in
            var sample = string.Join(' ', text.Split(' ').Skip(20).Take(4));
            Assert.DoesNotContain(sample, json, StringComparison.Ordinal);
        }
    }

    // ---- audit trail ----

    [SkippableFact]
    public async Task A_new_chunk_is_audited_with_document_revision_stage_scope_and_source_and_without_its_text()
    {
        var w = NewWorker();
        var sink = new RecordingSink();

        await ReviseFrom(w, sink, One, "wiki", Para(1), Para(2));

        var events = await Trail(w);
        Assert.Equal(2, events.Count);
        Assert.All(events, e =>
        {
            Assert.Equal(AuditKind.New, e.Kind);
            Assert.Equal(One, e.Document);
            Assert.Equal(1, e.Revision);
            Assert.Equal("wiki", e.Source);
            Assert.Equal(Scope, e.Scope);
            Assert.Equal(AuditStages.Embed, e.Stage);
            Assert.Equal("test-model", e.EmbeddingModel);
            Assert.Equal(Clock.GetUtcNow().ToUnixTimeMilliseconds(), e.At.ToUnixTimeMilliseconds());
        });
        Assert.Contains(events, e => e.Chunk.Value == IdOf(w, Para(1)));
        AssertNoText(events, Para(1), Para(2));
    }

    [SkippableFact]
    public async Task An_edit_is_audited_as_a_revised_chunk_and_a_deleted_one()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1), Para(2));
        var edited = Edit(Para(2), 100);

        await ReviseFrom(w, sink, One, "wiki", Para(1), edited);

        var second = (await Trail(w)).Where(e => e.Revision == 2).ToList();
        var revised = Assert.Single(second, e => e.Kind == AuditKind.Revised);
        Assert.Equal(IdOf(w, edited), revised.Chunk.Value);
        var deleted = Assert.Single(second, e => e.Kind == AuditKind.Deleted);
        Assert.Equal(IdOf(w, Para(2)), deleted.Chunk.Value);
        Assert.DoesNotContain(second, e => e.Chunk.Value == IdOf(w, Para(1)));
    }

    [SkippableFact]
    public async Task Deleting_a_document_audits_every_chunk_that_went_with_it()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1), Para(2));

        await w.Tracker.DeleteDocumentAsync(One, Scope, w.Embedder, sink);

        var deleted = (await Trail(w, new AuditQuery { Kind = AuditKind.Deleted })).Select(e => e.Chunk.Value).OrderBy(x => x).ToList();
        Assert.Equal(new[] { IdOf(w, Para(1)), IdOf(w, Para(2)) }.OrderBy(x => x), deleted);
    }

    [SkippableFact]
    public async Task A_chunk_another_document_still_uses_is_not_audited_as_deleted()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1), Para(2));
        await ReviseFrom(w, sink, Two, "drive", Para(2));

        await w.Tracker.DeleteDocumentAsync(One, Scope, w.Embedder, sink);

        var deleted = await Trail(w, new AuditQuery { Kind = AuditKind.Deleted });
        Assert.Equal(IdOf(w, Para(1)), Assert.Single(deleted).Chunk.Value);
    }

    [SkippableFact]
    public async Task A_repeat_under_the_skip_policy_is_audited_as_skipped_and_points_at_the_chunk_it_matched()
    {
        var w = NewWorker(DuplicatePolicy.Skip);
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1));

        await ReviseFrom(w, sink, Two, "drive", Para(1));

        var skipped = Assert.Single(await Trail(w, new AuditQuery(Document: Two)));
        Assert.Equal(AuditKind.Skipped, skipped.Kind);
        Assert.Equal(IdOf(w, Para(1)), skipped.Related!.Value.Value);
        Assert.Equal(AuditStages.Exact, skipped.Stage);
        Assert.Equal("drive", skipped.Source);
    }

    [SkippableFact]
    public async Task A_near_copy_under_the_alias_policy_is_audited_as_aliased_with_the_stage_that_found_it()
    {
        var w = NewWorker(DuplicatePolicy.Alias);
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1));

        await ReviseFrom(w, sink, Two, "drive", Edit(Para(1), 100));

        var aliased = Assert.Single(await Trail(w, new AuditQuery { Kind = AuditKind.Aliased }));
        Assert.Equal(IdOf(w, Para(1)), aliased.Related!.Value.Value);
        Assert.Contains(aliased.Stage, new[] { AuditStages.SimHash, AuditStages.MinHash });
    }

    [SkippableFact]
    public async Task A_near_copy_under_the_flag_policy_is_audited_as_flagged_and_embedded()
    {
        var w = NewWorker(DuplicatePolicy.Flag);
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1));

        await ReviseFrom(w, sink, Two, "drive", Edit(Para(1), 100));

        var flagged = Assert.Single(await Trail(w, new AuditQuery { Kind = AuditKind.Flagged }));
        Assert.Equal(IdOf(w, Para(1)), flagged.Related!.Value.Value);
        Assert.Equal(2, sink.StoredIds.Count);
    }

    [SkippableFact]
    public async Task A_chunk_that_could_not_be_embedded_is_audited_as_dead_lettered()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        w.Embedder.Hook = (_, _) => throw new InvalidOperationException("model is down");

        var result = await ReviseFrom(w, sink, One, "wiki", Para(1));

        Assert.Equal(RevisionStatus.Incomplete, result.Status);
        var letter = Assert.Single(await Trail(w, new AuditQuery { Kind = AuditKind.DeadLettered }));
        Assert.Equal(AuditStages.Embed, letter.Stage);
        Assert.Equal(One, letter.Document);
        Assert.Equal(IdOf(w, Para(1)), letter.Chunk.Value);
    }

    [SkippableFact]
    public async Task The_trail_can_be_read_by_document_chunk_source_and_time()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1));
        Clock.Advance(TimeSpan.FromHours(1));
        var later = Clock.GetUtcNow();
        await ReviseFrom(w, sink, Two, "drive", Para(2), Para(3));

        Assert.Single(await Trail(w, new AuditQuery(Document: One)));
        Assert.Equal(2, (await Trail(w, new AuditQuery { Source = "drive" })).Count);
        Assert.Single(await Trail(w, new AuditQuery(Chunk: new ChunkId(IdOf(w, Para(1))))));
        Assert.Equal(2, (await Trail(w, new AuditQuery(From: later))).Count);
        Assert.Single(await Trail(w, new AuditQuery(To: later.AddMinutes(-1))));
    }

    [SkippableFact]
    public async Task A_finding_is_audited_by_type_and_span_and_the_value_is_not_in_the_trail()
    {
        var w = NewWorker(configure: o => o.Pii.Enabled = true);
        var sink = new RecordingSink();
        var text = Para(1) + " card 4111 1111 1111 1111 end";

        await ReviseFrom(w, sink, One, "wiki", text);

        var e = Assert.Single(await Trail(w));
        var hit = Assert.Single(e.Findings);
        Assert.Equal(PiiType.Card, hit.Type);
        Assert.Equal("4111 1111 1111 1111", text.Substring(hit.Start, hit.Length));
        Assert.DoesNotContain("4111", AllJson(new[] { e }), StringComparison.Ordinal);
        Assert.Single(await Trail(w, new AuditQuery { FindingType = PiiType.Card }));
        Assert.Empty(await Trail(w, new AuditQuery { FindingType = PiiType.Email }));
    }

    [SkippableFact]
    public async Task A_masked_chunk_is_stored_and_embedded_without_the_value()
    {
        var w = NewWorker(configure: o =>
        {
            o.Pii.Enabled = true;
            o.Pii.Set(PiiType.Card, PiiPolicy.Mask);
        });
        var sink = new RecordingSink();

        await ReviseFrom(w, sink, One, "wiki", Para(1) + " card 4111 1111 1111 1111 end");

        Assert.DoesNotContain(w.Embedded, t => t.Contains("4111", StringComparison.Ordinal));
        Assert.All(sink.StoredIds, id => Assert.Contains("[CARD]", sink.Get(id)!.Text, StringComparison.Ordinal));
        Assert.Equal(PiiPolicy.Mask, Assert.Single(Assert.Single(await Trail(w)).Findings).Applied);
        await AssertClean(w, sink);
    }

    [SkippableFact]
    public async Task A_blocked_chunk_is_audited_and_never_stored_embedded_or_in_the_manifest()
    {
        var w = NewWorker(configure: o =>
        {
            o.Pii.Enabled = true;
            o.Pii.Set(PiiType.Card, PiiPolicy.Block);
        });
        var sink = new RecordingSink();

        var result = await ReviseFrom(w, sink, One, "wiki", Para(1), Para(2) + " card 4111 1111 1111 1111 end");

        Assert.Equal(RevisionStatus.Applied, result.Status);
        Assert.Equal(1, result.PiiBlocked);
        Assert.Equal(new[] { Para(1) }, w.Embedded);
        Assert.Single(sink.StoredIds);
        Assert.Single((await w.Tracker.GetManifestAsync(Scope, One))!.Entries);
        var blocked = Assert.Single(await Trail(w, new AuditQuery { Kind = AuditKind.Blocked }));
        Assert.Equal(AuditStages.Pii, blocked.Stage);
        Assert.Equal(PiiType.Card, Assert.Single(blocked.Findings).Type);
        Assert.DoesNotContain("4111", AllJson(new[] { blocked }), StringComparison.Ordinal);
        await AssertClean(w, sink);
    }

    [SkippableFact]
    public async Task A_failing_audit_log_does_not_fail_the_revision_and_the_events_are_sent_later()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        var log = new SwitchableAuditLog();
        var tracker = new RevisionTracker(
            w.Pipeline, w.Revisions, w.Stores.DeadLetters, time: Clock,
            writer: new AuditWriter(log, new InMemoryAuditOutbox(), new AuditOptions { RetryDelay = TimeSpan.Zero, MaxAttempts = 2 }, time: Clock));
        log.Down = true;

        var result = await tracker.ReviseAsync(One, Scope, Doc(Para(1), Para(2)), w.Embedder, sink, null, default, "wiki");

        Assert.Equal(RevisionStatus.Applied, result.Status);
        Assert.Equal(2, result.AuditPending);
        Assert.Equal(2, sink.StoredIds.Count);
        Assert.Empty(log.Inner.Events);

        log.Down = false;
        Assert.Equal(0, await tracker.FlushAuditAsync());
        Assert.Equal(2, log.Inner.Events.Count);
    }

    [SkippableFact]
    public async Task A_tracker_without_an_audit_log_works_and_reports_that_it_is_not_audited()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        var tracker = new RevisionTracker(w.Pipeline, w.Revisions, w.Stores.DeadLetters, time: Clock);

        var result = await tracker.ReviseAsync(One, Scope, Doc(Para(1)), w.Embedder, sink, null, default, "wiki");

        Assert.False(tracker.Audited);
        Assert.Equal(RevisionStatus.Applied, result.Status);
        Assert.Equal(0, result.AuditPending);
    }

    private sealed class SwitchableAuditLog : IAuditLog
    {
        public InMemoryAuditLog Inner { get; } = new();

        public bool Down { get; set; }

        public ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken ct = default) =>
            Down ? throw new IOException("audit store is down") : Inner.AppendAsync(auditEvent, ct);

        public IAsyncEnumerable<AuditEvent> QueryAsync(AuditQuery query, CancellationToken ct = default) => Inner.QueryAsync(query, ct);
    }

    // ---- the source is kept ----

    [SkippableFact]
    public async Task The_source_is_kept_with_the_document_and_a_revision_without_one_does_not_forget_it()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1));

        await w.Tracker.ReviseAsync(One, Scope, Doc(Para(1), Para(2)), w.Embedder, sink);

        Assert.Equal("wiki", (await w.Tracker.GetManifestAsync(Scope, One))!.Source);
        var docs = await w.Stores.Revisions.ListDocumentsAsync(Scope, "wiki", null, 10);
        Assert.Equal(One, Assert.Single(docs).Document);
        Assert.Equal(2, Assert.Single(docs).Revision);
    }

    [SkippableFact]
    public async Task A_document_can_move_to_another_source()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1));

        await ReviseFrom(w, sink, One, "drive", Para(1), Para(2));

        Assert.Empty(await w.Stores.Revisions.ListDocumentsAsync(Scope, "wiki", null, 10));
        Assert.Single(await w.Stores.Revisions.ListDocumentsAsync(Scope, "drive", null, 10));
    }

    [SkippableFact]
    public async Task Documents_are_listed_in_pages_by_source_and_scope()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        for (var i = 0; i < 7; i++)
        {
            await ReviseFrom(w, sink, new DocumentId("doc-" + i), "wiki", Para(100 + i));
        }

        await ReviseFrom(w, sink, new DocumentId("other"), "drive", Para(200));

        var first = await w.Stores.Revisions.ListDocumentsAsync(Scope, "wiki", null, 3);
        var second = await w.Stores.Revisions.ListDocumentsAsync(Scope, "wiki", first[^1].Document.Value, 3);
        var third = await w.Stores.Revisions.ListDocumentsAsync(Scope, "wiki", second[^1].Document.Value, 3);
        var all = await w.Stores.Revisions.ListDocumentsAsync(Scope, null, null, 100);

        Assert.Equal(3, first.Count);
        Assert.Equal(3, second.Count);
        Assert.Single(third);
        Assert.Equal(7, first.Concat(second).Concat(third).Select(d => d.Document.Value).Distinct().Count());
        Assert.Equal(8, all.Count);
        Assert.Equal(all.Select(d => d.Document.Value).OrderBy(x => x, StringComparer.Ordinal), all.Select(d => d.Document.Value));
        Assert.Empty(await w.Stores.Revisions.ListDocumentsAsync("other-scope", null, null, 10));
    }

    [SkippableFact]
    public async Task Documents_are_found_by_the_chunks_they_use()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1), Para(2));
        await ReviseFrom(w, sink, Two, "wiki", Para(2), Para(3));

        var users = await w.Stores.Revisions.ListDocumentsUsingAsync(Scope, new[] { new ChunkId(IdOf(w, Para(2))) }, null, 10);
        var one = await w.Stores.Revisions.ListDocumentsUsingAsync(Scope, new[] { new ChunkId(IdOf(w, Para(1))) }, null, 10);
        var none = await w.Stores.Revisions.ListDocumentsUsingAsync(Scope, new[] { new ChunkId("ffff") }, null, 10);

        Assert.Equal(new[] { "doc-1", "doc-2" }, users.Select(d => d.Document.Value));
        Assert.Equal(new[] { "doc-1" }, one.Select(d => d.Document.Value));
        Assert.Empty(none);
    }

    [SkippableFact]
    public async Task A_document_that_dropped_a_chunk_is_no_longer_found_by_it()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1), Para(2));
        await ReviseFrom(w, sink, One, "wiki", Para(1));

        var users = await w.Stores.Revisions.ListDocumentsUsingAsync(Scope, new[] { new ChunkId(IdOf(w, Para(2))) }, null, 10);

        Assert.Empty(users);
    }

    // ---- erase by source and document ----

    [SkippableFact]
    public async Task Erasing_a_source_erases_its_documents_and_leaves_other_sources_alone()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1), Para(2));
        await ReviseFrom(w, sink, Two, "wiki", Para(3));
        await ReviseFrom(w, sink, new DocumentId("doc-3"), "drive", Para(4));

        var result = await w.Tracker.EraseAsync(new EraseRequest(Scope, null, "wiki", "customer left", "support"), sink);

        Assert.Equal(2, result.Documents);
        Assert.Equal(3, result.VectorsDeleted);
        Assert.Equal(new[] { IdOf(w, Para(4)) }, sink.StoredIds);
        Assert.True((await w.Tracker.GetManifestAsync(Scope, One))!.IsDeleted);
        Assert.False((await w.Tracker.GetManifestAsync(Scope, new DocumentId("doc-3")))!.IsDeleted);
        await AssertClean(w, sink);

        // nothing about the erased text is left to match
        w.Embedded.Clear();
        await ReviseFrom(w, sink, new DocumentId("doc-4"), "wiki", Para(1));
        Assert.Equal(new[] { Para(1) }, w.Embedded);
    }

    [SkippableFact]
    public async Task A_chunk_that_a_document_outside_the_source_uses_survives_an_erase_by_source()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1), Para(2));
        await ReviseFrom(w, sink, Two, "drive", Para(2));

        var result = await w.Tracker.EraseAsync(new EraseRequest(Scope, null, "wiki", "customer left", "support"), sink);

        Assert.Equal(1, result.VectorsDeleted);
        Assert.Equal(new[] { IdOf(w, Para(2)) }, sink.StoredIds);
        await AssertClean(w, sink);
    }

    [SkippableFact]
    public async Task Erasing_a_source_with_more_documents_than_fit_in_a_page_gets_them_all()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        const int count = 430;
        for (var i = 0; i < count; i++)
        {
            await ReviseFrom(w, sink, new DocumentId("bulk-" + i.ToString("0000")), "bulk", "unique text number " + i + " " + string.Join(' ', TestText.Words(i, 12)));
        }

        await ReviseFrom(w, sink, One, "keep", Para(1));

        var result = await w.Tracker.EraseAsync(new EraseRequest(Scope, null, "bulk", "bulk erase", "ops"), sink);

        Assert.Equal(count, result.Documents);
        Assert.Equal(count, result.VectorsDeleted);
        Assert.Single(sink.StoredIds);
        Assert.Empty(await w.Stores.Revisions.ListDocumentsAsync(Scope, "bulk", null, 1000) is { } docs
            ? docs.Where(d => d.Revision > 0 && !IsErased(w, d).Result).ToList()
            : new List<DocumentRef>());
    }

    private static async Task<bool> IsErased(Worker w, DocumentRef doc) =>
        (await w.Tracker.GetManifestAsync(Scope, doc.Document))!.IsDeleted;

    [SkippableFact]
    public async Task Naming_a_document_and_a_source_that_do_not_belong_together_erases_nothing()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await w.Tracker.EraseAsync(new EraseRequest(Scope, One, "drive", "x", "support"), sink));

        Assert.Single(sink.StoredIds);
        Assert.False((await w.Tracker.GetManifestAsync(Scope, One))!.IsDeleted);

        var ok = await w.Tracker.EraseAsync(new EraseRequest(Scope, One, "wiki", "x", "support"), sink);
        Assert.Equal(1, ok.Documents);
        Assert.Empty(sink.StoredIds);
    }

    [SkippableFact]
    public async Task Erasing_leaves_a_receipt_with_counts_and_no_content()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1), Para(2));

        var result = await w.Tracker.EraseAsync(new EraseRequest(Scope, One, null, "ticket 4711", "support-agent"), sink);

        Assert.NotNull(result.ReceiptId);
        var trail = await Trail(w);
        var receipt = Assert.Single(trail, e => e.Kind == AuditKind.ErasureReceipt);
        Assert.Equal(result.ReceiptId, receipt.Id);
        Assert.Equal(AuditStages.Erasure, receipt.Stage);
        Assert.Equal("support-agent", receipt.Actor);
        Assert.Equal("ticket 4711", receipt.Reason);
        Assert.Equal(1, receipt.Counts["documents"]);
        Assert.Equal(2, receipt.Counts["chunks"]);
        Assert.Equal(2, receipt.Counts["vectors"]);
        Assert.Equal(result.StateRowsDeleted, receipt.Counts["state_rows"]);
        AssertNoText(trail, Para(1), Para(2));
    }

    [SkippableFact]
    public async Task Every_chunk_that_an_erase_removed_is_audited_as_erased_with_who_asked()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1), Para(2));

        await w.Tracker.EraseAsync(new EraseRequest(Scope, One, null, "x", "support-agent"), sink);

        var erased = await Trail(w, new AuditQuery { Kind = AuditKind.Erased });
        Assert.Equal(2, erased.Count);
        Assert.All(erased, e =>
        {
            Assert.Equal("support-agent", e.Actor);
            Assert.Equal(One, e.Document);
            Assert.Equal("wiki", e.Source);
        });
        Assert.DoesNotContain(await Trail(w), e => e.Kind == AuditKind.Deleted);
    }

    [SkippableFact]
    public async Task An_erase_without_an_audit_log_works_and_has_no_receipt()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1));
        var tracker = new RevisionTracker(w.Pipeline, w.Revisions, w.Stores.DeadLetters, time: Clock);

        var result = await tracker.EraseAsync(new EraseRequest(Scope, One, null, "x", "support"), sink);

        Assert.Null(result.ReceiptId);
        Assert.Empty(sink.StoredIds);
    }

    [SkippableFact]
    public async Task An_erase_stays_inside_its_scope()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1));

        var result = await w.Tracker.EraseAsync(new EraseRequest("tenant-b", null, "wiki", "x", "support"), sink);

        Assert.Equal(0, result.Documents);
        Assert.Single(sink.StoredIds);
    }

    [SkippableFact]
    public async Task A_failed_delete_ends_the_erase_with_an_error_and_an_incomplete_receipt_and_a_retry_finishes_it()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1), Para(2));
        var fail = true;
        sink.OnDelete = _ => fail ? new IOException("vector store is down") : null;

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await w.Tracker.EraseAsync(new EraseRequest(Scope, null, "wiki", "ticket 9", "support"), sink));

        var receipt = Assert.Single(await Trail(w, new AuditQuery { Kind = AuditKind.ErasureReceipt }));
        Assert.True(receipt.Counts.ContainsKey("incomplete"));
        Assert.Equal(2, sink.StoredIds.Count);

        fail = false;
        Clock.Advance(TimeSpan.FromMinutes(10));
        var again = await w.Tracker.EraseAsync(new EraseRequest(Scope, null, "wiki", "ticket 9", "support"), sink);
        await w.Tracker.SweepAsync(Scope, sink);

        Assert.Empty(sink.StoredIds);
        Assert.NotNull(again.ReceiptId);
        await AssertClean(w, sink);
    }

    // ---- erase by text, chunk and finding ----

    [SkippableFact]
    public async Task Erasing_by_text_removes_the_chunk_that_holds_it_from_every_document()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1), Para(2));
        await ReviseFrom(w, sink, Two, "drive", Para(2), Para(3));

        var result = await w.Tracker.EraseTextAsync(Scope, Para(2), sink, "customer asked", "support");

        Assert.Equal(2, result.Documents);
        Assert.Equal(1, result.VectorsDeleted);
        Assert.Equal(new[] { IdOf(w, Para(1)), IdOf(w, Para(3)) }.OrderBy(x => x), sink.StoredIds.OrderBy(x => x));
        Assert.Empty(await w.Tracker.FindByTextAsync(Scope, Para(2)));
        Assert.NotNull(result.ReceiptId);
        await AssertClean(w, sink);
    }

    [SkippableFact]
    public async Task Erasing_by_text_finds_a_near_copy_only_when_asked_to()
    {
        var w = NewWorker(DuplicatePolicy.Flag);
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1));
        await ReviseFrom(w, sink, Two, "drive", Edit(Para(1), 100));

        var exact = await w.Tracker.EraseTextAsync(Scope, Para(1), sink, "x", "support");
        Assert.Equal(1, exact.VectorsDeleted);
        Assert.Single(sink.StoredIds);

        var near = await w.Tracker.EraseTextAsync(Scope, Para(1), sink, "x", "support", mode: TextMatch.Near);
        Assert.Equal(1, near.VectorsDeleted);
        Assert.Empty(sink.StoredIds);
        await AssertClean(w, sink);
    }

    [SkippableFact]
    public async Task Erasing_text_that_is_not_stored_does_nothing()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1));

        var result = await w.Tracker.EraseTextAsync(Scope, Para(9), sink, "x", "support");

        Assert.Equal(0, result.VectorsDeleted);
        Assert.Single(sink.StoredIds);
    }

    [SkippableFact]
    public async Task Erasing_chunks_by_id_removes_them_from_the_documents_that_use_them()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1), Para(2));

        var result = await w.Tracker.EraseChunksAsync(Scope, new[] { new ChunkId(IdOf(w, Para(1))) }, sink, "x", "support");

        Assert.Equal(1, result.Documents);
        Assert.Equal(1, result.VectorsDeleted);
        Assert.Equal(new[] { IdOf(w, Para(2)) }, sink.StoredIds);
        var manifest = await w.Tracker.GetManifestAsync(Scope, One);
        Assert.Single(manifest!.Entries);
        Assert.Equal(2, manifest.Revision);
        await AssertClean(w, sink);
    }

    [SkippableFact]
    public async Task Erasing_by_a_finding_removes_every_chunk_that_had_one_of_that_type()
    {
        var w = NewWorker(configure: o => o.Pii.Enabled = true);
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1) + " card 4111 1111 1111 1111 end", Para(2));
        await ReviseFrom(w, sink, Two, "drive", Para(3) + " mail jane@example.org end", Para(4) + " card 5555 5555 5555 4444 end");

        var found = await w.Tracker.FindByFindingAsync(Scope, PiiType.Card);
        Assert.Equal(2, found.Count);

        var result = await w.Tracker.EraseFindingsAsync(Scope, PiiType.Card, sink, "card leak", "security");

        Assert.Equal(2, result.VectorsDeleted);
        Assert.Equal(2, sink.StoredIds.Count);
        Assert.Contains(IdOf(w, Para(2)), sink.StoredIds);
        Assert.Empty(await w.Tracker.FindByFindingAsync(Scope, PiiType.Card) is { } left ? left.Where(id => sink.StoredIds.Contains(id.Value)) : Array.Empty<ChunkId>());
        await AssertClean(w, sink);
    }

    [SkippableFact]
    public async Task Finding_by_type_needs_an_audit_log()
    {
        var w = NewWorker();
        var tracker = new RevisionTracker(w.Pipeline, w.Revisions, w.Stores.DeadLetters, time: Clock);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await tracker.FindByFindingAsync(Scope, PiiType.Card));
    }

    [SkippableFact]
    public async Task Erasing_with_no_document_and_no_source_is_refused()
    {
        var w = NewWorker();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await w.Tracker.EraseAsync(new EraseRequest(Scope, null, "  ", "x", "support"), new RecordingSink()));
    }

    // ---- the skip policy edge case ----

    [SkippableFact]
    public async Task Under_the_skip_policy_an_erase_leaves_no_exact_row_behind_for_a_text_that_was_skipped()
    {
        var w = NewWorker(DuplicatePolicy.Skip);
        var sink = new RecordingSink();
        var near = Edit(Para(1), 100);
        await ReviseFrom(w, sink, One, "wiki", Para(1));
        var skipped = await ReviseFrom(w, sink, Two, "drive", near);
        Assert.Equal(1, skipped.Skipped);
        Assert.Single(sink.StoredIds);

        // the skipped text has an exact row of its own that points at the stored chunk
        Assert.NotEmpty(await w.Tracker.FindByTextAsync(Scope, near));

        await w.Tracker.EraseAsync(new EraseRequest(Scope, null, "wiki", "x", "support"), sink);
        await w.Tracker.EraseAsync(new EraseRequest(Scope, null, "drive", "x", "support"), sink);

        Assert.Empty(sink.StoredIds);
        Assert.Empty(await w.Tracker.FindByTextAsync(Scope, near));
        Assert.Empty(await w.Tracker.FindByTextAsync(Scope, Para(1)));

        // the text is new again: it is embedded and stored
        w.Embedded.Clear();
        var again = await ReviseFrom(w, sink, new DocumentId("doc-3"), "drive", near);
        Assert.Equal(1, again.Embedded);
        Assert.Equal(new[] { near }, w.Embedded);
        await AssertClean(w, sink);
    }

    [SkippableFact]
    public async Task Erasing_the_document_that_skipped_a_text_forgets_that_text_and_keeps_the_original()
    {
        var w = NewWorker(DuplicatePolicy.Skip);
        var sink = new RecordingSink();
        var near = Edit(Para(1), 100);
        await ReviseFrom(w, sink, One, "wiki", Para(1));
        await ReviseFrom(w, sink, Two, "drive", near);

        await w.Tracker.EraseAsync(new EraseRequest(Scope, Two, null, "x", "support"), sink);

        Assert.Equal(new[] { IdOf(w, Para(1)) }, sink.StoredIds);
        var rows = await w.Tracker.FindByTextAsync(Scope, near);
        Assert.DoesNotContain(rows, id => id.Value == IdOf(w, near));
        Assert.Equal(new[] { IdOf(w, Para(1)) }, (await w.Tracker.FindByTextAsync(Scope, Para(1))).Select(i => i.Value));
        await AssertClean(w, sink);
    }

    [SkippableFact]
    public async Task An_alias_that_two_documents_share_is_forgotten_only_when_the_last_of_them_goes()
    {
        var w = NewWorker(DuplicatePolicy.Skip);
        var sink = new RecordingSink();
        var near = Edit(Para(1), 100);
        await ReviseFrom(w, sink, One, "wiki", Para(1));
        await ReviseFrom(w, sink, Two, "drive", near);
        await ReviseFrom(w, sink, new DocumentId("doc-3"), "drive", near);

        await w.Tracker.EraseAsync(new EraseRequest(Scope, Two, null, "x", "support"), sink);
        Assert.NotEmpty(await w.Tracker.FindByTextAsync(Scope, near));

        await w.Tracker.EraseAsync(new EraseRequest(Scope, new DocumentId("doc-3"), null, "x", "support"), sink);
        await w.Tracker.EraseAsync(new EraseRequest(Scope, One, null, "x", "support"), sink);

        Assert.Empty(sink.StoredIds);
        Assert.Empty(await w.Tracker.FindByTextAsync(Scope, near));
    }

    [SkippableFact]
    public async Task Re_ingesting_erased_text_brings_it_back_as_new()
    {
        var w = NewWorker();
        var sink = new RecordingSink();
        await ReviseFrom(w, sink, One, "wiki", Para(1));
        await w.Tracker.EraseAsync(new EraseRequest(Scope, One, null, "x", "support"), sink);
        Assert.Empty(sink.StoredIds);

        w.Embedded.Clear();
        var result = await ReviseFrom(w, sink, One, "wiki", Para(1));

        Assert.Equal(RevisionStatus.Applied, result.Status);
        Assert.Equal(new[] { Para(1) }, w.Embedded);
        Assert.Single(sink.StoredIds);
        await AssertClean(w, sink);
    }
}
