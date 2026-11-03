using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rechunk;

/// <summary>
/// Runs chunks through the dedup stages.
///  Stage 1: exact hash, always on.
///  Stage 2: SimHash, when it is switched on in the options.
///  Stage 3: MinHash with LSH, not written yet.
/// A chunk that matches in an earlier stage never reaches the later ones.
/// </summary>
public sealed class RechunkPipeline
{
    private readonly RechunkOptions _options;
    private readonly IChunkFingerprinter _fingerprinter;
    private readonly IExactHashIndex _exactIndex;
    private readonly INearDuplicateIndex? _simHashIndex;
    private readonly ILogger _logger;

    public RechunkPipeline(
        RechunkOptions options,
        IChunkFingerprinter fingerprinter,
        IExactHashIndex exactIndex,
        ILogger? logger = null,
        INearDuplicateIndex? simHashIndex = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _fingerprinter = fingerprinter ?? throw new ArgumentNullException(nameof(fingerprinter));
        _exactIndex = exactIndex ?? throw new ArgumentNullException(nameof(exactIndex));
        _logger = logger ?? NullLogger.Instance;
        options.Validate();

        if (options.SimHash.Enabled)
        {
            _simHashIndex = simHashIndex ?? new SimHashBandIndex(options.SimHash.MaxHammingDistance);
        }
    }

    public static RechunkPipelineBuilder CreateBuilder() => new();

    /// <summary>Looks at a batch of chunks and says which ones are new and which ones are repeats.</summary>
    public async ValueTask<DedupReport> DeduplicateAsync(
        string? scope, IReadOnlyList<ChunkInput> chunks, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        scope ??= _options.DefaultScope;

        // In a dry run nothing is written to the indexes, so repeats inside the batch are tracked here.
        var dryRunExact = new Dictionary<string, ChunkId>();
        var dryRunSimHash = _options.DryRun && _simHashIndex is not null
            ? new SimHashBandIndex(_options.SimHash.MaxHammingDistance)
            : null;

        var skipNearDuplicates = _options.OnDuplicate != DuplicatePolicy.Flag;
        var decisions = new List<DedupDecision>(chunks.Count);

        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();

            var fingerprint = _fingerprinter.Compute(chunk.Text);
            var id = ChunkId.FromExactHash(fingerprint.ExactHash.Span);

            // Stage 1: have we seen exactly this text?
            var exact = await _exactIndex.FindAsync(scope, fingerprint.ExactHash, ct).ConfigureAwait(false);
            if (exact is null && dryRunExact.TryGetValue(id.Value, out var earlier))
            {
                exact = earlier;
            }

            if (exact is { } owner)
            {
                decisions.Add(Repeat(chunk, id, MatchStage.Exact, owner, null));
                LogMatch(chunk, MatchStage.Exact, owner, null);
                continue;
            }

            // Stage 2: have we seen something very close to it?
            NearDuplicateMatch? near = null;
            if (_simHashIndex is not null && fingerprint.HasSimHash)
            {
                near = await FindNearAsync(scope, fingerprint, dryRunSimHash, chunk, ct).ConfigureAwait(false);
            }

            // A near duplicate that is not embedded stands for the canonical chunk from now on. A flagged one is
            // embedded like any new chunk, so it is stored as itself.
            var storedAs = near is not null && skipNearDuplicates ? near.Existing : id;
            var embedded = near is null || !skipNearDuplicates;

            if (_options.DryRun)
            {
                dryRunExact[id.Value] = storedAs;
                if (embedded)
                {
                    dryRunSimHash?.Add(id, scope, fingerprint.SimHash, fingerprint.GuardSignature);
                }
            }
            else
            {
                var added = await _exactIndex.AddIfAbsentAsync(scope, fingerprint.ExactHash, storedAs, ct).ConfigureAwait(false);
                if (!added.Added)
                {
                    // another caller stored the same text between our lookup and now
                    decisions.Add(Repeat(chunk, id, MatchStage.Exact, added.Owner, null));
                    LogMatch(chunk, MatchStage.Exact, added.Owner, null);
                    continue;
                }

                if (embedded && _simHashIndex is not null && fingerprint.HasSimHash)
                {
                    await _simHashIndex.AddAsync(id, scope, fingerprint, ct).ConfigureAwait(false);
                }
            }

            if (near is null)
            {
                decisions.Add(new DedupDecision(chunk, id, DedupOutcome.Unique, null, null));
                continue;
            }

            decisions.Add(Repeat(chunk, id, MatchStage.SimHash, near.Existing, near.HammingDistance));
            LogMatch(chunk, MatchStage.SimHash, near.Existing, near.HammingDistance);
        }

        return new DedupReport(scope, _options.DryRun, decisions);
    }

    private async ValueTask<NearDuplicateMatch?> FindNearAsync(
        string scope,
        ChunkFingerprint fingerprint,
        SimHashBandIndex? dryRunIndex,
        ChunkInput chunk,
        CancellationToken ct)
    {
        var candidates = await _simHashIndex!.FindAsync(fingerprint, scope, ct).ConfigureAwait(false);

        IEnumerable<NearDuplicateMatch> all = candidates;
        if (dryRunIndex is not null)
        {
            all = all.Concat(dryRunIndex.Find(scope, fingerprint.SimHash));
        }

        NearDuplicateMatch? best = null;
        foreach (var candidate in all)
        {
            if (_options.GuardNumbersAndNegations && candidate.GuardSignature != fingerprint.GuardSignature)
            {
                _logger.LogDebug(
                    "Chunk {Ordinal} is close to {Existing} but differs in numbers or negations, keeping both",
                    chunk.Ordinal, Short(candidate.Existing.Value));
                continue;
            }

            if (best is null || candidate.HammingDistance < best.HammingDistance)
            {
                best = candidate;
            }
        }

        return best;
    }

    private DedupDecision Repeat(ChunkInput chunk, ChunkId id, MatchStage stage, ChunkId of, int? distance)
    {
        var outcome = _options.OnDuplicate == DuplicatePolicy.Flag ? DedupOutcome.Flagged : DedupOutcome.Duplicate;
        return new DedupDecision(chunk, id, outcome, stage, of, distance);
    }

    // never log the text, only a short piece of the hash
    private void LogMatch(ChunkInput chunk, MatchStage stage, ChunkId existing, int? distance) =>
        _logger.LogDebug(
            "Chunk {Ordinal} matched {Stage} with {Existing} (distance {Distance})",
            chunk.Ordinal, stage, Short(existing.Value), distance);

    private static string Short(string value) => value.Length <= 8 ? value : value[..8];
}
