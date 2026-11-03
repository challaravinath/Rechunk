namespace Rechunk;

/// <summary>A chunk coming out of your chunker, before Rechunk has looked at it.</summary>
public sealed record ChunkInput(int Ordinal, string Text);

/// <summary>What we remember about the text of one chunk.</summary>
public readonly record struct ChunkFingerprint(
    ReadOnlyMemory<byte> ExactHash,
    ulong SimHash,
    ReadOnlyMemory<uint> MinHash);

/// <summary>Result of adding to the exact hash index. Owner is the chunk that holds the hash.</summary>
public readonly record struct ExactAddResult(ChunkId Owner, bool Added);

public enum MatchStage
{
    Exact,
    SimHash,
    MinHash,
}

public sealed record NearDuplicateMatch(
    ChunkId Existing,
    MatchStage Stage,
    int HammingDistance,
    double EstimatedJaccard);

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
    string EmbeddingModel);

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

public sealed record RevisionResult(int EmbeddingCalls, int Kept, int Skipped, int Deleted);

public sealed record EraseRequest(
    string Scope,
    DocumentId? Document,
    string? Source,
    string Reason,
    string RequestedBy);

public sealed record EraseResult(int VectorsDeleted, int StateRowsDeleted);

public sealed record VerifyReport(
    IReadOnlyList<ChunkId> OrphanVectors,
    IReadOnlyList<ChunkId> MissingVectors)
{
    public bool IsClean => OrphanVectors.Count == 0 && MissingVectors.Count == 0;
}

public sealed record AuditEvent(
    ChunkId Chunk,
    DocumentId Document,
    int Revision,
    string Source,
    DateTimeOffset At,
    string EmbeddingModel,
    ChunkAction Action,
    string Reason);

public sealed record AuditQuery(
    DocumentId? Document = null,
    ChunkId? Chunk = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);
