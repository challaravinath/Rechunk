using Microsoft.Extensions.Logging;

namespace Rechunk;

/// <summary>How a text is looked up when chunks are found by their content.</summary>
public enum TextMatch
{
    /// <summary>Only the chunk that has exactly this text (after masking and normalizing).</summary>
    Exact,

    /// <summary>Also the chunks that are near duplicates of it (SimHash or MinHash).</summary>
    Near,
}

public sealed partial class RevisionTracker
{
    private const int PageSize = 200;
    private const int ReasonLimit = 200;

    /// <summary>
    /// Right to erasure. Name a document, a source, or both. A document is erased with its vectors, fingerprints and dead
    /// letters, and keeps a manifest with no entries and no text. A source erases every document that was revised with that
    /// source. With both, the document has to belong to the source, or nothing is erased and this throws. A chunk that
    /// another document uses is not deleted, it stays as long as that document does.
    /// The erase writes a receipt to the audit log: counts and who asked, no content. The Reason is kept in it, so it must
    /// not contain personal data. Documents that were revised before sources were tracked have no source and are not found
    /// by a source, erase them by document.
    /// </summary>
    public async ValueTask<EraseResult> EraseAsync(EraseRequest request, IVectorSink sink, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);
        var source = string.IsNullOrWhiteSpace(request.Source) ? null : request.Source;
        if (request.Document is null && source is null)
        {
            throw new ArgumentException("An erase has to name a document or a source.", nameof(request));
        }

        await DrainSafelyAsync(ct).ConfigureAwait(false);
        var scope = string.IsNullOrWhiteSpace(request.Scope) ? _options.DefaultScope : request.Scope;
        var actor = string.IsNullOrWhiteSpace(request.RequestedBy) ? "erase" : request.RequestedBy;
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "erased on request" : request.Reason;

        var documents = new List<DocumentId>();
        if (request.Document is { } named)
        {
            if (source is not null)
            {
                var manifest = await _store.GetAsync(scope, named, ct).ConfigureAwait(false);
                if (manifest?.Source != source)
                {
                    throw new InvalidOperationException("The document does not belong to that source, so nothing was erased.");
                }
            }

            documents.Add(named);
        }
        else
        {
            string? after = null;
            while (true)
            {
                var page = await _store.ListDocumentsAsync(scope, source, after, PageSize, ct).ConfigureAwait(false);
                documents.AddRange(page.Select(d => d.Document));
                if (page.Count < PageSize)
                {
                    break;
                }

                after = page[^1].Document.Value;
            }
        }

        int docs = 0, chunks = 0, vectors = 0, rows = 0;
        Exception? failure = null;
        try
        {
            foreach (var document in documents)
            {
                ct.ThrowIfCancellationRequested();
                var before = await _store.GetAsync(scope, document, ct).ConfigureAwait(false);
                var done = await ReviseCoreAsync(
                    document, scope, Array.Empty<ChunkInput>(), NoEmbedder.Instance, sink, null, actor, AuditKind.Erased, reason, null, ct)
                    .ConfigureAwait(false);

                if (done.Result.Status is RevisionStatus.Conflict or RevisionStatus.Blocked)
                {
                    throw new InvalidOperationException(
                        $"The document could not be erased because it kept changing or a chunk was busy ({done.Result.Status}). Try again.");
                }

                rows += done.FingerprintRows + await RemoveDeadLettersAsync(scope, document, ct).ConfigureAwait(false);
                vectors += done.Result.Deleted;
                chunks += done.Result.Deleted;
                if (before is { IsDeleted: false })
                {
                    docs++;
                }

                if (done.SweepFailed)
                {
                    throw new InvalidOperationException(
                        "Some vectors could not be deleted yet. The document is erased, run the erase again to finish deleting them.");
                }
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            failure = ex;
        }

        var receipt = await WriteReceiptAsync(
            scope, request.Document, source, actor, reason, docs, chunks, vectors, rows, failure is not null, ct).ConfigureAwait(false);
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return new EraseResult(vectors, rows, docs, chunks, receipt);
    }

    /// <summary>
    /// Erases chunks by id wherever they are used. Every document that uses one of them is revised without it (the entries
    /// that are the chunk or point at it are dropped, the rest of the document stays), then the vectors and fingerprints go.
    /// A chunk that no revision knows (stored with the plain ingestor) is deleted directly. The text comes back if the
    /// source document is revised again with it, so fix the source too.
    /// </summary>
    public async ValueTask<EraseResult> EraseChunksAsync(
        string? scope, IReadOnlyCollection<ChunkId> chunks, IVectorSink sink, string reason, string requestedBy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(sink);
        scope ??= _options.DefaultScope;
        var actor = string.IsNullOrWhiteSpace(requestedBy) ? "erase" : requestedBy;
        reason = string.IsNullOrWhiteSpace(reason) ? "erased on request" : reason;

        await DrainSafelyAsync(ct).ConfigureAwait(false);
        var ids = chunks.Where(c => !string.IsNullOrEmpty(c.Value)).Distinct().ToList();
        var result = await EraseIdsWithReceiptOnFailureAsync(scope, ids, sink, actor, reason, ct).ConfigureAwait(false);
        var receipt = await WriteReceiptAsync(
            scope, null, null, actor, reason, result.Documents, result.Chunks, result.VectorsDeleted, result.StateRowsDeleted, false, ct)
            .ConfigureAwait(false);
        return result with { ReceiptId = receipt };
    }

    /// <summary>
    /// Finds the stored chunks that match a text, as the pipeline would match it (masked first, so a text with a secret in
    /// it finds the chunk that was stored without it). Changes nothing.
    /// </summary>
    public async ValueTask<IReadOnlyList<ChunkId>> FindByTextAsync(
        string? scope, string text, string? language = null, TextMatch mode = TextMatch.Exact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var matches = await _pipeline.FindStoredAsync(scope, new ChunkInput(0, text, language), mode == TextMatch.Near, ct).ConfigureAwait(false);
        return matches.Select(m => m.Existing).Distinct().ToList();
    }

    /// <summary>
    /// Erases every chunk that matches a text, and the fingerprint rows of the text itself (also when it was stored as an
    /// alias of another chunk, the case where an exact row can outlive a delete). Returns what was removed, with a receipt.
    /// </summary>
    public async ValueTask<EraseResult> EraseTextAsync(
        string? scope, string text, IVectorSink sink, string reason, string requestedBy,
        string? language = null, TextMatch mode = TextMatch.Exact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sink);
        scope ??= _options.DefaultScope;
        var actor = string.IsNullOrWhiteSpace(requestedBy) ? "erase" : requestedBy;
        reason = string.IsNullOrWhiteSpace(reason) ? "erased on request" : reason;
        await DrainSafelyAsync(ct).ConfigureAwait(false);

        var input = new ChunkInput(0, text, language);
        var matches = await _pipeline.FindStoredAsync(scope, input, mode == TextMatch.Near, ct).ConfigureAwait(false);
        var ids = matches.Select(m => m.Existing).Distinct().ToList();
        var own = await _pipeline.IdForAsync(input, ct).ConfigureAwait(false);
        var ownLanguage = LanguageTags.FromId(own);
        var rows = 0;

        var result = ids.Count == 0
            ? new EraseResult(0, 0)
            : await EraseIdsWithReceiptOnFailureAsync(scope, ids, sink, actor, reason, ct).ConfigureAwait(false);

        // the row of the text itself: it points at the owner, which is the chunk when the text was stored as it is
        if (LanguageTags.TryParseId(own, out var hash, out _))
        {
            var effective = LanguageTags.ScopeFor(scope, ownLanguage);
            foreach (var owner in ids.Prepend(own).Distinct())
            {
                rows += await _fingerprints.ForgetExactAsync(effective, owner, new ReadOnlyMemory<byte>[] { hash }, ct).ConfigureAwait(false);
            }
        }

        result = result with { StateRowsDeleted = result.StateRowsDeleted + rows };
        var receipt = await WriteReceiptAsync(
            scope, null, null, actor, reason, result.Documents, result.Chunks, result.VectorsDeleted, result.StateRowsDeleted, false, ct)
            .ConfigureAwait(false);
        return result with { ReceiptId = receipt };
    }

    /// <summary>
    /// Finds the chunks that had a PII finding of this type when they were ingested, from the audit trail. The log holds
    /// the type and span of findings, never the value, so this answers "everything that had a card number", not "everything
    /// with this card number". For one known value use FindByTextAsync. Only as complete as the audit log: an in memory log
    /// with a limit, or events that are still in the outbox, are not seen.
    /// </summary>
    public async ValueTask<IReadOnlyList<ChunkId>> FindByFindingAsync(
        string? scope, PiiType type, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken ct = default)
    {
        if (_audit is null)
        {
            throw new InvalidOperationException("Finding chunks by a PII finding needs an audit log.");
        }

        scope ??= _options.DefaultScope;
        await DrainSafelyAsync(ct).ConfigureAwait(false);
        var found = new HashSet<ChunkId>();
        var skip = 0;
        while (true)
        {
            var count = 0;
            var query = new AuditQuery(From: from, To: to) { Scope = scope, FindingType = type, Skip = skip, Take = 500 };
            await foreach (var e in _audit.Log.QueryAsync(query, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                count++;
                if (e.Kind is AuditKind.New or AuditKind.Revised or AuditKind.Flagged or AuditKind.Aliased or AuditKind.Skipped
                    && !string.IsNullOrEmpty(e.Chunk.Value))
                {
                    found.Add(e.Chunk);
                }
            }

            if (count < 500)
            {
                break;
            }

            skip += count;
        }

        return found.OrderBy(c => c.Value, StringComparer.Ordinal).ToList();
    }

    /// <summary>Finds the chunks that had a finding of this type (see FindByFindingAsync) and erases them.</summary>
    public async ValueTask<EraseResult> EraseFindingsAsync(
        string? scope, PiiType type, IVectorSink sink, string reason, string requestedBy,
        DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken ct = default)
    {
        var ids = await FindByFindingAsync(scope, type, from, to, ct).ConfigureAwait(false);
        return await EraseChunksAsync(scope, ids, sink, reason, requestedBy, ct).ConfigureAwait(false);
    }

    // A failed erase still leaves a receipt, marked incomplete, and then fails the same way.
    private async ValueTask<EraseResult> EraseIdsWithReceiptOnFailureAsync(
        string scope, List<ChunkId> ids, IVectorSink sink, string actor, string reason, CancellationToken ct)
    {
        try
        {
            return await EraseIdsAsync(scope, ids, sink, actor, reason, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            await WriteReceiptAsync(scope, null, null, actor, reason, 0, 0, 0, 0, true, ct).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<EraseResult> EraseIdsAsync(
        string scope, List<ChunkId> ids, IVectorSink sink, string actor, string reason, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return new EraseResult(0, 0);
        }

        var wanted = ids.Select(i => i.Value).ToHashSet(StringComparer.Ordinal);
        var documents = new List<DocumentRef>();
        string? after = null;
        while (true)
        {
            var page = await _store.ListDocumentsUsingAsync(scope, ids, after, PageSize, ct).ConfigureAwait(false);
            documents.AddRange(page);
            if (page.Count < PageSize)
            {
                break;
            }

            after = page[^1].Document.Value;
        }

        // chunks that no revision ever knew are deleted directly, so they have to be told apart before the documents change
        var unknown = new List<ChunkId>();
        foreach (var id in ids)
        {
            if (await _store.GetRefAsync(scope, id, ct).ConfigureAwait(false) is null)
            {
                unknown.Add(id);
            }
        }

        var rows = 0;
        var touched = 0;
        var events = new List<AuditEvent>();
        foreach (var doc in documents)
        {
            var (changed, removedRows, dropped, revision, source) = await DropFromDocumentAsync(scope, doc.Document, wanted, sink, ct).ConfigureAwait(false);
            rows += removedRows;
            if (changed)
            {
                touched++;
                events.AddRange(dropped.Select(d => new AuditEvent(
                    d, doc.Document, revision, source ?? string.Empty, _time.GetUtcNow(), string.Empty, AuditKind.Erased, reason)
                {
                    Scope = scope,
                    Language = LanguageTags.FromId(d),
                    Stage = AuditStages.Erasure,
                    Actor = actor,
                }));
            }
        }

        var sweep = await SweepAsync(scope, sink, ct).ConfigureAwait(false);
        rows += sweep.FingerprintRowsRemoved;
        var vectors = sweep.Retired.Count(r => wanted.Contains(r.Value));

        if (unknown.Count > 0)
        {
            await sink.DeleteAsync(scope, unknown, ct).ConfigureAwait(false);
            foreach (var id in unknown)
            {
                var language = LanguageTags.FromId(id);
                if (LanguageTags.TryParseId(id, out var hash, out _))
                {
                    rows += await _fingerprints.ForgetAsync(LanguageTags.ScopeFor(scope, language), id, new ReadOnlyMemory<byte>[] { hash }, ct).ConfigureAwait(false);
                }
            }

            vectors += unknown.Count;
        }

        var left = 0;
        foreach (var id in ids)
        {
            if (await _store.GetRefAsync(scope, id, ct).ConfigureAwait(false) is not null)
            {
                left++;
            }
        }

        await WriteEventsAsync(events, ct).ConfigureAwait(false);
        if (sweep.Failed > 0)
        {
            throw new InvalidOperationException(
                "Some vectors could not be deleted yet. The documents are changed, run the erase again to finish deleting them.");
        }

        return new EraseResult(vectors, rows, touched, ids.Count - left, null);
    }

    // Commits the document again without the entries that are, or point at, one of the chunks.
    private async ValueTask<(bool Changed, int Rows, List<ChunkId> Dropped, int Revision, string? Source)> DropFromDocumentAsync(
        string scope, DocumentId document, HashSet<string> wanted, IVectorSink sink, CancellationToken ct)
    {
        var retries = _options.Revisions.MaxConflictRetries;
        var rows = 0;
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var manifest = await _store.GetAsync(scope, document, ct).ConfigureAwait(false);
            if (manifest is null)
            {
                return (false, rows, new List<ChunkId>(), 0, null);
            }

            var hit = manifest.Entries.Where(e => wanted.Contains(e.Id.Value) || wanted.Contains(e.Target.Value)).ToList();
            if (hit.Count == 0)
            {
                return (false, rows, new List<ChunkId>(), manifest.Revision, manifest.Source);
            }

            var keep = manifest.Entries.Except(hit).ToList();
            var keepIds = keep.Select(e => e.Id.Value).ToHashSet(StringComparer.Ordinal);
            rows += await ForgetDroppedAliasesAsync(scope, document, manifest.Entries, keepIds, ct).ConfigureAwait(false);

            var commit = await _store.CommitAsync(
                new CommitRevisionRequest(scope, document, manifest.Revision, keep, _time.GetUtcNow()), ct).ConfigureAwait(false);
            if (commit.Status == CommitRevisionStatus.Committed)
            {
                var dropped = hit.Select(e => e.Id).Concat(hit.Select(e => e.Target)).Where(i => wanted.Contains(i.Value)).Distinct().ToList();
                return (true, rows, dropped, commit.Revision, manifest.Source);
            }

            if (commit.Status == CommitRevisionStatus.Blocked)
            {
                var sweep = await SweepSafelyAsync(scope, sink, ct).ConfigureAwait(false);
                rows += sweep.FingerprintRowsRemoved;
            }

            if (attempt >= retries)
            {
                throw new InvalidOperationException(
                    $"A document could not be changed because it kept changing or a chunk was busy ({commit.Status}). Try again.");
            }

            if (_options.Revisions.ConflictDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.Revisions.ConflictDelay, _time, ct).ConfigureAwait(false);
            }
        }
    }

    // A duplicate that was stored as an alias has an exact row of its own, pointing at the chunk it duplicates. When the
    // document that had it lets go, the row goes too, unless another document still has the same text. When the chunk it
    // points at is deleted this happens anyway; this is for the chunk that stays because someone else uses it.
    private async ValueTask<int> ForgetDroppedAliasesAsync(
        string scope, DocumentId document, IReadOnlyList<ManifestEntry> old, HashSet<string> newIds, CancellationToken ct)
    {
        var rows = 0;
        foreach (var entry in old.Where(e => e.Id != e.Target && !newIds.Contains(e.Id.Value)))
        {
            if (!LanguageTags.TryParseId(entry.Id, out var hash, out var language))
            {
                continue;
            }

            IReadOnlyList<DocumentRef> others;
            try
            {
                others = await _store.ListDocumentsUsingAsync(scope, new[] { entry.Id }, null, 2, ct).ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
                _logger.LogWarning("The revision store can not tell which documents use a chunk, so duplicate rows are left to the sweep.");
                return rows;
            }

            if (others.Any(d => d.Document != document))
            {
                continue;
            }

            rows += await _fingerprints.ForgetExactAsync(LanguageTags.ScopeFor(scope, language), entry.Target, new ReadOnlyMemory<byte>[] { hash }, ct)
                .ConfigureAwait(false);
        }

        return rows;
    }

    private async ValueTask<string?> WriteReceiptAsync(
        string scope, DocumentId? document, string? source, string actor, string reason,
        int documents, int chunks, int vectors, int rows, bool incomplete, CancellationToken ct)
    {
        if (_audit is null)
        {
            return null;
        }

        var text = reason.ReplaceLineEndings(" ");
        if (text.Length > ReasonLimit)
        {
            text = text[..ReasonLimit];
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["documents"] = documents,
            ["chunks"] = chunks,
            ["vectors"] = vectors,
            ["state_rows"] = rows,
        };
        if (incomplete)
        {
            counts["incomplete"] = 1;
        }

        var receipt = new AuditEvent(
            new ChunkId(string.Empty), document ?? new DocumentId(string.Empty), 0, source ?? string.Empty,
            _time.GetUtcNow(), string.Empty, AuditKind.ErasureReceipt, text)
        {
            Scope = scope,
            Stage = AuditStages.Erasure,
            Actor = actor,
            Counts = counts,
        };
        await WriteEventsAsync(new List<AuditEvent> { receipt }, ct).ConfigureAwait(false);
        return receipt.Id;
    }
}
