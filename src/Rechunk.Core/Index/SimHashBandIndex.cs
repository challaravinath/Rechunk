namespace Rechunk;

/// <summary>Banded SimHash index. Not written yet.</summary>
public sealed class SimHashBandIndex : INearDuplicateIndex
{
    // TODO:
    //  - split the 64 bits into 4 slices of 16 bits, so every pair within 3 bits shares a slice
    //  - lookup by slice, then check the real Hamming distance
    //  - test it against a brute force scan, it must not miss anything

    public ValueTask AddAsync(ChunkId id, string scope, ChunkFingerprint fingerprint, CancellationToken ct = default) =>
        throw new NotImplementedException("SimHash index is not written yet.");

    public ValueTask RemoveAsync(ChunkId id, string scope, CancellationToken ct = default) =>
        throw new NotImplementedException("SimHash index is not written yet.");

    public ValueTask<IReadOnlyList<NearDuplicateMatch>> FindAsync(
        ChunkFingerprint fingerprint, string scope, CancellationToken ct = default) =>
        throw new NotImplementedException("SimHash index is not written yet.");
}
