namespace Rechunk.Core.Tests.Store;

public class ChunkIngestorTests
{
    private const string Scope = "tenant-a";

    private readonly ManualTimeProvider _clock = new();
    private readonly ScriptedEmbedder _embedder = new();
    private readonly RecordingSink _sink = new();
    private readonly InMemoryDeadLetterStore _deadLetters = new();
    private readonly InMemoryFingerprintStore _inner;
    private readonly FlakyStore _store;

    public ChunkIngestorTests()
    {
        var options = new RechunkOptions();
        options.SimHash.Enabled = true;
        options.MinHash.Enabled = true;
        _inner = new InMemoryFingerprintStore(options, _clock);
        _store = new FlakyStore(_inner);
    }

    private static string Text(int seed, int words = 120) => string.Join(' ', TestText.Words(seed, words));

    private ChunkIngestor Ingestor(int maxAttempts = 3, bool storeText = false, IFingerprintStore? store = null)
    {
        var pipeline = RechunkPipeline.CreateBuilder()
            .UseSimHash().UseMinHash()
            .UseFingerprintStore(store ?? _store)
            .Configure(o =>
            {
                o.Failures.MaxAttempts = maxAttempts;
                o.Failures.RetryDelay = TimeSpan.Zero;
                o.Failures.StoreTextInDeadLetters = storeText;
            })
            .Build();
        return new ChunkIngestor(pipeline, _embedder, _sink, _deadLetters, time: _clock);
    }

    private async Task ClaimAsOtherWorker(string text)
    {
        var pipeline = RechunkPipeline.CreateBuilder().UseSimHash().UseMinHash().UseFingerprintStore(_store).Build();
        await pipeline.DeduplicateAsync(Scope, new[] { new ChunkInput(0, text) }, new DedupClaimOptions("other", TimeSpan.FromMinutes(1)));
    }

    private static IngestRequest Request(string holder, params string[] texts) =>
        new(Scope, new DocumentId("doc-1"), texts.Select((t, i) => new ChunkInput(i, t)).ToList(), holder);

    [Fact]
    public async Task New_chunks_are_embedded_written_and_committed()
    {
        var result = await Ingestor().IngestAsync(Request("m1", Text(1), Text(2)));

        Assert.Equal(2, result.Embedded);
        Assert.True(result.IsComplete);
        Assert.Equal(2, _sink.StoredIds.Count);
        Assert.Equal(0, await _deadLetters.CountAsync(new DeadLetterQuery()));
    }

    [Fact]
    public async Task A_duplicate_from_a_later_message_is_skipped()
    {
        var ingestor = Ingestor();
        await ingestor.IngestAsync(Request("m1", Text(1)));

        var later = await ingestor.IngestAsync(Request("m2", Text(1)));

        Assert.Equal(0, later.Embedded);
        Assert.Equal(1, later.Duplicates);
        Assert.Single(_sink.StoredIds);
    }

    [Fact]
    public async Task A_chunk_that_always_fails_ends_up_in_the_dead_letter_store_after_the_set_number_of_tries()
    {
        _embedder.Hook = (_, _) => throw new InvalidOperationException("the model is down");

        var result = await Ingestor(maxAttempts: 4).IngestAsync(Request("m1", Text(1)));

        Assert.Equal(1, result.DeadLetterCount);
        Assert.False(result.IsComplete);
        Assert.Equal(4, _embedder.Calls);

        var letter = Assert.Single(await _deadLetters.ListAsync(new DeadLetterQuery()));
        Assert.Equal(Scope, letter.Scope);
        Assert.Equal(DeadLetterStage.Embed, letter.Stage);
        Assert.Equal(4, letter.Attempts);
        Assert.Equal(_clock.GetUtcNow(), letter.FailedAt);
        Assert.Equal(new DocumentId("doc-1"), letter.Document);
        Assert.StartsWith("InvalidOperationException: the model is down", letter.Reason);
    }

    [Fact]
    public async Task The_dead_letter_has_no_chunk_text_unless_asked_for()
    {
        _embedder.Hook = (_, _) => throw new InvalidOperationException("no");
        var text = Text(1);

        await Ingestor().IngestAsync(Request("m1", text));

        var letter = Assert.Single(await _deadLetters.ListAsync(new DeadLetterQuery()));
        Assert.Null(letter.Text);
        Assert.DoesNotContain(text[..30], letter.Reason);
    }

    [Fact]
    public async Task The_text_is_kept_when_StoreTextInDeadLetters_is_on()
    {
        _embedder.Hook = (_, _) => throw new InvalidOperationException("no");
        var text = Text(1);

        await Ingestor(storeText: true).IngestAsync(Request("m1", text));

        Assert.Equal(text, Assert.Single(await _deadLetters.ListAsync(new DeadLetterQuery())).Text);
    }

    [Fact]
    public async Task An_error_that_repeats_the_chunk_text_is_cleaned_before_it_is_saved()
    {
        var text = Text(1);
        _embedder.Hook = (texts, _) => throw new InvalidOperationException("bad input: " + texts[0]);

        await Ingestor().IngestAsync(Request("m1", text));

        var letter = Assert.Single(await _deadLetters.ListAsync(new DeadLetterQuery()));
        Assert.Contains("[chunk text]", letter.Reason);
        Assert.DoesNotContain(text, letter.Reason);
    }

    [Fact]
    public async Task A_long_error_message_is_cut_to_the_limit()
    {
        _embedder.Hook = (_, _) => throw new InvalidOperationException(new string('x', 5000));

        await Ingestor().IngestAsync(Request("m1", Text(1)));

        var letter = Assert.Single(await _deadLetters.ListAsync(new DeadLetterQuery()));
        Assert.Equal(300, letter.Reason.Length);
    }

    [Fact]
    public async Task A_failed_chunk_is_released_so_it_is_not_seen_as_a_duplicate()
    {
        _embedder.Hook = (_, _) => throw new InvalidOperationException("no");
        var ingestor = Ingestor();
        await ingestor.IngestAsync(Request("m1", Text(1)));

        _embedder.Hook = null;
        var retry = await ingestor.IngestAsync(Request("m2", Text(1)));

        Assert.Equal(1, retry.Embedded);
        Assert.Equal(0, retry.InFlight);
    }

    [Fact]
    public async Task A_failure_that_goes_away_on_its_own_is_retried_and_leaves_no_dead_letter()
    {
        _embedder.Hook = (_, call) =>
        {
            if (call == 1)
            {
                throw new TimeoutException("slow");
            }
        };

        var result = await Ingestor().IngestAsync(Request("m1", Text(1), Text(2)));

        Assert.True(result.IsComplete);
        Assert.Equal(2, result.Embedded);
        Assert.Equal(2, _sink.StoredIds.Count);
        Assert.Equal(0, await _deadLetters.CountAsync(new DeadLetterQuery()));
    }

    [Fact]
    public async Task One_bad_chunk_does_not_hold_back_the_others()
    {
        var bad = Text(2);
        _embedder.Hook = (texts, _) =>
        {
            if (texts.Contains(bad))
            {
                throw new InvalidOperationException("cannot embed this one");
            }
        };

        var result = await Ingestor().IngestAsync(Request("m1", Text(1), bad, Text(3)));

        Assert.Equal(2, result.Embedded);
        Assert.Equal(1, result.DeadLetterCount);
        Assert.Equal(1, result.DeadLetters[0].Ordinal);
        Assert.Equal(2, _sink.StoredIds.Count);
    }

    [Fact]
    public async Task A_failing_vector_write_is_reported_as_the_write_stage()
    {
        _sink.Hook = (_, _) => throw new TimeoutException("vector store timeout");

        var result = await Ingestor().IngestAsync(Request("m1", Text(1)));

        Assert.Equal(DeadLetterStage.VectorWrite, result.DeadLetters.Single().Stage);
    }

    [Fact]
    public async Task A_failing_commit_is_reported_as_the_commit_stage_and_the_claim_is_released()
    {
        _store.OnCommit = _ => new TimeoutException("store timeout");
        var ingestor = Ingestor();

        var result = await ingestor.IngestAsync(Request("m1", Text(1)));

        Assert.Equal(DeadLetterStage.Commit, result.DeadLetters.Single().Stage);
        _store.OnCommit = null;

        // the vector was written already, writing it again replaces it because the id comes from the text
        var retry = await ingestor.IngestAsync(Request("m2", Text(1)));
        Assert.Equal(1, retry.Embedded);
        Assert.Single(_sink.StoredIds);
    }

    [Fact]
    public async Task A_failing_claim_is_reported_as_the_dedup_stage_for_every_chunk()
    {
        _store.OnClaim = _ => new TimeoutException("store timeout");

        var result = await Ingestor().IngestAsync(Request("m1", Text(1), Text(2)));

        Assert.Equal(2, result.DeadLetterCount);
        Assert.All(result.DeadLetters, d => Assert.Equal(DeadLetterStage.Dedup, d.Stage));
        Assert.Equal(0, _embedder.Calls);
    }

    [Fact]
    public async Task A_chunk_that_another_worker_is_handling_is_reported_as_in_flight_and_not_embedded()
    {
        await ClaimAsOtherWorker(Text(1));

        var result = await Ingestor().IngestAsync(Request("m1", Text(1)));

        Assert.Equal(1, result.InFlight);
        Assert.False(result.IsComplete);
        Assert.Equal(0, result.Embedded);
        Assert.Equal(0, _embedder.Calls);
    }

    [Fact]
    public async Task A_retry_with_the_same_holder_after_a_crash_gets_its_claim_back_and_finishes()
    {
        // the first run claimed the chunk and then the worker died before it wrote anything
        var pipeline = RechunkPipeline.CreateBuilder().UseSimHash().UseMinHash().UseFingerprintStore(_store).Build();
        await pipeline.DeduplicateAsync(Scope, new[] { new ChunkInput(0, Text(1)) }, new DedupClaimOptions("m1"));

        var result = await Ingestor().IngestAsync(Request("m1", Text(1)));

        Assert.Equal(1, result.Embedded);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public async Task A_crashed_worker_is_taken_over_once_its_lease_has_run_out()
    {
        var pipeline = RechunkPipeline.CreateBuilder().UseSimHash().UseMinHash().UseFingerprintStore(_store).Build();
        await pipeline.DeduplicateAsync(Scope, new[] { new ChunkInput(0, Text(1)) }, new DedupClaimOptions("m1", TimeSpan.FromSeconds(30)));

        var early = await Ingestor().IngestAsync(Request("m2", Text(1)));
        Assert.Equal(1, early.InFlight);

        _clock.Advance(TimeSpan.FromSeconds(31));
        var late = await Ingestor().IngestAsync(Request("m2", Text(1)));
        Assert.Equal(1, late.Embedded);
    }

    [Fact]
    public async Task Cancelling_gives_the_claims_back()
    {
        using var cts = new CancellationTokenSource();
        _sink.Hook = (_, _) => cts.Cancel();
        _sink.Hook += (_, _) => throw new OperationCanceledException(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await Ingestor().IngestAsync(Request("m1", Text(1)), cts.Token));

        _sink.Hook = null;
        var retry = await Ingestor().IngestAsync(Request("m2", Text(1)));
        Assert.Equal(1, retry.Embedded);
        Assert.Equal(0, retry.InFlight);
    }

    [Fact]
    public async Task The_same_failing_message_delivered_twice_keeps_one_dead_letter()
    {
        _embedder.Hook = (_, _) => throw new InvalidOperationException("no");
        var ingestor = Ingestor();

        await ingestor.IngestAsync(Request("m1", Text(1)));
        await ingestor.IngestAsync(Request("m1", Text(1)));

        Assert.Equal(1, await _deadLetters.CountAsync(new DeadLetterQuery()));
    }

    [Fact]
    public async Task Replay_uses_the_saved_text_and_removes_the_letter_when_it_works()
    {
        _embedder.Hook = (_, _) => throw new InvalidOperationException("no");
        var ingestor = Ingestor(storeText: true);
        await ingestor.IngestAsync(Request("m1", Text(1)));

        _embedder.Hook = null;
        var replay = await ingestor.ReplayAsync(new DeadLetterQuery());

        Assert.Equal(1, replay.Attempted);
        Assert.Equal(1, replay.Succeeded);
        Assert.Equal(0, await _deadLetters.CountAsync(new DeadLetterQuery()));
        Assert.Single(_sink.StoredIds);
    }

    [Fact]
    public async Task Replay_without_saved_text_needs_a_text_source()
    {
        _embedder.Hook = (_, _) => throw new InvalidOperationException("no");
        var ingestor = Ingestor();
        var text = Text(1);
        await ingestor.IngestAsync(Request("m1", text));

        _embedder.Hook = null;
        var skipped = await ingestor.ReplayAsync(new DeadLetterQuery());
        Assert.Equal(1, skipped.SkippedNoText);
        Assert.Equal(1, await _deadLetters.CountAsync(new DeadLetterQuery()));

        var replay = await ingestor.ReplayAsync(new DeadLetterQuery(), (_, _) => ValueTask.FromResult<string?>(text));
        Assert.Equal(1, replay.Succeeded);
        Assert.Equal(0, await _deadLetters.CountAsync(new DeadLetterQuery()));
    }

    [Fact]
    public async Task A_replay_that_fails_again_updates_the_same_letter_and_adds_the_attempts()
    {
        _embedder.Hook = (_, _) => throw new InvalidOperationException("first");
        var ingestor = Ingestor(maxAttempts: 2, storeText: true);
        await ingestor.IngestAsync(Request("m1", Text(1)));
        var before = Assert.Single(await _deadLetters.ListAsync(new DeadLetterQuery()));

        _clock.Advance(TimeSpan.FromHours(1));
        _embedder.Hook = (_, _) => throw new DivideByZeroException("second");
        var replay = await ingestor.ReplayAsync(new DeadLetterQuery());

        Assert.Equal(1, replay.Failed);
        var after = Assert.Single(await _deadLetters.ListAsync(new DeadLetterQuery()));
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(4, after.Attempts);
        Assert.StartsWith("DivideByZeroException", after.Reason);
        Assert.True(after.FailedAt > before.FailedAt);
    }

    [Fact]
    public async Task Replay_leaves_a_letter_alone_while_another_worker_is_on_the_chunk()
    {
        _embedder.Hook = (_, _) => throw new InvalidOperationException("no");
        var ingestor = Ingestor(storeText: true);
        await ingestor.IngestAsync(Request("m1", Text(1)));
        _embedder.Hook = null;
        await ClaimAsOtherWorker(Text(1));

        var replay = await ingestor.ReplayAsync(new DeadLetterQuery());

        Assert.Equal(1, replay.Deferred);
        Assert.Equal(1, await _deadLetters.CountAsync(new DeadLetterQuery()));
    }

    [Fact]
    public async Task Near_duplicates_in_one_message_are_embedded_once_and_the_second_is_stored_as_a_duplicate()
    {
        var words = TestText.Words(33, 300);
        var edited = (string[])words.Clone();
        edited[100] = "zqzz";

        var result = await Ingestor().IngestAsync(Request("m1", string.Join(' ', words), string.Join(' ', edited)));

        Assert.Equal(1, result.Embedded);
        Assert.Equal(1, result.Duplicates);
        Assert.Single(_sink.StoredIds);
    }

    [Fact]
    public async Task A_near_duplicate_waiting_on_a_failed_chunk_is_released_with_it()
    {
        var words = TestText.Words(33, 300);
        var edited = (string[])words.Clone();
        edited[100] = "zqzz";
        _embedder.Hook = (_, _) => throw new InvalidOperationException("no");
        var ingestor = Ingestor();

        var failed = await ingestor.IngestAsync(Request("m1", string.Join(' ', words), string.Join(' ', edited)));
        Assert.Equal(1, failed.DeadLetterCount);

        _embedder.Hook = null;
        var retry = await ingestor.IngestAsync(Request("m2", string.Join(' ', edited)));
        Assert.Equal(1, retry.Embedded);
    }

    [Fact]
    public void Invalid_failure_settings_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RechunkOptions { Failures = { MaxAttempts = 0 } }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new RechunkOptions { Failures = { RetryDelay = TimeSpan.FromSeconds(-1) } }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new RechunkOptions { Failures = { RetryBackoff = 0.5 } }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new RechunkOptions { Store = { LeaseDuration = TimeSpan.Zero } }.Validate());
    }
}
