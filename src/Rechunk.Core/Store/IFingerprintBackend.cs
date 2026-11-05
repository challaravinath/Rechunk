namespace Rechunk;

public enum BandKind
{
    SimHash = 1,
    MinHash = 2,
}

/// <summary>One slice of a fingerprint: the band number and the value of that band.</summary>
public readonly record struct BandKey(int Band, ulong Key);

/// <summary>A stored fingerprint of the near duplicate indexes. The payload means nothing to the backend.</summary>
public sealed record BandEntry(string Id, byte[] Payload);

/// <summary>
/// What a database has to offer for the near duplicate indexes: rows filed under band keys, found again by key.
/// All the SimHash and MinHash logic stays in Rechunk, so a new backend only moves bytes around.
/// Every method is scoped, and one scope never sees the rows of another.
/// </summary>
public interface IBandStorage
{
    /// <summary>
    /// Stores the payload under the id and files the id under every key. If the id is already there the payload is
    /// replaced. An id always carries the same fingerprint (it comes from the text), so the keys stay the same.
    /// </summary>
    ValueTask PutAsync(
        string scope, BandKind kind, string id, byte[] payload, IReadOnlyList<BandKey> keys, CancellationToken ct = default);

    /// <summary>Removes the id and its keys. Says whether it was there.</summary>
    ValueTask<bool> DeleteAsync(
        string scope, BandKind kind, string id, IReadOnlyList<BandKey> keys, CancellationToken ct = default);

    ValueTask<byte[]?> GetAsync(string scope, BandKind kind, string id, CancellationToken ct = default);

    /// <summary>Every stored entry filed under at least one of the keys, each one once.</summary>
    ValueTask<IReadOnlyList<BandEntry>> QueryAsync(
        string scope, BandKind kind, IReadOnlyList<BandKey> keys, CancellationToken ct = default);
}

/// <summary>
/// The part of a shared store that a database implements: the exact hash rows with their pending and
/// committed state, plus the band storage. The rules for each call are the ones on IFingerprintStore.
/// Times come from the caller so that every backend and every test uses the same clock.
/// </summary>
public interface IFingerprintBackend : IBandStorage
{
    /// <summary>Has to be atomic: with many callers for the same hash, exactly one of them wins.</summary>
    ValueTask<ClaimResult> ClaimAsync(ClaimRequest request, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Only the exact hash row. The near indexes are not touched here.</summary>
    ValueTask<CommitStatus> CommitAsync(CommitRequest request, CancellationToken ct = default);

    ValueTask<bool> ReleaseAsync(ReleaseRequest request, CancellationToken ct = default);

    /// <summary>The owner of a committed hash, or null when it is unknown or still pending.</summary>
    ValueTask<ChunkId?> FindCommittedAsync(string scope, ReadOnlyMemory<byte> exactHash, CancellationToken ct = default);

    /// <summary>Removes the row whatever its state. Says whether it was there.</summary>
    ValueTask<bool> RemoveAsync(string scope, ReadOnlyMemory<byte> exactHash, CancellationToken ct = default);

    /// <summary>
    /// Removes the row only when it is committed and belongs to the owner (a chunk's own hash and the hashes of the
    /// duplicates that point at it). A row that is pending, or owned by another chunk, stays. Says whether a row went.
    /// </summary>
    ValueTask<bool> RemoveOwnedAsync(string scope, ReadOnlyMemory<byte> exactHash, ChunkId owner, CancellationToken ct = default);
}
