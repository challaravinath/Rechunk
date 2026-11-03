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

public sealed record DedupDecision(
    ChunkInput Input,
    ChunkId Id,
    DedupOutcome Outcome,
    MatchStage? Stage,
    ChunkId? DuplicateOf);

public sealed record DedupReport(string Scope, bool DryRun, IReadOnlyList<DedupDecision> Decisions)
{
    public int UniqueCount => Decisions.Count(d => d.Outcome == DedupOutcome.Unique);

    public int DuplicateCount => Decisions.Count(d => d.Outcome == DedupOutcome.Duplicate);

    public int FlaggedCount => Decisions.Count(d => d.Outcome == DedupOutcome.Flagged);
}
