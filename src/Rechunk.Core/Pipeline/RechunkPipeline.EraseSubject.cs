namespace Rechunk;

public sealed partial class RechunkPipeline
{
    /// <summary>
    /// Erases what this scope holds about one person, including near copies that point at those chunks.
    /// The value is normalized and keyed the same way the subject index keys it. The receipt is signed under the scope key.
    /// It names the keyed subject id, the keyed chunk ids, and the counts. It does not contain the value, the chunk text, or a fingerprint.
    /// A scope with no key provider has no subject index and no key to sign with: the result is not available, and dedup is unchanged.
    /// A subject with no links is not an error. The receipt is signed and the counts are zero.
    /// Calling it again for the same subject returns another signed receipt with zero counts.
    /// Vectors live in your store. Delete the chunk ids from the receipt there yourself.
    /// </summary>
    public async ValueTask<ErasureReceipt> EraseSubjectAsync(string scope, SubjectRef subject, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        if (_keys is null || _subjects is null)
        {
            return ErasureReceipt.NotAvailable(scope);
        }

        var key = await _keys.GetRequiredAsync(scope, ct).ConfigureAwait(false);
        var keyed = SubjectIds.TryKey(subject.Kind, subject.Value, key);
        var chunks = keyed is null
            ? Array.Empty<ChunkId>()
            : await _subjects.ListChunksAsync(scope, keyed.Value, ct).ConfigureAwait(false);

        var erased = chunks.Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var references = 0;
        var revisions = 0;
        if (erased.Count > 0 && _revisions is not null)
        {
            (references, revisions) = await DropManifestEntriesAsync(scope, erased, aliases, ct).ConfigureAwait(false);
        }

        if (_store is not null)
        {
            foreach (var id in chunks)
            {
                await ForgetChunkAsync(scope, id, ct).ConfigureAwait(false);
            }
        }

        var links = 0;
        foreach (var id in chunks)
        {
            links += await _subjects.CountLinksAsync(scope, id, ct).ConfigureAwait(false);
            await _subjects.RemoveAsync(scope, id, ct).ConfigureAwait(false);
        }

        var chunkIds = erased.Concat(aliases).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var receipt = new ErasureReceipt
        {
            Available = true,
            Id = Guid.NewGuid().ToString("N"),
            Scope = scope,
            SubjectId = keyed?.Id,
            Timestamp = DateTimeOffset.UtcNow,
            ChunksRemoved = chunks.Count,
            ReferencesRemoved = references,
            RevisionsAffected = revisions,
            SubjectLinksRemoved = links,
            ChunkIds = chunkIds,
            KeyVersion = key.Version,
        };
        receipt = receipt with { Signature = ErasureReceipts.Sign(key, receipt) };
        await AuditErasureAsync(scope, receipt, ct).ConfigureAwait(false);
        return receipt;
    }

    /// <summary>
    /// True when the signature matches the receipt fields under this scope's key.
    /// False when the scope has no key, the receipt was not signed, or any signed field was changed.
    /// </summary>
    public async ValueTask<bool> VerifyErasureReceiptAsync(string scope, ErasureReceipt receipt, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(receipt);
        if (!receipt.Available || receipt.Signature is null || !string.Equals(receipt.Scope, scope, StringComparison.Ordinal))
        {
            return false;
        }

        if (_keys is null)
        {
            return false;
        }

        ScopeKey key;
        try
        {
            key = await _keys.GetRequiredAsync(scope, ct).ConfigureAwait(false);
        }
        catch (KeyUnavailableException)
        {
            return false;
        }

        if (receipt.KeyVersion is { } version && version != key.Version)
        {
            return false;
        }

        return ErasureReceipts.Matches(key, receipt);
    }

    private async ValueTask<(int References, int Revisions)> DropManifestEntriesAsync(
        string scope, HashSet<string> erased, HashSet<string> aliases, CancellationToken ct)
    {
        var documents = new List<DocumentRef>();
        string? after = null;
        var ids = erased.Select(id => new ChunkId(id)).ToList();
        while (true)
        {
            var page = await _revisions!.ListDocumentsUsingAsync(scope, ids, after, 100, ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            documents.AddRange(page);
            if (page.Count < 100)
            {
                break;
            }

            after = page[^1].Document.Value;
        }

        var references = 0;
        var revisions = 0;
        foreach (var doc in documents)
        {
            var removed = await DropFromDocumentAsync(scope, doc.Document, erased, aliases, ct).ConfigureAwait(false);
            if (removed > 0)
            {
                references += removed;
                revisions++;
            }
        }

        return (references, revisions);
    }

    private async ValueTask<int> DropFromDocumentAsync(
        string scope, DocumentId document, HashSet<string> erased, HashSet<string> aliases, CancellationToken ct)
    {
        var retries = _options.Revisions.MaxConflictRetries;
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var manifest = await _revisions!.GetAsync(scope, document, ct).ConfigureAwait(false);
            if (manifest is null)
            {
                return 0;
            }

            foreach (var entry in manifest.Entries)
            {
                if (entry.Id != entry.Target && (erased.Contains(entry.Id.Value) || erased.Contains(entry.Target.Value)))
                {
                    aliases.Add(entry.Id.Value);
                }
            }

            var keep = manifest.Entries
                .Where(e => !erased.Contains(e.Id.Value) && !erased.Contains(e.Target.Value))
                .ToList();
            var removed = manifest.Entries.Count - keep.Count;
            if (removed == 0)
            {
                return 0;
            }

            var commit = await _revisions.CommitAsync(
                new CommitRevisionRequest(scope, document, manifest.Revision, keep, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
            if (commit.Status == CommitRevisionStatus.Committed)
            {
                return removed;
            }

            if (attempt >= retries)
            {
                throw new InvalidOperationException(
                    "A document could not be changed because it kept changing or a chunk was busy (" + commit.Status + "). Try again.");
            }

            if (_options.Revisions.ConflictDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.Revisions.ConflictDelay, ct).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask ForgetChunkAsync(string scope, ChunkId id, CancellationToken ct)
    {
        var effective = LanguageTags.ScopeFor(scope, LanguageTags.FromId(id));
        var hashes = await _store!.ListOwnedHashesAsync(effective, id, ct).ConfigureAwait(false);
        if (hashes.Count == 0 && !string.Equals(effective, scope, StringComparison.Ordinal))
        {
            hashes = await _store.ListOwnedHashesAsync(scope, id, ct).ConfigureAwait(false);
            effective = scope;
        }

        if (hashes.Count == 0)
        {
            await _store.SimHash.RemoveAsync(id, effective, ct).ConfigureAwait(false);
            await _store.MinHash.RemoveAsync(id, effective, ct).ConfigureAwait(false);
            return;
        }

        await _store.ForgetAsync(effective, id, hashes.Select(h => (ReadOnlyMemory<byte>)h).ToList(), ct).ConfigureAwait(false);
    }

    private async ValueTask AuditErasureAsync(string scope, ErasureReceipt receipt, CancellationToken ct)
    {
        if (_audit is null || receipt.Id is null)
        {
            return;
        }

        var auditEvent = new AuditEvent(
            new ChunkId("erasure"),
            new DocumentId(string.Empty),
            0,
            string.Empty,
            receipt.Timestamp,
            string.Empty,
            AuditKind.SubjectErased,
            "subject erased")
        {
            Id = receipt.Id,
            Scope = scope,
            Stage = AuditStages.Erasure,
            Actor = "erase",
            Counts = new Dictionary<string, int>
            {
                ["chunks"] = receipt.ChunksRemoved,
                ["references"] = receipt.ReferencesRemoved,
                ["revisions"] = receipt.RevisionsAffected,
                ["subject_links"] = receipt.SubjectLinksRemoved,
            },
        };
        await _audit.WriteAsync(new[] { auditEvent }, ct).ConfigureAwait(false);
    }
}
