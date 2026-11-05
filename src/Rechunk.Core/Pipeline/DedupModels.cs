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

    /// <summary>
    /// A finding with the Block policy. The chunk is not fingerprinted, claimed, embedded or stored, and its Id is empty.
    /// Input holds the text with the findings masked.
    /// </summary>
    Blocked,
}

/// <summary>A stored chunk that a text matched, see RechunkPipeline.FindStoredAsync. Id is the id the text itself gets.</summary>
public sealed record StoredMatch(
    ChunkId Id,
    ChunkId Existing,
    MatchStage Stage,
    int? HammingDistance,
    double? EstimatedJaccard,
    string? Language);

/// <summary>
/// What happened to one chunk. Stage and DuplicateOf are set when it matched something.
/// HammingDistance is set for a SimHash match (0 means the same SimHash, not the same text).
/// EstimatedJaccard is set for a MinHash match, and for a SimHash match that MinHash looked at.
/// Language is the tag the chunk was matched under (its own, or the detected one), null when untagged.
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
    ChunkClaim? Claim = null,
    string? Language = null)
{
    /// <summary>What the PII scan found, with the policy applied to each finding. Types and spans, never the values.</summary>
    public IReadOnlyList<PiiHit> Findings => Input.PiiFindings ?? Array.Empty<PiiHit>();
}

public sealed record DedupReport(string Scope, bool DryRun, IReadOnlyList<DedupDecision> Decisions)
{
    public int UniqueCount => Decisions.Count(d => d.Outcome == DedupOutcome.Unique);

    public int DuplicateCount => Decisions.Count(d => d.Outcome == DedupOutcome.Duplicate);

    public int FlaggedCount => Decisions.Count(d => d.Outcome == DedupOutcome.Flagged);

    public int BlockedCount => Decisions.Count(d => d.Outcome == DedupOutcome.Blocked);

    public int InFlightCount => Decisions.Count(d => d.Outcome == DedupOutcome.InFlight);

    /// <summary>The claims that are waiting for a commit or a release.</summary>
    public IEnumerable<ChunkClaim> Claims => Decisions.Where(d => d.Claim is not null).Select(d => d.Claim!);
}

/// <summary>Who is asking and for how long, when the pipeline claims chunks in a shared store.</summary>
/// <param name="Holder">Names the piece of work, for example the queue message id.</param>
/// <param name="Lease">How long the claims last. The store default is used when null.</param>
/// <param name="NearExclude">
/// Chunks the near duplicate stages must not match against. Revision tracking uses it for the old chunks of the same
/// document that are being replaced, so an edited chunk is embedded again and does not point at its old self.
/// </param>
public sealed record DedupClaimOptions(string Holder, TimeSpan? Lease = null, IReadOnlySet<ChunkId>? NearExclude = null);
