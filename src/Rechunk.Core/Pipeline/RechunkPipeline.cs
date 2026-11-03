using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rechunk;

/// <summary>
/// Runs chunks through the dedup stages. Right now only stage 1 (exact hash) is wired in.
/// Stages 2 and 3 (SimHash, MinHash with LSH) come after they are written.
/// </summary>
public sealed class RechunkPipeline
{
    private readonly RechunkOptions _options;
    private readonly IChunkFingerprinter _fingerprinter;
    private readonly IExactHashIndex _exactIndex;
    private readonly ILogger _logger;

    public RechunkPipeline(
        RechunkOptions options,
        IChunkFingerprinter fingerprinter,
        IExactHashIndex exactIndex,
        ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _fingerprinter = fingerprinter ?? throw new ArgumentNullException(nameof(fingerprinter));
        _exactIndex = exactIndex ?? throw new ArgumentNullException(nameof(exactIndex));
        _logger = logger ?? NullLogger.Instance;
        options.Validate();
    }

    public static RechunkPipelineBuilder CreateBuilder() => new();

    /// <summary>Looks at a batch of chunks and says which ones are new and which ones are repeats.</summary>
    public async ValueTask<DedupReport> DeduplicateAsync(
        string? scope, IReadOnlyList<ChunkInput> chunks, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        scope ??= _options.DefaultScope;

        // in a dry run nothing is written to the index, so repeats inside the batch are tracked here
        var seenInDryRun = new Dictionary<string, ChunkId>();
        var decisions = new List<DedupDecision>(chunks.Count);

        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();

            var fingerprint = _fingerprinter.Compute(chunk.Text);
            var id = ChunkId.FromExactHash(fingerprint.ExactHash.Span);

            ChunkId? existing;
            if (_options.DryRun)
            {
                existing = await _exactIndex.FindAsync(scope, fingerprint.ExactHash, ct).ConfigureAwait(false);
                if (existing is null && seenInDryRun.TryGetValue(id.Value, out var earlier))
                {
                    existing = earlier;
                }
            }
            else
            {
                var added = await _exactIndex.AddIfAbsentAsync(scope, fingerprint.ExactHash, id, ct).ConfigureAwait(false);
                existing = added.Added ? null : added.Owner;
            }

            if (existing is null)
            {
                seenInDryRun[id.Value] = id;
                decisions.Add(new DedupDecision(chunk, id, DedupOutcome.Unique, null, null));
                continue;
            }

            // TODO: stage 2 and 3 go here, for chunks that did not match exactly
            var outcome = _options.OnDuplicate == DuplicatePolicy.Flag ? DedupOutcome.Flagged : DedupOutcome.Duplicate;
            decisions.Add(new DedupDecision(chunk, id, outcome, MatchStage.Exact, existing));

            // never log the text, only a short piece of the hash
            _logger.LogDebug("Chunk {Ordinal} matched {Stage} with {Existing}", chunk.Ordinal, MatchStage.Exact, Short(existing.Value.Value));
        }

        return new DedupReport(scope, _options.DryRun, decisions);
    }

    private static string Short(string value) => value.Length <= 8 ? value : value[..8];
}
