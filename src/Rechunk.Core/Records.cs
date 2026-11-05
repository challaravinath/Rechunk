namespace Rechunk;

/// <summary>
/// A chunk coming out of your chunker, before Rechunk has looked at it. Language is optional: a tag like "en" or "zh".
/// Chunks of different languages are never treated as duplicates of each other. Leave it null to let the detector set in
/// the options decide, or to keep the chunk untagged.
/// </summary>
public sealed record ChunkInput(int Ordinal, string Text, string? Language = null)
{
    // set by the PII scanner, so a chunk is scanned once and its text is already masked when it has this
    internal PiiScan? Scan { get; init; }

    /// <summary>What the PII scan found in this chunk, or null when no scan ran. Types and spans only.</summary>
    public IReadOnlyList<PiiHit>? PiiFindings => Scan?.Hits;
}

/// <summary>
/// What we remember about the text of one chunk.
/// SimHash is 0 and MinHash is empty when the stage is off or the chunk is too short for it to mean anything.
/// GuardSignature comes from the false merge guard, and is 0 when the guard is off.
/// </summary>
public readonly record struct ChunkFingerprint(
    ReadOnlyMemory<byte> ExactHash,
    ulong SimHash,
    ReadOnlyMemory<uint> MinHash,
    ulong GuardSignature = 0)
{
    public bool HasSimHash => SimHash != 0;

    /// <summary>False when the stage is off or the chunk is too short.</summary>
    public bool HasMinHash => !MinHash.IsEmpty;
}

/// <summary>Result of adding to the exact hash index. Owner is the chunk that holds the hash.</summary>
public readonly record struct ExactAddResult(ChunkId Owner, bool Added);

public enum MatchStage
{
    Exact,
    SimHash,
    MinHash,
}

/// <summary>
/// A chunk found by a near duplicate index. HammingDistance is filled by the SimHash stage and
/// EstimatedJaccard by the MinHash stage (and by the pipeline when MinHash confirmed a SimHash match).
/// GuardSignature is the one stored with the chunk, so the pipeline can run the false merge guard without having the old text.
/// </summary>
public sealed record NearDuplicateMatch(
    ChunkId Existing,
    MatchStage Stage,
    int? HammingDistance,
    double? EstimatedJaccard = null,
    ulong GuardSignature = 0);

public enum ChunkAction
{
    Keep,
    Embed,
    Replace,
    Alias,
    Delete,
}

public enum DuplicatePolicy
{
    /// <summary>Do not embed it, nothing else.</summary>
    Skip,

    /// <summary>Do not embed it, but remember its source next to the first copy.</summary>
    Alias,

    /// <summary>Embed it anyway and just report the duplicate.</summary>
    Flag,
}

public enum RevisionPolicy
{
    /// <summary>Re-embed chunks whose text changed, even a little.</summary>
    ReEmbedChanged,

    /// <summary>Keep the old vector for small edits. Cheaper, but the vector can go stale.</summary>
    ReuseForSmallEdits,
}

/// <summary>A chunk with its vector, ready to go to a vector store.</summary>
public sealed record EmbeddedChunk(
    ChunkId Id,
    DocumentId Document,
    int Revision,
    string Text,
    ReadOnlyMemory<float> Vector,
    string EmbeddingModel,
    string? Scope = null,
    string? Language = null);

public sealed record PlannedChunk(
    ChunkInput Input,
    ChunkId Id,
    ChunkAction Action,
    ChunkId? AliasOf,
    string Reason);

public sealed record RevisionPlan(
    DocumentId Document,
    string Scope,
    int FromRevision,
    int ToRevision,
    IReadOnlyList<PlannedChunk> Chunks,
    IReadOnlyList<ChunkId> ToDelete);

public enum RevisionStatus
{
    /// <summary>The new revision is the current one.</summary>
    Applied,

    /// <summary>The chunks are the same as the current revision, in the same order. Nothing was written.</summary>
    Unchanged,

    /// <summary>
    /// Some chunks are being handled by another worker, or went to the dead letter store. The document still points at
    /// its old revision. Run the same revision again later: chunks that were stored are reused.
    /// </summary>
    Incomplete,

    /// <summary>Another worker kept changing the same document and the retries ran out. Nothing changed.</summary>
    Conflict,

    /// <summary>A chunk that had to be reused was being deleted and the retries ran out. Nothing changed.</summary>
    Blocked,
}

/// <summary>
/// What a revision did. EmbeddingCalls is how many times the embedder was called. Kept counts chunks that were not
/// touched (Moved of them changed position), Skipped counts new chunks that were not embedded because the text is stored
/// already, and Deleted counts chunks whose vector was removed because nothing uses them any more.
/// </summary>
public sealed record RevisionResult(
    int EmbeddingCalls,
    int Kept,
    int Skipped,
    int Deleted,
    RevisionStatus Status = RevisionStatus.Applied,
    int Revision = 0,
    int Embedded = 0,
    int Moved = 0,
    int InFlight = 0,
    int DeadLettered = 0,
    int PiiBlocked = 0,
    int AuditPending = 0);

public sealed record EraseRequest(
    string Scope,
    DocumentId? Document,
    string? Source,
    string Reason,
    string RequestedBy);

/// <summary>
/// VectorsDeleted counts vectors that were removed. A chunk that another document still uses stays, so it is not
/// counted. StateRowsDeleted counts fingerprint rows and dead letters that were removed. Documents is how many documents
/// were deleted, Chunks how many chunks lost their last use, and ReceiptId is the id of the audit event that records the
/// erasure, which holds counts and no content.
/// </summary>
public sealed record EraseResult(
    int VectorsDeleted,
    int StateRowsDeleted,
    int Documents = 0,
    int Chunks = 0,
    string? ReceiptId = null);

public sealed record VerifyReport(
    IReadOnlyList<ChunkId> OrphanVectors,
    IReadOnlyList<ChunkId> MissingVectors)
{
    public bool IsClean => OrphanVectors.Count == 0 && MissingVectors.Count == 0;
}
