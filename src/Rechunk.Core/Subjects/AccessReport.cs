namespace Rechunk;

/// <summary>
/// What this scope holds about one subject. Counts, sources, document ids, revision numbers and dates.
/// It does not contain chunk text, a fingerprint, or the subject value. SubjectId is the keyed id.
/// </summary>
public sealed record AccessReport
{
    /// <summary>False when this scope has no key provider, so there is no subject index to read.</summary>
    public bool Available { get; init; }

    public string Scope { get; init; } = string.Empty;

    /// <summary>The keyed subject id, or null when the report is not available or the value was empty.</summary>
    public string? SubjectId { get; init; }

    public int ChunkCount { get; init; }

    public IReadOnlyList<ChunkId> Chunks { get; init; } = Array.Empty<ChunkId>();

    /// <summary>How many linked chunks sit in each source. An empty source means the document was stored without one.</summary>
    public IReadOnlyList<SubjectSourceCount> Sources { get; init; } = Array.Empty<SubjectSourceCount>();

    /// <summary>Documents whose current revision includes a linked chunk.</summary>
    public IReadOnlyList<SubjectDocumentCount> Documents { get; init; } = Array.Empty<SubjectDocumentCount>();

    /// <summary>How many current document revisions include a linked chunk. Zero when no revision store is in use.</summary>
    public int RevisionCount { get; init; }

    /// <summary>
    /// Manifest entries that point at a linked chunk and are not that chunk. The subject index already links the chunk
    /// the near copy was stored as. This does not search for near copies that were never linked.
    /// </summary>
    public int NearCopyCount { get; init; }

    /// <summary>Earliest document revision time among the documents above. Null when none of those documents has a time.</summary>
    public DateTimeOffset? Earliest { get; init; }

    /// <summary>Latest document revision time among the documents above. Null when none of those documents has a time.</summary>
    public DateTimeOffset? Latest { get; init; }

    /// <summary>Kinds on the links that matched. Empty when nothing is linked.</summary>
    public IReadOnlyList<string> Kinds { get; init; } = Array.Empty<string>();

    public static AccessReport NotAvailable(string scope) => new() { Scope = scope, Available = false };

    public static AccessReport Empty(string scope, string? subjectId) => new()
    {
        Scope = scope,
        Available = true,
        SubjectId = subjectId,
    };
}

/// <summary>Linked chunks that share a source.</summary>
public sealed record SubjectSourceCount(string Source, int Chunks);

/// <summary>Linked chunks in one document, and the revision that document is on.</summary>
public sealed record SubjectDocumentCount(string Document, string? Source, int Revision, int Chunks);
