namespace Rechunk;

public sealed partial class RechunkPipeline
{
    /// <summary>
    /// What this scope holds about one person. The value is normalized and keyed the same way the subject index keys it.
    /// The report has counts, sources, document ids, revision numbers, dates and keyed chunk ids. It does not have the
    /// value, the chunk text, or a fingerprint.
    /// A scope with no key provider has no subject index: the report is not available, and nothing else about dedup changes.
    /// A subject with no links returns an empty report, not an error. Dates come from the document revision time. A linked
    /// chunk that is not in a document revision has no date, so the earliest and latest stay null when every chunk is like that.
    /// </summary>
    public async ValueTask<AccessReport> AccessReportAsync(string scope, SubjectRef subject, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var report = await BuildReportAsync(scope, subject, ct).ConfigureAwait(false);
        await AuditReportAsync(scope, report, ct).ConfigureAwait(false);
        return report;
    }

    private async ValueTask<AccessReport> BuildReportAsync(string scope, SubjectRef subject, CancellationToken ct)
    {
        if (_keys is null || _subjects is null)
        {
            return AccessReport.NotAvailable(scope);
        }

        var key = await _keys.GetRequiredAsync(scope, ct).ConfigureAwait(false);
        var keyed = SubjectIds.TryKey(subject.Kind, subject.Value, key);
        if (keyed is null)
        {
            return AccessReport.Empty(scope, null);
        }

        var chunks = await _subjects.ListChunksAsync(scope, keyed.Value, ct).ConfigureAwait(false);
        if (chunks.Count == 0 || _revisions is null)
        {
            return new AccessReport
            {
                Available = true,
                Scope = scope,
                SubjectId = keyed.Value.Id,
                ChunkCount = chunks.Count,
                Chunks = chunks,
                Kinds = chunks.Count == 0 ? Array.Empty<string>() : new[] { keyed.Value.Kind },
            };
        }

        var linked = chunks.Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        var documents = await DocumentsUsingAsync(scope, chunks, ct).ConfigureAwait(false);
        var bySource = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var byDocument = new List<SubjectDocumentCount>();
        var near = 0;
        DateTimeOffset? earliest = null;
        DateTimeOffset? latest = null;

        foreach (var manifest in documents)
        {
            var held = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in manifest.Entries)
            {
                if (linked.Contains(entry.Id.Value))
                {
                    held.Add(entry.Id.Value);
                }

                if (linked.Contains(entry.Target.Value))
                {
                    held.Add(entry.Target.Value);
                }

                if (entry.Id != entry.Target && (linked.Contains(entry.Id.Value) || linked.Contains(entry.Target.Value)))
                {
                    near++;
                }
            }

            if (held.Count == 0)
            {
                continue;
            }

            var source = manifest.Source ?? string.Empty;
            if (!bySource.TryGetValue(source, out var inSource))
            {
                inSource = new HashSet<string>(StringComparer.Ordinal);
                bySource[source] = inSource;
            }

            foreach (var id in held)
            {
                inSource.Add(id);
            }

            byDocument.Add(new SubjectDocumentCount(manifest.Document.Value, manifest.Source, manifest.Revision, held.Count));
            if (manifest.UpdatedAt != default)
            {
                earliest = earliest is null || manifest.UpdatedAt < earliest ? manifest.UpdatedAt : earliest;
                latest = latest is null || manifest.UpdatedAt > latest ? manifest.UpdatedAt : latest;
            }
        }

        return new AccessReport
        {
            Available = true,
            Scope = scope,
            SubjectId = keyed.Value.Id,
            ChunkCount = chunks.Count,
            Chunks = chunks,
            Sources = bySource
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new SubjectSourceCount(p.Key, p.Value.Count))
                .ToList(),
            Documents = byDocument.OrderBy(d => d.Document, StringComparer.Ordinal).ToList(),
            RevisionCount = byDocument.Count,
            NearCopyCount = near,
            Earliest = earliest,
            Latest = latest,
            Kinds = new[] { keyed.Value.Kind },
        };
    }

    private async ValueTask<List<DocumentManifest>> DocumentsUsingAsync(string scope, IReadOnlyList<ChunkId> chunks, CancellationToken ct)
    {
        var found = new List<DocumentManifest>();
        string? after = null;
        while (true)
        {
            var page = await _revisions!.ListDocumentsUsingAsync(scope, chunks, after, 100, ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                return found;
            }

            foreach (var doc in page)
            {
                var manifest = await _revisions.GetAsync(scope, doc.Document, ct).ConfigureAwait(false);
                if (manifest is not null)
                {
                    found.Add(manifest);
                }
            }

            if (page.Count < 100)
            {
                return found;
            }

            after = page[^1].Document.Value;
        }
    }

    private async ValueTask AuditReportAsync(string scope, AccessReport report, CancellationToken ct)
    {
        if (_audit is null)
        {
            return;
        }

        var counts = new Dictionary<string, int>
        {
            ["chunks"] = report.ChunkCount,
            ["sources"] = report.Sources.Count,
            ["documents"] = report.Documents.Count,
            ["revisions"] = report.RevisionCount,
            ["near_copies"] = report.NearCopyCount,
        };
        var auditEvent = new AuditEvent(
            new ChunkId("access"),
            new DocumentId(string.Empty),
            0,
            string.Empty,
            DateTimeOffset.UtcNow,
            string.Empty,
            AuditKind.AccessReport,
            "access report")
        {
            Scope = scope,
            Stage = AuditStages.Access,
            Actor = "access",
            Counts = counts,
        };
        await _audit.WriteAsync(new[] { auditEvent }, ct).ConfigureAwait(false);
    }
}
