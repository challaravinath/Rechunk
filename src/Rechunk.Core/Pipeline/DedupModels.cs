namespace Rechunk;

public enum DedupOutcome
{
    /// <summary>New text, send it to the embedder.</summary>
    Unique,

    /// <summary>Seen before, do not embed it. DuplicateOf says which chunk it matched.</summary>
    Duplicate,

    /// <summary>Seen before, but the policy is Flag so it still gets embedded.</summary>
    Flagged,
}

/// <summary>
/// What happened to one chunk. Stage and DuplicateOf are set when it matched something.
/// HammingDistance is set for a SimHash match (0 means the same SimHash, not the same text).
/// EstimatedJaccard is set for a MinHash match, and for a SimHash match that MinHash looked at.
/// </summary>
public sealed record DedupDecision(
    ChunkInput Input,
    ChunkId Id,
    DedupOutcome Outcome,
    MatchStage? Stage,
    ChunkId? DuplicateOf,
    int? HammingDistance = null,
    double? EstimatedJaccard = null);

public sealed record DedupReport(string Scope, bool DryRun, IReadOnlyList<DedupDecision> Decisions)
{
    public int UniqueCount => Decisions.Count(d => d.Outcome == DedupOutcome.Unique);

    public int DuplicateCount => Decisions.Count(d => d.Outcome == DedupOutcome.Duplicate);

    public int FlaggedCount => Decisions.Count(d => d.Outcome == DedupOutcome.Flagged);
}
