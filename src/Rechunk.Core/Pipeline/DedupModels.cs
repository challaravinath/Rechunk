namespace Rechunk;

public enum DedupOutcome
{
    /// <summary>New text, send it to the embedder.</summary>
    Unique,

    /// <summary>Seen before, do not embed it. DuplicateOf says which chunk it matched.</summary>
    Duplicate,

    /// <summary>Seen before, but the policy is Flag so it still gets embedded.</summary>
    Flagged,

    /// <summary>
    /// Another worker holds the same text right now (shared store only). Do not embed it. Try again after the
    /// lease ends, by then it is either a duplicate or free to take.
    /// </summary>
    InFlight,
}

/// <summary>
/// What happened to one chunk. Stage and DuplicateOf are set when it matched something.
/// HammingDistance is set for a SimHash match (0 means the same SimHash, not the same text).
/// EstimatedJaccard is set for a MinHash match, and for a SimHash match that MinHash looked at.
/// Claim is set (shared store only) for a chunk that is to be embedded. Commit it after the vector write worked, or
/// release it when the write failed.
/// </summary>
public sealed record DedupDecision(
    ChunkInput Input,
    ChunkId Id,
    DedupOutcome Outcome,
    MatchStage? Stage,
    ChunkId? DuplicateOf,
    int? HammingDistance = null,
    double? EstimatedJaccard = null,
    ChunkClaim? Claim = null);

public sealed record DedupReport(string Scope, bool DryRun, IReadOnlyList<DedupDecision> Decisions)
{
    public int UniqueCount => Decisions.Count(d => d.Outcome == DedupOutcome.Unique);

    public int DuplicateCount => Decisions.Count(d => d.Outcome == DedupOutcome.Duplicate);

    public int FlaggedCount => Decisions.Count(d => d.Outcome == DedupOutcome.Flagged);

    public int InFlightCount => Decisions.Count(d => d.Outcome == DedupOutcome.InFlight);

    /// <summary>The claims that are waiting for a commit or a release.</summary>
    public IEnumerable<ChunkClaim> Claims => Decisions.Where(d => d.Claim is not null).Select(d => d.Claim!);
}

/// <summary>Who is asking and for how long, when the pipeline claims chunks in a shared store.</summary>
public sealed record DedupClaimOptions(string Holder, TimeSpan? Lease = null);
