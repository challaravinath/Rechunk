namespace Rechunk;

/// <summary>
/// One place for the fingerprints of every chunk, shared by all workers. It holds the three things the
/// dedup stages look at: the exact hashes, the SimHash band index and the MinHash LSH index.
///
/// A chunk goes through a short lifecycle so that a failure never leaves it marked as seen:
///
///   Claim    pending, with a lease. Only one holder can have it. Other workers that bring the same text
///            are told it is in flight and should come back later.
///   Commit   called after the vector write worked. The hash is now final and the near duplicate indexes
///            know about the chunk.
///   Release  called when the work failed. The pending claim is dropped so the next try can take it.
///
/// If a worker dies and never calls Commit or Release, the lease runs out and another holder can take over.
/// Everything is scoped per tenant, nothing matches across scopes.
/// </summary>
public interface IFingerprintStore
{
    /// <summary>Stage 1 as the pipeline sees it. Find only returns hashes that are committed.</summary>
    IExactHashIndex Exact { get; }

    /// <summary>Stage 2. Only committed chunks are in here.</summary>
    INearDuplicateIndex SimHash { get; }

    /// <summary>Stage 3. Only committed chunks are in here.</summary>
    IMinHashIndex MinHash { get; }

    /// <summary>
    /// Atomic add if new. Exactly one caller wins when many claim the same text at once. Asking again with the
    /// same holder and chunk (a retry) is fine and just extends the lease.
    /// </summary>
    ValueTask<ClaimResult> ClaimAsync(ClaimRequest request, CancellationToken ct = default);

    /// <summary>
    /// Makes a claim final. Safe to call twice. If the lease ran out but nobody took the hash over, the
    /// original holder can still commit. Says NotHeld when somebody else owns it now or the claim is gone.
    /// </summary>
    ValueTask<CommitStatus> CommitAsync(CommitRequest request, CancellationToken ct = default);

    /// <summary>
    /// Drops a pending claim, and anything it added to the near indexes. Does nothing for a committed hash or for
    /// a claim that belongs to somebody else. Says whether a claim was dropped.
    /// </summary>
    ValueTask<bool> ReleaseAsync(ReleaseRequest request, CancellationToken ct = default);
}

/// <summary>Ask for a hash. Holder is whatever names one piece of work, for example the queue message id.</summary>
public sealed record ClaimRequest(
    string Scope,
    ChunkId Id,
    ReadOnlyMemory<byte> ExactHash,
    string Holder,
    TimeSpan Lease);

public enum ClaimStatus
{
    /// <summary>New hash, it is yours until the lease ends.</summary>
    Claimed,

    /// <summary>You already held it (a retry). The lease was extended.</summary>
    Renewed,

    /// <summary>The earlier holder's lease had ended and you took it over.</summary>
    TakenOver,

    /// <summary>Somebody finished this text already. Owner is the chunk that stands for it.</summary>
    AlreadyCommitted,

    /// <summary>Somebody else holds it right now. Owner is their chunk, LeaseUntil says when it frees up.</summary>
    InFlight,
}

public readonly record struct ClaimResult(ClaimStatus Status, ChunkId Owner, DateTimeOffset? LeaseUntil = null)
{
    /// <summary>True when the caller holds the claim and should do the work.</summary>
    public bool Won => Status is ClaimStatus.Claimed or ClaimStatus.Renewed or ClaimStatus.TakenOver;
}

/// <summary>
/// AliasOf is set when the chunk is not embedded and stands for another chunk (a near duplicate that is
/// skipped). Fingerprint is added to the near indexes, leave it null when there is nothing to add.
/// </summary>
public sealed record CommitRequest(
    string Scope,
    ChunkId Id,
    ReadOnlyMemory<byte> ExactHash,
    string Holder,
    ChunkId? AliasOf = null,
    ChunkFingerprint? Fingerprint = null);

public enum CommitStatus
{
    Committed,

    /// <summary>It was committed before, by the same chunk. Nothing changed.</summary>
    AlreadyCommitted,

    /// <summary>The claim is not yours (any more) so nothing was committed.</summary>
    NotHeld,
}

public sealed record ReleaseRequest(string Scope, ChunkId Id, ReadOnlyMemory<byte> ExactHash, string Holder);

/// <summary>
/// What a dedup run hands back for a chunk it claimed. Pass it to the store to commit or release once you
/// know if the vector write worked.
///
/// AliasOf is set for a chunk that is a near duplicate of another chunk in the same batch. It is not embedded, and
/// it can only be committed once that other chunk is. If the other chunk failed, release it.
/// </summary>
public sealed record ChunkClaim(
    string Scope,
    ChunkId Id,
    ReadOnlyMemory<byte> ExactHash,
    string Holder,
    ChunkFingerprint Fingerprint,
    ChunkId? AliasOf = null)
{
    public CommitRequest ToCommit() => new(Scope, Id, ExactHash, Holder, AliasOf, AliasOf is null ? Fingerprint : null);

    public ReleaseRequest ToRelease() => new(Scope, Id, ExactHash, Holder);
}
