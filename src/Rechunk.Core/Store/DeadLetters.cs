namespace Rechunk;

/// <summary>Where in the ingest a chunk gave up.</summary>
public enum DeadLetterStage
{
    /// <summary>Looking the chunk up or claiming it in the fingerprint store.</summary>
    Dedup,

    Embed,

    /// <summary>Writing the vector to the vector store.</summary>
    VectorWrite,

    /// <summary>Marking the fingerprint as final after the vector was written.</summary>
    Commit,
}

/// <summary>
/// A chunk that kept failing. It holds enough to find the chunk again and see why it failed, and no chunk text,
/// unless StoreTextInDeadLetters is switched on. Language is the tag the chunk had, so a replay puts it back in the right group. Reason is the exception type and a shortened message.
/// </summary>
public sealed record DeadLetter(
    string Id,
    string Scope,
    ChunkId Chunk,
    DocumentId? Document,
    int Ordinal,
    DeadLetterStage Stage,
    string Reason,
    int Attempts,
    DateTimeOffset FailedAt,
    string? Text = null,
    string? Language = null);

public sealed record DeadLetterQuery(
    string? Scope = null,
    DeadLetterStage? Stage = null,
    ChunkId? Chunk = null,
    DocumentId? Document = null,
    int Skip = 0,
    int Take = 100);

public interface IDeadLetterStore
{
    /// <summary>Saves the record. A record with the same Id replaces the old one.</summary>
    ValueTask AddAsync(DeadLetter deadLetter, CancellationToken ct = default);

    ValueTask<DeadLetter?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Oldest first.</summary>
    ValueTask<IReadOnlyList<DeadLetter>> ListAsync(DeadLetterQuery query, CancellationToken ct = default);

    ValueTask<long> CountAsync(DeadLetterQuery query, CancellationToken ct = default);

    /// <summary>Says whether there was a record to remove.</summary>
    ValueTask<bool> RemoveAsync(string id, CancellationToken ct = default);
}
