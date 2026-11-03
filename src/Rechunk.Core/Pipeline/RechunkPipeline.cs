using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rechunk;

/// <summary>
/// Runs chunks through the dedup stages. A chunk that matches in an earlier stage never reaches the later ones.
///
///  Stage 1, exact hash: always on. Catches the same text after cleanup.
///  Stage 2, SimHash: quick and rough. Finds chunks that are only a few bits away.
///  Stage 3, MinHash with LSH: finds chunks whose word shingles overlap by at least the threshold.
///
/// When both near stages are on, MinHash does two jobs. It confirms what SimHash found (unless
/// SimHash.ConfirmWithMinHash is false), and it catches looser duplicates that SimHash missed.
/// The false merge guard applies to both: chunks whose numbers or negations differ are never merged.
///
/// A chunk that is skipped as a near duplicate is not added to the near indexes. The chunk it matched
/// stands for it from then on, and its exact hash is pointed at that chunk. This keeps the indexes small
/// but also means a chain of small edits (A, then B close to A, then C close to B but not to A) is not followed.
/// </summary>
public sealed class RechunkPipeline
{
    private readonly RechunkOptions _options;
    private readonly IChunkFingerprinter _fingerprinter;
    private readonly IExactHashIndex _exactIndex;
    private readonly INearDuplicateIndex? _simHashIndex;
    private readonly IMinHashIndex? _minHashIndex;
    private readonly ILogger _logger;

    public RechunkPipeline(
        RechunkOptions options,
        IChunkFingerprinter fingerprinter,
        IExactHashIndex exactIndex,
        ILogger? logger = null,
        INearDuplicateIndex? simHashIndex = null,
        IMinHashIndex? minHashIndex = null)
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

        if (options.MinHash.Enabled)
        {
            _minHashIndex = minHashIndex ?? new MinHashLshIndex(options.MinHash);
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
        var dryRunMinHash = _options.DryRun && _minHashIndex is not null
            ? new MinHashLshIndex(_options.MinHash)
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
                decisions.Add(Repeat(chunk, id, MatchStage.Exact, owner, null, null));
                LogMatch(chunk, MatchStage.Exact, owner, null, null);
                continue;
            }

            // Stages 2 and 3: have we seen something very close to it?
            var near = await FindNearAsync(scope, fingerprint, dryRunSimHash, dryRunMinHash, chunk, ct).ConfigureAwait(false);

            // A near duplicate that is not embedded stands for the canonical chunk from now on. A flagged one is
            // embedded like any new chunk, so it is stored as itself.
            var storedAs = near is not null && skipNearDuplicates ? near.Existing : id;
            var embedded = near is null || !skipNearDuplicates;

            if (_options.DryRun)
            {
                dryRunExact[id.Value] = storedAs;
                if (embedded)
                {
                    AddToNearIndexes(id, scope, fingerprint, dryRunSimHash, dryRunMinHash);
                }
            }
            else
            {
                var added = await _exactIndex.AddIfAbsentAsync(scope, fingerprint.ExactHash, storedAs, ct).ConfigureAwait(false);
                if (!added.Added)
                {
                    // another caller stored the same text between our lookup and now
                    decisions.Add(Repeat(chunk, id, MatchStage.Exact, added.Owner, null, null));
                    LogMatch(chunk, MatchStage.Exact, added.Owner, null, null);
                    continue;
                }

                if (embedded)
                {
                    if (_simHashIndex is not null && fingerprint.HasSimHash)
                    {
                        await _simHashIndex.AddAsync(id, scope, fingerprint, ct).ConfigureAwait(false);
                    }

                    if (_minHashIndex is not null && fingerprint.HasMinHash)
                    {
                        await _minHashIndex.AddAsync(id, scope, fingerprint, ct).ConfigureAwait(false);
                    }
                }
            }

            if (near is null)
            {
                decisions.Add(new DedupDecision(chunk, id, DedupOutcome.Unique, null, null));
                continue;
            }

            decisions.Add(Repeat(chunk, id, near.Stage, near.Existing, near.HammingDistance, near.EstimatedJaccard));
            LogMatch(chunk, near.Stage, near.Existing, near.HammingDistance, near.EstimatedJaccard);
        }

        return new DedupReport(scope, _options.DryRun, decisions);
    }

    private void AddToNearIndexes(
        ChunkId id, string scope, ChunkFingerprint fingerprint, SimHashBandIndex? simHashOverlay, MinHashLshIndex? minHashOverlay)
    {
        if (fingerprint.HasSimHash)
        {
            simHashOverlay?.Add(id, scope, fingerprint.SimHash, fingerprint.GuardSignature);
        }

        if (fingerprint.HasMinHash)
        {
            minHashOverlay?.Add(id, scope, fingerprint.MinHash.Span, fingerprint.GuardSignature);
        }
    }

    private async ValueTask<NearDuplicateMatch?> FindNearAsync(
        string scope,
        ChunkFingerprint fingerprint,
        SimHashBandIndex? simHashOverlay,
        MinHashLshIndex? minHashOverlay,
        ChunkInput chunk,
        CancellationToken ct)
    {
        var minHashOn = _minHashIndex is not null && fingerprint.HasMinHash;

        // Stage 2: SimHash candidates, closest first.
        if (_simHashIndex is not null && fingerprint.HasSimHash)
        {
            var found = await _simHashIndex.FindAsync(fingerprint, scope, ct).ConfigureAwait(false);
            IEnumerable<NearDuplicateMatch> all = found;
            if (simHashOverlay is not null)
            {
                all = all.Concat(simHashOverlay.Find(scope, fingerprint.SimHash));
            }

            foreach (var candidate in all.OrderBy(c => c.HammingDistance).ThenBy(c => c.Existing.Value, StringComparer.Ordinal))
            {
                if (Blocked(fingerprint, candidate, chunk))
                {
                    continue;
                }

                double? jaccard = null;
                if (minHashOn)
                {
                    jaccard = await EstimateAsync(scope, candidate.Existing, fingerprint, minHashOverlay, ct).ConfigureAwait(false);
                }

                if (minHashOn && _options.SimHash.ConfirmWithMinHash && (jaccard is null || jaccard < _options.MinHash.Threshold))
                {
                    _logger.LogDebug(
                        "Chunk {Ordinal} is close to {Existing} by SimHash but MinHash does not confirm it (Jaccard {Jaccard})",
                        chunk.Ordinal, Short(candidate.Existing.Value), jaccard);
                    continue;
                }

                return candidate with { EstimatedJaccard = jaccard };
            }
        }

        // Stage 3: MinHash on its own, for what SimHash did not catch.
        if (minHashOn)
        {
            var found = await _minHashIndex!.FindAsync(fingerprint, scope, ct).ConfigureAwait(false);
            IEnumerable<NearDuplicateMatch> all = found;
            if (minHashOverlay is not null)
            {
                all = all.Concat(minHashOverlay.Find(scope, fingerprint.MinHash.Span));
            }

            foreach (var candidate in all
                .OrderByDescending(c => c.EstimatedJaccard)
                .ThenBy(c => c.Existing.Value, StringComparer.Ordinal))
            {
                if (!Blocked(fingerprint, candidate, chunk))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private async ValueTask<double?> EstimateAsync(
        string scope, ChunkId id, ChunkFingerprint fingerprint, MinHashLshIndex? overlay, CancellationToken ct)
    {
        var stored = await _minHashIndex!.EstimateJaccardAsync(scope, id, fingerprint.MinHash, ct).ConfigureAwait(false);
        return stored ?? overlay?.EstimateJaccard(scope, id, fingerprint.MinHash.Span);
    }

    // the false merge guard: close wording, but different numbers or negations
    private bool Blocked(ChunkFingerprint fingerprint, NearDuplicateMatch candidate, ChunkInput chunk)
    {
        if (!_options.GuardNumbersAndNegations || candidate.GuardSignature == fingerprint.GuardSignature)
        {
            return false;
        }

        _logger.LogDebug(
            "Chunk {Ordinal} is close to {Existing} but differs in numbers or negations, keeping both",
            chunk.Ordinal, Short(candidate.Existing.Value));
        return true;
    }

    private DedupDecision Repeat(
        ChunkInput chunk, ChunkId id, MatchStage stage, ChunkId of, int? distance, double? jaccard)
    {
        var outcome = _options.OnDuplicate == DuplicatePolicy.Flag ? DedupOutcome.Flagged : DedupOutcome.Duplicate;
        return new DedupDecision(chunk, id, outcome, stage, of, distance, jaccard);
    }

    // never log the text, only a short piece of the hash
    private void LogMatch(ChunkInput chunk, MatchStage stage, ChunkId existing, int? distance, double? jaccard) =>
        _logger.LogDebug(
            "Chunk {Ordinal} matched {Stage} with {Existing} (distance {Distance}, Jaccard {Jaccard})",
            chunk.Ordinal, stage, Short(existing.Value), distance, jaccard);

    private static string Short(string value) => value.Length <= 8 ? value : value[..8];
}
