using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rechunk;

/// <summary>A batch of chunks from one document. Holder names this piece of work, the queue message id is a good one.</summary>
public sealed record IngestRequest(
    string? Scope,
    DocumentId Document,
    IReadOnlyList<ChunkInput> Chunks,
    string? Holder = null,
    int Revision = 1);

public sealed record IngestResult(
    string Scope,
    string Holder,
    int Embedded,
    int Duplicates,
    int InFlight,
    IReadOnlyList<DeadLetter> DeadLetters,
    int LostClaims = 0)
{
    public int DeadLetterCount => DeadLetters.Count;

    /// <summary>True when every chunk is either stored or a duplicate of one that is.</summary>
    public bool IsComplete => InFlight == 0 && DeadLetters.Count == 0;
}

public sealed record ReplayResult(int Attempted, int Succeeded, int Failed, int Deferred, int SkippedNoText);

/// <summary>
/// Runs the whole ingest for a batch of chunks: dedup and claim, embed, write the vectors, commit the claims.
/// A chunk that fails is tried again up to Failures.MaxAttempts times. If it still fails it is written to the dead
/// letter store and its claim is released, so it is never left marked as seen without a vector.
///
/// Without a shared store on the pipeline it still retries and writes dead letters, but a failed chunk stays
/// remembered by the in memory indexes, so use a store when failures matter.
/// </summary>
public sealed class ChunkIngestor
{
    private readonly RechunkPipeline _pipeline;
    private readonly IChunkEmbedder _embedder;
    private readonly IVectorSink _sink;
    private readonly IDeadLetterStore _deadLetters;
    private readonly RechunkOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public ChunkIngestor(
        RechunkPipeline pipeline,
        IChunkEmbedder embedder,
        IVectorSink sink,
        IDeadLetterStore deadLetters,
        ILogger? logger = null,
        TimeProvider? time = null)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _deadLetters = deadLetters ?? throw new ArgumentNullException(nameof(deadLetters));
        _options = pipeline.Options;
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    public async ValueTask<IngestResult> IngestAsync(IngestRequest request, CancellationToken ct = default)
    {
        var result = await RunAsync(request, ct).ConfigureAwait(false);
        foreach (var deadLetter in result.DeadLetters)
        {
            await _deadLetters.AddAsync(deadLetter, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Tries the dead letters that match the query again. The text comes from the record when it was stored,
    /// otherwise from textSource (reload the chunk from your own source, it should return the current text).
    /// A record that works now is removed, one that fails again is updated, one that is still being handled by another
    /// worker is left alone.
    /// </summary>
    public async ValueTask<ReplayResult> ReplayAsync(
        DeadLetterQuery query,
        Func<DeadLetter, CancellationToken, ValueTask<string?>>? textSource = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var items = await _deadLetters.ListAsync(query, ct).ConfigureAwait(false);
        int succeeded = 0, failed = 0, deferred = 0, skipped = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var text = item.Text;
            if (text is null && textSource is not null)
            {
                text = await textSource(item, ct).ConfigureAwait(false);
            }

            if (text is null)
            {
                skipped++;
                continue;
            }

            var request = new IngestRequest(
                item.Scope,
                item.Document ?? default,
                new[] { new ChunkInput(item.Ordinal, text) },
                "replay-" + item.Id);
            var result = await RunAsync(request, ct).ConfigureAwait(false);

            if (result.InFlight > 0)
            {
                deferred++;
            }
            else if (result.DeadLetters.Count > 0)
            {
                var again = result.DeadLetters[0];
                await _deadLetters.AddAsync(
                    item with
                    {
                        Stage = again.Stage,
                        Reason = again.Reason,
                        Attempts = item.Attempts + again.Attempts,
                        FailedAt = again.FailedAt,
                    },
                    ct).ConfigureAwait(false);
                failed++;
            }
            else
            {
                await _deadLetters.RemoveAsync(item.Id, ct).ConfigureAwait(false);
                succeeded++;
            }
        }

        return new ReplayResult(items.Count, succeeded, failed, deferred, skipped);
    }

    private async ValueTask<IngestResult> RunAsync(IngestRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Chunks);

        var scope = request.Scope ?? _options.DefaultScope;
        var holder = request.Holder ?? "ingest-" + Guid.NewGuid().ToString("N");
        var max = _options.Failures.MaxAttempts;
        var deadLetters = new List<DeadLetter>();

        // Step 1: dedup and claim. A retry with the same holder gets its own claims back.
        DedupReport? report = null;
        Exception? failure = null;
        for (var attempt = 1; attempt <= max && report is null; attempt++)
        {
            try
            {
                await Pause(attempt, ct).ConfigureAwait(false);
                report = await _pipeline.DeduplicateAsync(scope, request.Chunks, new DedupClaimOptions(holder), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!IsCancelled(ex, ct))
            {
                failure = ex;
                _logger.LogWarning("Dedup failed for {Count} chunks in {Scope}, attempt {Attempt} of {Max}: {Error}",
                    request.Chunks.Count, scope, attempt, max, ex.GetType().Name);
            }
        }

        if (report is null)
        {
            foreach (var chunk in request.Chunks)
            {
                deadLetters.Add(MakeDeadLetter(
                    scope, _pipeline.IdFor(chunk.Text), request.Document, chunk, DeadLetterStage.Dedup, failure!, max));
            }

            return new IngestResult(scope, holder, 0, 0, 0, deadLetters);
        }

        var todo = report.Decisions
            .Where(d => d.Outcome is DedupOutcome.Unique or DedupOutcome.Flagged)
            .ToList();
        var committedIds = new HashSet<string>(StringComparer.Ordinal);
        var done = 0;
        var lost = 0;

        try
        {
            if (todo.Count > 0)
            {
                var written = await WriteAsync(request, scope, todo, deadLetters, ct).ConfigureAwait(false);
                foreach (var decision in written)
                {
                    var status = await CommitAsync(request, scope, decision, deadLetters, ct).ConfigureAwait(false);
                    if (status is not null)
                    {
                        committedIds.Add(decision.Id.Value);
                        done++;
                        if (status == CommitStatus.NotHeld)
                        {
                            lost++;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (IsCancelled(ex, ct))
        {
            await ReleaseAllAsync(report.Claims).ConfigureAwait(false);
            throw;
        }

        await SettleAliasesAsync(report, committedIds).ConfigureAwait(false);

        return new IngestResult(scope, holder, done, report.DuplicateCount, report.InFlightCount, deadLetters, lost);
    }

    // A chunk that is close to another chunk of the same batch waits for it: it is committed when that chunk is,
    // and given back when that chunk failed, so it is never left pointing at nothing.
    private async ValueTask SettleAliasesAsync(DedupReport report, HashSet<string> committedIds)
    {
        var store = _pipeline.Store;
        if (store is null)
        {
            return;
        }

        foreach (var claim in report.Claims.Where(c => c.AliasOf is not null))
        {
            if (committedIds.Contains(claim.AliasOf!.Value.Value))
            {
                try
                {
                    await store.CommitAsync(claim.ToCommit(), CancellationToken.None).ConfigureAwait(false);
                    continue;
                }
                catch (Exception ex)
                {
                    // nothing is lost: the claim is given back below and the next run looks at the chunk again
                    _logger.LogWarning("Could not commit the claim for chunk {Chunk}: {Error}", Short(claim.Id.Value), ex.GetType().Name);
                }
            }

            await ReleaseAllAsync(new[] { claim }).ConfigureAwait(false);
        }
    }

    // Embeds and writes the vectors. The whole batch is tried first, and if that fails every chunk is retried alone,
    // so one bad chunk does not hold back the rest. Returns the chunks whose vector is written.
    private async ValueTask<List<DedupDecision>> WriteAsync(
        IngestRequest request, string scope, List<DedupDecision> todo, List<DeadLetter> deadLetters, CancellationToken ct)
    {
        var max = _options.Failures.MaxAttempts;
        var stage = DeadLetterStage.Embed;

        try
        {
            var vectors = await _embedder.EmbedAsync(todo.Select(d => d.Input.Text).ToList(), ct).ConfigureAwait(false);
            if (vectors.Count != todo.Count)
            {
                throw new InvalidOperationException($"The embedder returned {vectors.Count} vectors for {todo.Count} chunks.");
            }

            stage = DeadLetterStage.VectorWrite;
            await _sink.UpsertAsync(
                todo.Select((d, i) => ToEmbedded(request, d, vectors[i])).ToList(), ct).ConfigureAwait(false);
            return todo;
        }
        catch (Exception ex) when (!IsCancelled(ex, ct))
        {
            _logger.LogWarning("Batch of {Count} chunks failed at {Stage}, trying them one by one: {Error}",
                todo.Count, stage, ex.GetType().Name);

            if (max == 1)
            {
                foreach (var d in todo)
                {
                    await GiveUpAsync(request, scope, d, stage, ex, 1, deadLetters).ConfigureAwait(false);
                }

                return new List<DedupDecision>();
            }
        }

        var written = new List<DedupDecision>();
        foreach (var d in todo)
        {
            Exception? last = null;
            var lastStage = stage;
            var ok = false;

            for (var attempt = 2; attempt <= max && !ok; attempt++)
            {
                try
                {
                    await Pause(attempt, ct).ConfigureAwait(false);
                    lastStage = DeadLetterStage.Embed;
                    var vectors = await _embedder.EmbedAsync(new[] { d.Input.Text }, ct).ConfigureAwait(false);
                    if (vectors.Count != 1)
                    {
                        throw new InvalidOperationException($"The embedder returned {vectors.Count} vectors for 1 chunk.");
                    }

                    lastStage = DeadLetterStage.VectorWrite;
                    await _sink.UpsertAsync(new[] { ToEmbedded(request, d, vectors[0]) }, ct).ConfigureAwait(false);
                    ok = true;
                }
                catch (Exception ex) when (!IsCancelled(ex, ct))
                {
                    last = ex;
                }
            }

            if (ok)
            {
                written.Add(d);
            }
            else
            {
                await GiveUpAsync(request, scope, d, lastStage, last!, max, deadLetters).ConfigureAwait(false);
            }
        }

        return written;
    }

    // Commits one claim. Null means it gave up (a dead letter was written), otherwise the status of the commit.
    private async ValueTask<CommitStatus?> CommitAsync(
        IngestRequest request, string scope, DedupDecision decision, List<DeadLetter> deadLetters, CancellationToken ct)
    {
        var store = _pipeline.Store;
        if (store is null || decision.Claim is null)
        {
            return CommitStatus.Committed;
        }

        var max = _options.Failures.MaxAttempts;
        Exception? last = null;
        for (var attempt = 1; attempt <= max; attempt++)
        {
            try
            {
                await Pause(attempt, ct).ConfigureAwait(false);
                return await store.CommitAsync(decision.Claim.ToCommit(), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!IsCancelled(ex, ct))
            {
                last = ex;
            }
        }

        // The vector is written already. Writing it again later is harmless because the id comes from the text.
        await GiveUpAsync(request, scope, decision, DeadLetterStage.Commit, last!, max, deadLetters).ConfigureAwait(false);
        return null;
    }

    private async ValueTask GiveUpAsync(
        IngestRequest request, string scope, DedupDecision decision, DeadLetterStage stage, Exception error, int attempts,
        List<DeadLetter> deadLetters)
    {
        _logger.LogError("Chunk {Chunk} in {Scope} is going to the dead letter store after {Attempts} attempts at {Stage}: {Error}",
            Short(decision.Id.Value), scope, attempts, stage, error.GetType().Name);

        deadLetters.Add(MakeDeadLetter(scope, decision.Id, request.Document, decision.Input, stage, error, attempts));
        await ReleaseAllAsync(decision.Claim is null ? Array.Empty<ChunkClaim>() : new[] { decision.Claim }).ConfigureAwait(false);
    }

    private async ValueTask ReleaseAllAsync(IEnumerable<ChunkClaim> claims)
    {
        var store = _pipeline.Store;
        if (store is null)
        {
            return;
        }

        foreach (var claim in claims)
        {
            try
            {
                // not cancellable on purpose, a cancelled run still has to give its claims back
                await store.ReleaseAsync(claim.ToRelease(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // if this fails the lease will run out by itself
                _logger.LogWarning("Could not release the claim for chunk {Chunk}: {Error}", Short(claim.Id.Value), ex.GetType().Name);
            }
        }
    }

    private DeadLetter MakeDeadLetter(
        string scope, ChunkId id, DocumentId document, ChunkInput chunk, DeadLetterStage stage, Exception error, int attempts) =>
        new(
            DeadLetterId(scope, id),
            scope,
            id,
            document,
            chunk.Ordinal,
            stage,
            Describe(error, chunk.Text),
            attempts,
            _time.GetUtcNow(),
            _options.Failures.StoreTextInDeadLetters ? chunk.Text : null);

    // The same chunk in the same scope always gets the same id, so a queue message that is delivered again and fails
    // again updates its record and does not add a second one.
    private static string DeadLetterId(string scope, ChunkId chunk)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scope + "\n" + chunk.Value));
        return Convert.ToHexString(bytes, 0, 16).ToLowerInvariant();
    }

    // The type of the error and a shortened message. If the message repeats the chunk text, that part is cut out.
    private string Describe(Exception error, string text)
    {
        var message = error.Message ?? string.Empty;
        if (text.Length > 0)
        {
            message = message.Replace(text, "[chunk text]", StringComparison.Ordinal);
        }

        message = message.ReplaceLineEndings(" ");
        var reason = $"{error.GetType().Name}: {message}";
        var limit = Math.Max(20, _options.Failures.MaxReasonLength);
        return reason.Length <= limit ? reason : reason[..limit];
    }

    private EmbeddedChunk ToEmbedded(IngestRequest request, DedupDecision decision, ReadOnlyMemory<float> vector) =>
        new(decision.Id, request.Document, request.Revision, decision.Input.Text, vector, _embedder.ModelId);

    private async ValueTask Pause(int attempt, CancellationToken ct)
    {
        if (attempt <= 1 || _options.Failures.RetryDelay <= TimeSpan.Zero)
        {
            return;
        }

        var wait = TimeSpan.FromTicks((long)(_options.Failures.RetryDelay.Ticks * Math.Pow(_options.Failures.RetryBackoff, attempt - 2)));
        await Task.Delay(wait, _time, ct).ConfigureAwait(false);
    }

    private static bool IsCancelled(Exception ex, CancellationToken ct) => ex is OperationCanceledException && ct.IsCancellationRequested;

    private static string Short(string value) => value.Length <= 8 ? value : value[..8];
}
