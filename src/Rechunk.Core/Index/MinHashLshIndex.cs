namespace Rechunk;

/// <summary>MinHash index with LSH banding. Not written yet.</summary>
public sealed class MinHashLshIndex : INearDuplicateIndex
{
    // TODO:
    //  - bands and rows set by hand, or derived from the threshold
    //  - only chunks that match in at least one band become candidates
    //  - confirm candidates by comparing the full signatures

    public ValueTask AddAsync(ChunkId id, string scope, ChunkFingerprint fingerprint, CancellationToken ct = default) =>
        throw new NotImplementedException("MinHash index is not written yet.");

    public ValueTask RemoveAsync(ChunkId id, string scope, CancellationToken ct = default) =>
        throw new NotImplementedException("MinHash index is not written yet.");

    public ValueTask<IReadOnlyList<NearDuplicateMatch>> FindAsync(
        ChunkFingerprint fingerprint, string scope, CancellationToken ct = default) =>
        throw new NotImplementedException("MinHash index is not written yet.");
}
