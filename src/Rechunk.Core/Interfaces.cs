namespace Rechunk;

public interface IChunkFingerprinter
{
    /// <summary>Stored with every fingerprint, so a change in the algorithm is never silent.</summary>
    string AlgorithmVersion { get; }

    ChunkFingerprint Compute(ReadOnlySpan<char> text);
}

/// <summary>Stage 1: exact matches on the hash of the cleaned up text.</summary>
public interface IExactHashIndex
{
    ValueTask<ChunkId?> FindAsync(string scope, ReadOnlyMemory<byte> exactHash, CancellationToken ct = default);

    /// <summary>Adds the hash if it is new. Says who owns the hash now and whether this call was the one that added it.</summary>
    ValueTask<ExactAddResult> AddIfAbsentAsync(string scope, ReadOnlyMemory<byte> exactHash, ChunkId id, CancellationToken ct = default);

    ValueTask<bool> RemoveAsync(string scope, ReadOnlyMemory<byte> exactHash, CancellationToken ct = default);
}

/// <summary>Stage 2 and 3: the SimHash index and the MinHash LSH index.</summary>
public interface INearDuplicateIndex
{
    ValueTask AddAsync(ChunkId id, string scope, ChunkFingerprint fingerprint, CancellationToken ct = default);

    ValueTask RemoveAsync(ChunkId id, string scope, CancellationToken ct = default);

    ValueTask<IReadOnlyList<NearDuplicateMatch>> FindAsync(
        ChunkFingerprint fingerprint, string scope, CancellationToken ct = default);
}

/// <summary>The MinHash index. Same as the SimHash one, plus a way to ask how similar a stored chunk is.</summary>
public interface IMinHashIndex : INearDuplicateIndex
{
    /// <summary>
    /// Estimated Jaccard similarity between the signature and the stored chunk, or null if that chunk is
    /// not stored in the scope. Used to confirm a SimHash match.
    /// </summary>
    ValueTask<double?> EstimateJaccardAsync(
        string scope, ChunkId id, ReadOnlyMemory<uint> signature, CancellationToken ct = default);
}

/// <summary>Turns text into vectors. The Microsoft.Extensions.AI package will have an adapter for this.</summary>
public interface IChunkEmbedder
{
    string ModelId { get; }

    ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default);
}

/// <summary>The small gap between Rechunk and whatever vector store you use.</summary>
public interface IVectorSink
{
    ValueTask UpsertAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken ct = default);

    ValueTask DeleteAsync(IReadOnlyList<ChunkId> ids, CancellationToken ct = default);

    /// <summary>
    /// Deletes by scope and id. Revision tracking calls this one. A chunk id comes from the text, so the same text in two
    /// scopes has the same id: if your records are keyed by id alone, a delete here removes the vector for every scope.
    /// Key your records by scope and id (EmbeddedChunk carries the scope) and override this method to avoid that.
    /// The default just calls the method above, so existing sinks keep working. Deleting an id that is not there
    /// must not fail, because a delete can be repeated.
    /// </summary>
    ValueTask DeleteAsync(string scope, IReadOnlyList<ChunkId> ids, CancellationToken ct = default) => DeleteAsync(ids, ct);

    IAsyncEnumerable<ChunkId> ListIdsAsync(string scope, CancellationToken ct = default);
}

public interface IRevisionTracker
{
    /// <summary>Decide what to do with the new chunks of a document. Changes nothing.</summary>
    ValueTask<RevisionPlan> PlanAsync(
        DocumentId document, string scope, IReadOnlyList<ChunkInput> newChunks, CancellationToken ct = default);

    /// <summary>
    /// Carries out a plan: embeds the new and changed chunks and writes their vectors, switches the document to the new
    /// revision, then deletes the vectors and fingerprints of chunks that nothing uses any more and writes the audit events.
    /// </summary>
    ValueTask<RevisionResult> ApplyAsync(
        RevisionPlan plan, IChunkEmbedder embedder, IVectorSink sink, CancellationToken ct = default);

    /// <summary>Right to erasure. Removes vectors, fingerprints and state, and leaves a tombstone without the text.</summary>
    ValueTask<EraseResult> EraseAsync(EraseRequest request, IVectorSink sink, CancellationToken ct = default);

    /// <summary>Compares what the state says should exist with what the vector store really holds.</summary>
    ValueTask<VerifyReport> VerifyAsync(string scope, IVectorSink sink, CancellationToken ct = default);
}

public interface IAuditLog
{
    ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken ct = default);

    IAsyncEnumerable<AuditEvent> QueryAsync(AuditQuery query, CancellationToken ct = default);
}
