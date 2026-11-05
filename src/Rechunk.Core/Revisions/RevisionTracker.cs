using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rechunk;

/// <summary>What one sweep did.</summary>
public sealed record SweepResult(IReadOnlyList<ChunkId> Retired, int FingerprintRowsRemoved, int Failed);

/// <summary>
/// Revision tracking and clean deletes. Give it the new version of a document, split into chunks, and it works out what
/// changed. A chunk whose text is already in the document (also when it moved) is kept as it is and not embedded again.
/// New and edited chunks go through the normal ingest. When that worked, the document is switched to the new revision in
/// one atomic step, and every chunk that no document uses any more is deleted: the vector, the fingerprints in all three
/// dedup stages, and its entry in the revision store. A chunk that two documents share is deleted when the last one lets go.
///
/// All of it goes through the shared stores, so it is safe with many stateless workers. The order is chosen so a crash
/// leaves nothing half done that a later run can not finish: vectors are written first, the document switches second,
/// and old vectors are removed last. A revision that did not finish leaves the document on its old revision, and running
/// the same revision again reuses what was stored. A chunk is deleted by one sweeper at a time, and a sweeper that dies
/// leaves a lease that runs out, after which another one finishes the job.
///
/// It needs a fingerprint store on the pipeline (UseInMemoryFingerprintStore, or one of the storage packages), because
/// forgetting a chunk means removing its shared fingerprints.
/// </summary>
public sealed partial class RevisionTracker : IRevisionTracker
{
    private readonly RechunkPipeline _pipeline;
    private readonly IFingerprintStore _fingerprints;
    private readonly IRevisionStore _store;
    private readonly IDeadLetterStore _deadLetters;
    private readonly AuditWriter? _audit;
    private readonly RechunkOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public RevisionTracker(
        RechunkPipeline pipeline,
        IRevisionStore store,
        IDeadLetterStore deadLetters,
        IAuditLog? audit = null,
        ILogger? logger = null,
        TimeProvider? time = null,
        AuditWriter? writer = null)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _deadLetters = deadLetters ?? throw new ArgumentNullException(nameof(deadLetters));
        _fingerprints = pipeline.Store
            ?? throw new InvalidOperationException(
                "Revision tracking needs a fingerprint store on the pipeline, so deleted chunks can be forgotten. "
                + "Add one with UseInMemoryFingerprintStore() or one of the storage packages.");
        _options = pipeline.Options;
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _audit = writer ?? (audit is null ? null : new AuditWriter(audit, new InMemoryAuditOutbox(), _options.Audit, _logger, _time));
    }

    /// <summary>
    /// Writes the audit events that could not be written when they happened (the audit log was down, so they wait in the
    /// outbox). Returns how many are still waiting. It runs by itself at the start of every revision, erase and sweep.
    /// </summary>
    public async ValueTask<int> FlushAuditAsync(CancellationToken ct = default)
    {
        if (_audit is null)
        {
            return 0;
        }

        await _audit.DrainAsync(ct).ConfigureAwait(false);
        return await _audit.PendingAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The state of a document: its revision and the chunks in it, or null when it was never revised.</summary>
    public ValueTask<DocumentManifest?> GetManifestAsync(string scope, DocumentId document, CancellationToken ct = default) =>
        _store.GetAsync(scope, document, ct);

    public async ValueTask<RevisionPlan> PlanAsync(
        DocumentId document, string scope, IReadOnlyList<ChunkInput> newChunks, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(newChunks);

        var manifest = await _store.GetAsync(scope, document, ct).ConfigureAwait(false);
        var old = manifest?.Entries ?? Array.Empty<ManifestEntry>();
        var from = manifest?.Revision ?? 0;
        var items = (await IdentifyAsync(newChunks, ct).ConfigureAwait(false)).Items;
        var oldById = FirstById(old);
        var newIds = items.Select(i => i.Id.Value).ToHashSet(StringComparer.Ordinal);
        var oldByOrdinal = old.GroupBy(e => e.Ordinal).ToDictionary(g => g.Key, g => g.First());

        var planned = new List<PlannedChunk>(items.Count);
        foreach (var item in items)
        {
            if (oldById.TryGetValue(item.Id.Value, out var existing))
            {
                var reason = existing.Ordinal == item.Input.Ordinal
                    ? "text is the same"
                    : $"text is the same, moved from position {existing.Ordinal}";
                planned.Add(new PlannedChunk(item.Input, item.Id, ChunkAction.Keep, null, reason));
            }
            else if (oldByOrdinal.TryGetValue(item.Input.Ordinal, out var replaced) && !newIds.Contains(replaced.Id.Value))
            {
                planned.Add(new PlannedChunk(item.Input, item.Id, ChunkAction.Replace, null, "text changed at this position"));
            }
            else
            {
                planned.Add(new PlannedChunk(item.Input, item.Id, ChunkAction.Embed, null, "new text"));
            }
        }

        var unchanged = IsSame(old, items);
        var dropped = DroppedTargets(old, newIds);
        return new RevisionPlan(document, scope, from, unchanged ? from : from + 1, planned, dropped.Select(t => new ChunkId(t)).ToList());
    }

    public ValueTask<RevisionResult> ApplyAsync(
        RevisionPlan plan, IChunkEmbedder embedder, IVectorSink sink, CancellationToken ct = default) =>
        ApplyAsync(plan, embedder, sink, null, ct);

    /// <summary>Carries out a plan for a document that comes from the given source (see ReviseAsync).</summary>
    public async ValueTask<RevisionResult> ApplyAsync(
        RevisionPlan plan, IChunkEmbedder embedder, IVectorSink sink, string? source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var inputs = plan.Chunks.Select(c => c.Input).ToList();
        var done = await ReviseCoreAsync(
            plan.Document, plan.Scope, inputs, embedder, sink, null, "revision", AuditKind.Deleted, "no document uses it any more", source, ct)
            .ConfigureAwait(false);
        return done.Result;
    }

    /// <summary>
    /// Plan and apply in one go. The holder names this piece of work: pass the queue message id, so that if the worker
    /// dies and the message comes back, the retry gets its claims back. Without one a random holder is used.
    /// The source names where the document comes from (a connector, a site, a bucket). It is kept with the document and
    /// in the audit trail, and it is what an erase by source goes by.
    /// </summary>
    public async ValueTask<RevisionResult> ReviseAsync(
        DocumentId document,
        string? scope,
        IReadOnlyList<ChunkInput> chunks,
        IChunkEmbedder embedder,
        IVectorSink sink,
        string? holder = null,
        CancellationToken ct = default,
        string? source = null)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var done = await ReviseCoreAsync(
            document, scope ?? _options.DefaultScope, chunks, embedder, sink, holder, "revision", AuditKind.Deleted,
            "no document uses it any more", source, ct)
            .ConfigureAwait(false);
        return done.Result;
    }

    /// <summary>Deletes a document: the same as revising it to nothing.</summary>
    public ValueTask<RevisionResult> DeleteDocumentAsync(
        DocumentId document, string? scope, IChunkEmbedder embedder, IVectorSink sink, CancellationToken ct = default) =>
        ReviseAsync(document, scope, Array.Empty<ChunkInput>(), embedder, sink, null, ct);

    /// <summary>The audit trail is only written when there is an audit log. Always false for a tracker without one.</summary>
    public bool Audited => _audit is not null;

    public async ValueTask<VerifyReport> VerifyAsync(string scope, IVectorSink sink, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(sink);

        var refs = await _store.ListRefsAsync(scope, ct).ConfigureAwait(false);
        var known = refs.Select(r => r.Chunk.Value).ToHashSet(StringComparer.Ordinal);
        var inSink = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var id in sink.ListIdsAsync(scope, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            inSink.Add(id.Value);
        }

        var orphans = inSink.Where(i => !known.Contains(i)).OrderBy(i => i, StringComparer.Ordinal).Select(i => new ChunkId(i)).ToList();
        var missing = refs
            .Where(r => r.Count > 0 && !inSink.Contains(r.Chunk.Value))
            .Select(r => r.Chunk)
            .OrderBy(c => c.Value, StringComparer.Ordinal)
            .ToList();
        return new VerifyReport(orphans, missing);
    }

    /// <summary>
    /// Deletes the chunks that no document uses. A revision does this by itself after it switches, so call this on a timer
    /// or after a failed delete, to finish what a crashed worker left. Safe to run from many workers at once: a chunk is
    /// owned by one sweeper at a time.
    /// </summary>
    public async ValueTask<SweepResult> SweepAsync(string? scope, IVectorSink sink, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        scope ??= _options.DefaultScope;
        var holder = "sweep-" + Guid.NewGuid().ToString("N");
        var batch = _options.Revisions.SweepBatchSize;
        var retired = new List<ChunkId>();
        var rows = 0;
        var failed = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var now = _time.GetUtcNow();
            var pending = await _store.ListPendingAsync(scope, now, batch, ct).ConfigureAwait(false);
            if (pending.Count == 0)
            {
                break;
            }

            // Take every chunk first. Once a chunk is Retiring no revision can start using it.
            var taken = new List<RetireClaim>();
            foreach (var chunk in pending)
            {
                var claim = await _store.BeginRetireAsync(scope, chunk, holder, _options.Revisions.RetireLease, now, ct).ConfigureAwait(false);
                if (claim is not null)
                {
                    taken.Add(claim);
                }
            }

            if (taken.Count == 0)
            {
                break;
            }

            // The vectors go before the fingerprints. If this fails the chunks stay Retiring, their lease runs out and
            // a later sweep does it again, which is safe because deleting a vector twice is fine.
            try
            {
                await sink.DeleteAsync(scope, taken.Select(t => t.Chunk).ToList(), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                failed += taken.Count;
                _logger.LogWarning("Deleting {Count} vectors failed, the chunks stay marked and the next sweep tries again: {Error}",
                    taken.Count, ex.GetType().Name);
                break;
            }

            var progress = 0;
            foreach (var claim in taken)
            {
                try
                {
                    rows += await ForgetAsync(scope, claim, ct).ConfigureAwait(false);
                    if (await _store.FinishRetireAsync(scope, claim.Chunk, holder, ct).ConfigureAwait(false))
                    {
                        retired.Add(claim.Chunk);
                        progress++;
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
                {
                    failed++;
                    _logger.LogWarning("Could not forget chunk {Chunk}, the next sweep finishes it: {Error}",
                        Short(claim.Chunk.Value), ex.GetType().Name);
                }
            }

            if (progress == 0)
            {
                break;
            }
        }

        return new SweepResult(retired, rows, failed);
    }

    // Removes the fingerprints of a chunk and of the duplicates stored as aliases of it.
    private async ValueTask<int> ForgetAsync(string scope, RetireClaim claim, CancellationToken ct)
    {
        var language = LanguageTags.FromId(claim.Chunk);
        var effective = LanguageTags.ScopeFor(scope, language);
        var hashes = new List<ReadOnlyMemory<byte>>();
        foreach (var id in claim.Aliases.Prepend(claim.Chunk))
        {
            if (LanguageTags.TryParseId(id, out var hash, out _))
            {
                hashes.Add(hash);
            }
        }

        return await _fingerprints.ForgetAsync(effective, claim.Chunk, hashes, ct).ConfigureAwait(false);
    }

    private sealed record Done(RevisionResult Result, int FingerprintRows, bool SweepFailed = false);

    private async ValueTask<Done> ReviseCoreAsync(
        DocumentId document,
        string scope,
        IReadOnlyList<ChunkInput> inputs,
        IChunkEmbedder embedder,
        IVectorSink sink,
        string? holder,
        string actor,
        AuditKind deleteKind,
        string deleteReason,
        string? source,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(inputs);

        await DrainSafelyAsync(ct).ConfigureAwait(false);

        // A revision that names no source keeps the one the document has.
        if (string.IsNullOrEmpty(source))
        {
            source = (await _store.GetAsync(scope, document, ct).ConfigureAwait(false))?.Source;
        }

        // A scan that fails stops here: nothing has been stored, and the caller can try again.
        var identified = await IdentifyAsync(inputs, ct).ConfigureAwait(false);
        var done = await ReviseInnerAsync(
            document, scope, identified.Items, embedder, sink, holder, actor, deleteKind, deleteReason, source ?? string.Empty, ct)
            .ConfigureAwait(false);

        if (identified.Blocked.Count > 0)
        {
            var at = done.Result.Status == RevisionStatus.Incomplete ? done.Result.Revision + 1 : done.Result.Revision;
            var context = new AuditContext(scope, document, at, source ?? string.Empty, embedder.ModelId, actor, _time.GetUtcNow(), _options.OnDuplicate);
            var events = identified.Blocked
                .Select(b => AuditEvents.Make(
                    context,
                    new ChunkOutcome(b.Ordinal, new ChunkId(string.Empty), new ChunkId(string.Empty), null, ChunkStatus.Blocked, Findings: b.PiiFindings),
                    AuditKind.Blocked, "kept out by the PII policy", AuditStages.Pii))
                .ToList();
            await WriteEventsAsync(events, ct).ConfigureAwait(false);
        }

        var pending = _audit is null ? 0 : await _audit.PendingAsync(ct).ConfigureAwait(false);
        return done with { Result = done.Result with { PiiBlocked = identified.Blocked.Count, AuditPending = pending } };
    }

    private async ValueTask DrainSafelyAsync(CancellationToken ct)
    {
        if (_audit is null)
        {
            return;
        }

        try
        {
            await _audit.DrainAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _logger.LogWarning("Could not drain the audit outbox: {Error}", ex.GetType().Name);
        }
    }

    private async ValueTask<Done> ReviseInnerAsync(
        DocumentId document,
        string scope,
        List<Item> items,
        IChunkEmbedder embedder,
        IVectorSink sink,
        string? holder,
        string actor,
        AuditKind deleteKind,
        string deleteReason,
        string source,
        CancellationToken ct)
    {
        holder ??= "rev-" + Guid.NewGuid().ToString("N");
        var counting = new CountingEmbedder(embedder);
        var ingestor = new ChunkIngestor(_pipeline, counting, sink, _deadLetters, _logger, _time);
        var retries = _options.Revisions.MaxConflictRetries;
        var rows = 0;

        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var manifest = await _store.GetAsync(scope, document, ct).ConfigureAwait(false);
            var from = manifest?.Revision ?? 0;
            var old = manifest?.Entries ?? Array.Empty<ManifestEntry>();

            if (IsSame(old, items))
            {
                // a document that is already as asked: still finish any delete a crashed worker left behind
                var left = await SweepAsync(scope, sink, ct).ConfigureAwait(false);
                rows += left.FingerprintRowsRemoved;
                return new Done(new RevisionResult(0, items.Count, 0, 0, RevisionStatus.Unchanged, from), rows, left.Failed > 0);
            }

            var to = from + 1;
            var oldById = FirstById(old);
            var newIds = items.Select(i => i.Id.Value).ToHashSet(StringComparer.Ordinal);
            var kept = items.Where(i => oldById.ContainsKey(i.Id.Value)).ToList();
            var fresh = items.Where(i => !oldById.ContainsKey(i.Id.Value)).ToList();

            // When a changed chunk is embedded again, it must not be matched with the old text it replaces.
            IReadOnlySet<ChunkId>? exclude = null;
            if (_options.OnRevision == RevisionPolicy.ReEmbedChanged)
            {
                var keptTargets = kept.Select(k => oldById[k.Id.Value].Target.Value).ToHashSet(StringComparer.Ordinal);
                exclude = DroppedTargets(old, newIds)
                    .Where(t => !keptTargets.Contains(t))
                    .Select(t => new ChunkId(t))
                    .ToHashSet();
            }

            IngestResult? ingest = null;
            if (fresh.Count > 0)
            {
                ingest = await ingestor.IngestAsync(
                    new IngestRequest(scope, document, fresh.Select(f => f.Input).ToList(), holder, to, exclude, source), ct)
                    .ConfigureAwait(false);

                if (!ingest.IsComplete || ingest.Outcomes.Any(o => o.Status is ChunkStatus.InFlight or ChunkStatus.Failed))
                {
                    var inFlight = ingest.Outcomes.Count(o => o.Status == ChunkStatus.InFlight);
                    var failedCount = ingest.Outcomes.Count(o => o.Status == ChunkStatus.Failed);
                    _logger.LogWarning(
                        "Revision {Revision} of a document is not finished: {InFlight} chunks are with other workers, {Failed} failed",
                        to, inFlight, failedCount);
                    await WriteEventsAsync(
                        AuditEvents.FromIngest(Context(scope, document, to, source, embedder.ModelId, actor), ingest), ct).ConfigureAwait(false);
                    return new Done(new RevisionResult(
                        counting.Calls, kept.Count, 0, 0, RevisionStatus.Incomplete, from,
                        ingest.Outcomes.Count(o => o.Status == ChunkStatus.Stored), 0, inFlight, failedCount), rows);
                }
            }

            // build the new manifest in the order of the document
            var outcomeByIndex = new Dictionary<string, ChunkOutcome>(StringComparer.Ordinal);
            if (ingest is not null)
            {
                for (var i = 0; i < fresh.Count; i++)
                {
                    outcomeByIndex[Key(fresh[i].Input, i)] = ingest.Outcomes[i];
                }
            }

            var oldByOrdinal = old.GroupBy(e => e.Ordinal).ToDictionary(g => g.Key, g => g.First());
            var entries = new List<ManifestEntry>(items.Count);
            var freshIndex = 0;
            var moved = 0;
            foreach (var item in items)
            {
                if (oldById.TryGetValue(item.Id.Value, out var existing))
                {
                    if (existing.Ordinal != item.Input.Ordinal)
                    {
                        moved++;
                    }

                    entries.Add(existing with { Ordinal = item.Input.Ordinal, PreviousOrdinal = existing.Ordinal, Replaces = null });
                    continue;
                }

                var outcome = outcomeByIndex[Key(item.Input, freshIndex++)];
                ChunkId? replaces = oldByOrdinal.TryGetValue(item.Input.Ordinal, out var at) && !newIds.Contains(at.Id.Value)
                    ? at.Id
                    : null;
                entries.Add(new ManifestEntry(item.Input.Ordinal, item.Id, outcome.Target, item.Language, to, null, replaces));
            }

            // An erase forgets the text of the aliases first. If that fails, the document is untouched and the erase can run again.
            if (deleteKind == AuditKind.Erased)
            {
                rows += await ForgetDroppedAliasesAsync(scope, document, old, newIds, ct).ConfigureAwait(false);
            }

            var commit = await _store.CommitAsync(
                new CommitRevisionRequest(scope, document, from, entries, _time.GetUtcNow(), string.IsNullOrEmpty(source) ? null : source), ct).ConfigureAwait(false);

            if (commit.Status == CommitRevisionStatus.Committed)
            {
                var sweep = await SweepSafelyAsync(scope, sink, ct).ConfigureAwait(false);
                rows += sweep.FingerprintRowsRemoved;
                if (deleteKind != AuditKind.Erased)
                {
                    try
                    {
                        rows += await ForgetDroppedAliasesAsync(scope, document, old, newIds, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
                    {
                        _logger.LogWarning("Could not forget the text of dropped duplicates, it stays until the chunk goes: {Error}", ex.GetType().Name);
                    }
                }

                var deleted = sweep.Retired.Where(r => commit.Orphaned.Contains(r)).ToList();

                await WriteAppliedAuditAsync(
                    Context(scope, document, commit.Revision, source, embedder.ModelId, actor), entries, ingest, deleted, deleteKind, deleteReason, ct)
                    .ConfigureAwait(false);

                var skipped = ingest?.Outcomes.Count(o => o.Status == ChunkStatus.Duplicate) ?? 0;
                var embedded = ingest?.Outcomes.Count(o => o.Status == ChunkStatus.Stored) ?? 0;
                return new Done(new RevisionResult(
                    counting.Calls, kept.Count, skipped, deleted.Count, RevisionStatus.Applied, commit.Revision, embedded, moved), rows, sweep.Failed > 0);
            }

            if (commit.Status == CommitRevisionStatus.Blocked)
            {
                // a chunk we wanted is being deleted: help finish that, then plan again so it is stored from scratch
                var sweep = await SweepSafelyAsync(scope, sink, ct).ConfigureAwait(false);
                rows += sweep.FingerprintRowsRemoved;
            }

            if (attempt >= retries)
            {
                var status = commit.Status == CommitRevisionStatus.Blocked ? RevisionStatus.Blocked : RevisionStatus.Conflict;
                _logger.LogWarning("Revision {Revision} was not applied: {Status} after {Attempts} tries", to, status, attempt + 1);
                return new Done(new RevisionResult(counting.Calls, kept.Count, 0, 0, status, commit.Revision), rows);
            }

            if (_options.Revisions.ConflictDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.Revisions.ConflictDelay, _time, ct).ConfigureAwait(false);
            }
        }
    }

    // The sweep that follows a switch must not undo it: the document is on its new revision already, and a sweep that
    // fails just leaves chunks for the next one.
    private async ValueTask<SweepResult> SweepSafelyAsync(string scope, IVectorSink sink, CancellationToken ct)
    {
        try
        {
            return await SweepAsync(scope, sink, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _logger.LogWarning("The sweep after a revision failed, the next sweep will finish it: {Error}", ex.GetType().Name);
            return new SweepResult(Array.Empty<ChunkId>(), 0, 1);
        }
    }

    private AuditContext Context(string scope, DocumentId document, int revision, string source, string model, string actor) =>
        new(scope, document, revision, source, model, actor, _time.GetUtcNow(), _options.OnDuplicate);

    private async ValueTask WriteAppliedAuditAsync(
        AuditContext context,
        List<ManifestEntry> entries,
        IngestResult? ingest,
        List<ChunkId> deleted,
        AuditKind deleteKind,
        string deleteReason,
        CancellationToken ct)
    {
        if (_audit is null)
        {
            return;
        }

        var events = new List<AuditEvent>();
        if (ingest is not null)
        {
            var replacing = entries.Where(e => e.Replaces is not null).Select(e => e.Id).ToHashSet();
            events.AddRange(AuditEvents.FromIngest(context, ingest, replacing));
        }

        var stage = deleteKind == AuditKind.Erased ? AuditStages.Erasure : AuditStages.Revision;
        events.AddRange(deleted.Select(d => new AuditEvent(
            d, context.Document, context.Revision, context.Source, context.At, context.Model, deleteKind, deleteReason)
        {
            Scope = context.Scope,
            Language = LanguageTags.FromId(d),
            Stage = stage,
            Actor = context.Actor,
        }));

        await WriteEventsAsync(events, ct).ConfigureAwait(false);
    }

    // The writer retries and then parks what it can not write, and never throws after a commit.
    private async ValueTask WriteEventsAsync(List<AuditEvent> events, CancellationToken ct)
    {
        if (_audit is null || events.Count == 0)
        {
            return;
        }

        await _audit.WriteAsync(events, ct).ConfigureAwait(false);
    }

    private async ValueTask<int> RemoveDeadLettersAsync(string scope, DocumentId document, CancellationToken ct)
    {
        var removed = 0;
        while (true)
        {
            var batch = await _deadLetters.ListAsync(new DeadLetterQuery(scope, Document: document, Take: 100), ct).ConfigureAwait(false);
            var progress = 0;
            foreach (var item in batch)
            {
                if (await _deadLetters.RemoveAsync(item.Id, ct).ConfigureAwait(false))
                {
                    progress++;
                }
            }

            removed += progress;
            if (batch.Count < 100 || progress == 0)
            {
                return removed;
            }
        }
    }

    private sealed record Item(ChunkInput Input, ChunkId Id, string? Language);

    private sealed record Identified(List<Item> Items, List<ChunkInput> Blocked);

    // Scans the chunks (when that is on), then works out the id of each from the text that is left. A chunk with a Block
    // finding is not part of the document: it is returned in Blocked and never gets an id.
    private async ValueTask<Identified> IdentifyAsync(IReadOnlyList<ChunkInput> chunks, CancellationToken ct)
    {
        foreach (var chunk in chunks)
        {
            ArgumentNullException.ThrowIfNull(chunk);
        }

        var prepared = await _pipeline.ScanAsync(chunks, ct).ConfigureAwait(false);
        var list = new List<Item>(prepared.Count);
        var blocked = new List<ChunkInput>();
        foreach (var chunk in prepared)
        {
            if (chunk.Scan?.Blocked == true)
            {
                blocked.Add(chunk);
                continue;
            }

            var (id, language) = _pipeline.IdentifyPrepared(chunk);
            list.Add(new Item(chunk, id, language));
        }

        return new Identified(list, blocked);
    }

    private static Dictionary<string, ManifestEntry> FirstById(IReadOnlyList<ManifestEntry> entries)
    {
        var map = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            map.TryAdd(e.Id.Value, e);
        }

        return map;
    }

    // true when the document has exactly these chunks, in this order and at these positions
    private static bool IsSame(IReadOnlyList<ManifestEntry> old, List<Item> items)
    {
        if (old.Count != items.Count)
        {
            return false;
        }

        for (var i = 0; i < old.Count; i++)
        {
            if (old[i].Id != items[i].Id || old[i].Ordinal != items[i].Input.Ordinal)
            {
                return false;
            }
        }

        return true;
    }

    // targets of old entries that the new revision does not bring back by id, and no kept entry still uses
    private static List<string> DroppedTargets(IReadOnlyList<ManifestEntry> old, HashSet<string> newIds)
    {
        var keptTargets = old.Where(e => newIds.Contains(e.Id.Value)).Select(e => e.Target.Value).ToHashSet(StringComparer.Ordinal);
        return old.Where(e => !newIds.Contains(e.Id.Value))
            .Select(e => e.Target.Value)
            .Where(t => !keptTargets.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
    }

    private static string Key(ChunkInput input, int index) => index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Short(string value) => value.Length <= 8 ? value : value[..8];

    private sealed class CountingEmbedder(IChunkEmbedder inner) : IChunkEmbedder
    {
        public int Calls;

        public string ModelId => inner.ModelId;

        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return inner.EmbedAsync(texts, ct);
        }
    }

    private sealed class NoEmbedder : IChunkEmbedder
    {
        public static readonly NoEmbedder Instance = new();

        public string ModelId => "none";

        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            throw new InvalidOperationException("Nothing should be embedded when a document is erased.");
    }
}
