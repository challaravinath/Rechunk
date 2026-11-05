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
public sealed class RevisionTracker : IRevisionTracker
{
    private readonly RechunkPipeline _pipeline;
    private readonly IFingerprintStore _fingerprints;
    private readonly IRevisionStore _store;
    private readonly IDeadLetterStore _deadLetters;
    private readonly IAuditLog? _audit;
    private readonly RechunkOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public RevisionTracker(
        RechunkPipeline pipeline,
        IRevisionStore store,
        IDeadLetterStore deadLetters,
        IAuditLog? audit = null,
        ILogger? logger = null,
        TimeProvider? time = null)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _deadLetters = deadLetters ?? throw new ArgumentNullException(nameof(deadLetters));
        _fingerprints = pipeline.Store
            ?? throw new InvalidOperationException(
                "Revision tracking needs a fingerprint store on the pipeline, so deleted chunks can be forgotten. "
                + "Add one with UseInMemoryFingerprintStore() or one of the storage packages.");
        _audit = audit;
        _options = pipeline.Options;
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
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
        var items = Identify(newChunks);
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

    public async ValueTask<RevisionResult> ApplyAsync(
        RevisionPlan plan, IChunkEmbedder embedder, IVectorSink sink, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var inputs = plan.Chunks.Select(c => c.Input).ToList();
        var done = await ReviseCoreAsync(plan.Document, plan.Scope, inputs, embedder, sink, null, "revision", "no document uses it any more", ct)
            .ConfigureAwait(false);
        return done.Result;
    }

    /// <summary>
    /// Plan and apply in one go. The holder names this piece of work: pass the queue message id, so that if the worker
    /// dies and the message comes back, the retry gets its claims back. Without one a random holder is used.
    /// </summary>
    public async ValueTask<RevisionResult> ReviseAsync(
        DocumentId document,
        string? scope,
        IReadOnlyList<ChunkInput> chunks,
        IChunkEmbedder embedder,
        IVectorSink sink,
        string? holder = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var done = await ReviseCoreAsync(
            document, scope ?? _options.DefaultScope, chunks, embedder, sink, holder, "revision", "no document uses it any more", ct)
            .ConfigureAwait(false);
        return done.Result;
    }

    /// <summary>Deletes a document: the same as revising it to nothing.</summary>
    public ValueTask<RevisionResult> DeleteDocumentAsync(
        DocumentId document, string? scope, IChunkEmbedder embedder, IVectorSink sink, CancellationToken ct = default) =>
        ReviseAsync(document, scope, Array.Empty<ChunkInput>(), embedder, sink, null, ct);

    /// <summary>
    /// Right to erasure for one document. Removes its vectors, fingerprints and dead letters, and leaves a manifest with no
    /// entries (no text). A chunk that another document uses is not deleted, it stays as long as that document does.
    /// Erasing by source is not supported, name the document.
    /// </summary>
    public async ValueTask<EraseResult> EraseAsync(EraseRequest request, IVectorSink sink, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);
        if (request.Document is not { } document)
        {
            throw new ArgumentException(
                "An erase has to name a document. Erasing everything from a source is not supported, list the documents instead.",
                nameof(request));
        }

        var scope = request.Scope ?? _options.DefaultScope;
        var done = await ReviseCoreAsync(
            document, scope, Array.Empty<ChunkInput>(), NoEmbedder.Instance, sink, null,
            string.IsNullOrWhiteSpace(request.RequestedBy) ? "erase" : request.RequestedBy,
            string.IsNullOrWhiteSpace(request.Reason) ? "erased on request" : request.Reason, ct).ConfigureAwait(false);

        if (done.Result.Status is RevisionStatus.Conflict or RevisionStatus.Blocked)
        {
            throw new InvalidOperationException(
                $"The document could not be erased because it kept changing or a chunk was busy ({done.Result.Status}). Try again.");
        }

        var rows = done.FingerprintRows + await RemoveDeadLettersAsync(scope, document, ct).ConfigureAwait(false);
        return new EraseResult(done.Result.Deleted, rows);
    }

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

    private sealed record Done(RevisionResult Result, int FingerprintRows);

    private async ValueTask<Done> ReviseCoreAsync(
        DocumentId document,
        string scope,
        IReadOnlyList<ChunkInput> inputs,
        IChunkEmbedder embedder,
        IVectorSink sink,
        string? holder,
        string auditSource,
        string deleteReason,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(scope);

        holder ??= "rev-" + Guid.NewGuid().ToString("N");
        var counting = new CountingEmbedder(embedder);
        var ingestor = new ChunkIngestor(_pipeline, counting, sink, _deadLetters, _logger, _time);
        var items = Identify(inputs);
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
                return new Done(new RevisionResult(0, items.Count, 0, 0, RevisionStatus.Unchanged, from), rows);
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
                    new IngestRequest(scope, document, fresh.Select(f => f.Input).ToList(), holder, to, exclude), ct)
                    .ConfigureAwait(false);

                if (!ingest.IsComplete || ingest.Outcomes.Any(o => o.Status is ChunkStatus.InFlight or ChunkStatus.Failed))
                {
                    var inFlight = ingest.Outcomes.Count(o => o.Status == ChunkStatus.InFlight);
                    var failedCount = ingest.Outcomes.Count(o => o.Status == ChunkStatus.Failed);
                    _logger.LogWarning(
                        "Revision {Revision} of a document is not finished: {InFlight} chunks are with other workers, {Failed} failed",
                        to, inFlight, failedCount);
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

            var commit = await _store.CommitAsync(
                new CommitRevisionRequest(scope, document, from, entries, _time.GetUtcNow()), ct).ConfigureAwait(false);

            if (commit.Status == CommitRevisionStatus.Committed)
            {
                var sweep = await SweepSafelyAsync(scope, sink, ct).ConfigureAwait(false);
                rows += sweep.FingerprintRowsRemoved;
                var deleted = sweep.Retired.Where(r => commit.Orphaned.Contains(r)).ToList();

                await WriteAuditAsync(document, commit.Revision, embedder.ModelId, auditSource, deleteReason, entries, ingest, deleted, ct)
                    .ConfigureAwait(false);

                var skipped = ingest?.Outcomes.Count(o => o.Status == ChunkStatus.Duplicate) ?? 0;
                var embedded = ingest?.Outcomes.Count(o => o.Status == ChunkStatus.Stored) ?? 0;
                return new Done(new RevisionResult(
                    counting.Calls, kept.Count, skipped, deleted.Count, RevisionStatus.Applied, commit.Revision, embedded, moved), rows);
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

    private async ValueTask WriteAuditAsync(
        DocumentId document,
        int revision,
        string model,
        string source,
        string deleteReason,
        List<ManifestEntry> entries,
        IngestResult? ingest,
        List<ChunkId> deleted,
        CancellationToken ct)
    {
        if (_audit is null)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var events = new List<AuditEvent>();
        foreach (var entry in entries.Where(e => e.FirstRevision == revision))
        {
            var outcome = ingest?.Outcomes.FirstOrDefault(o => o.Id == entry.Id);
            var (action, reason) = outcome?.Status switch
            {
                ChunkStatus.Stored when entry.Replaces is not null => (ChunkAction.Replace, "text changed at this position"),
                ChunkStatus.Stored => (ChunkAction.Embed, "new text"),
                ChunkStatus.Duplicate when entry.Id != entry.Target => (ChunkAction.Alias, "close to a chunk that is stored"),
                _ => (ChunkAction.Keep, "text was stored already"),
            };
            events.Add(new AuditEvent(entry.Id, document, revision, source, now, model, action, reason));
        }

        events.AddRange(deleted.Select(d => new AuditEvent(d, document, revision, source, now, model, ChunkAction.Delete, deleteReason)));

        try
        {
            foreach (var e in events)
            {
                await _audit.AppendAsync(e, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // the revision is applied, so a failing audit log is reported and not allowed to undo it
            _logger.LogError("The audit log did not take {Count} events for revision {Revision}: {Error}",
                events.Count, revision, ex.GetType().Name);
        }
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

    private List<Item> Identify(IReadOnlyList<ChunkInput> chunks)
    {
        var list = new List<Item>(chunks.Count);
        foreach (var chunk in chunks)
        {
            ArgumentNullException.ThrowIfNull(chunk);
            var (id, language) = _pipeline.Identify(chunk);
            list.Add(new Item(chunk, id, language));
        }

        return list;
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
