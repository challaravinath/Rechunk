using Microsoft.Extensions.DependencyInjection;

namespace Rechunk.Core.Tests;

public class SimHashStageTests
{
    private const int Words = 300;

    private static List<ChunkInput> Chunks(params string[] texts) =>
        texts.Select((t, i) => new ChunkInput(i, t)).ToList();

    private static RechunkPipelineBuilder Builder(int maxDistance = 6) =>
        RechunkPipeline.CreateBuilder().UseSimHash(o => o.MaxHammingDistance = maxDistance);

    private static string Original => TestText.Paragraph(21, Words);

    private static string OneWordChanged => TestText.WithWordChanged(21, Words, 100, "revised");

    [Fact]
    public async Task Near_duplicate_is_caught_by_the_second_stage()
    {
        var pipeline = Builder().Build();

        var report = await pipeline.DeduplicateAsync("docs", Chunks(Original, OneWordChanged));

        var second = report.Decisions[1];
        Assert.Equal(DedupOutcome.Duplicate, second.Outcome);
        Assert.Equal(MatchStage.SimHash, second.Stage);
        Assert.Equal(report.Decisions[0].Id, second.DuplicateOf);
        Assert.InRange(second.HammingDistance!.Value, 0, 6);
        Assert.NotEqual(report.Decisions[0].Id, second.Id);
    }

    [Fact]
    public async Task Exact_repeat_still_matches_at_the_first_stage()
    {
        var pipeline = Builder().Build();

        var report = await pipeline.DeduplicateAsync("docs", Chunks(Original, Original));

        Assert.Equal(MatchStage.Exact, report.Decisions[1].Stage);
        Assert.Null(report.Decisions[1].HammingDistance);
    }

    [Fact]
    public async Task Unrelated_text_stays_unique()
    {
        var pipeline = Builder().Build();

        var report = await pipeline.DeduplicateAsync("docs", Chunks(Original, TestText.Paragraph(500, Words)));

        Assert.Equal(2, report.UniqueCount);
    }

    [Fact]
    public async Task Near_duplicates_are_ignored_when_the_stage_is_off()
    {
        var pipeline = RechunkPipeline.CreateBuilder().Build();

        var report = await pipeline.DeduplicateAsync("docs", Chunks(Original, OneWordChanged));

        Assert.Equal(2, report.UniqueCount);
    }

    [Fact]
    public async Task Near_duplicate_seen_in_an_earlier_run_is_caught()
    {
        var pipeline = Builder().Build();

        await pipeline.DeduplicateAsync("docs", Chunks(Original));
        var second = await pipeline.DeduplicateAsync("docs", Chunks(OneWordChanged));

        Assert.Equal(MatchStage.SimHash, second.Decisions[0].Stage);
    }

    [Fact]
    public async Task Skipped_near_duplicate_text_is_an_exact_repeat_of_the_original_next_time()
    {
        var pipeline = Builder().Build();

        var first = await pipeline.DeduplicateAsync("docs", Chunks(Original, OneWordChanged));
        var again = await pipeline.DeduplicateAsync("docs", Chunks(OneWordChanged));

        Assert.Equal(MatchStage.Exact, again.Decisions[0].Stage);
        Assert.Equal(first.Decisions[0].Id, again.Decisions[0].DuplicateOf);
    }

    [Fact]
    public async Task Near_duplicate_in_another_scope_is_not_matched()
    {
        var pipeline = Builder().Build();

        await pipeline.DeduplicateAsync("tenant-a", Chunks(Original));
        var other = await pipeline.DeduplicateAsync("tenant-b", Chunks(OneWordChanged));

        Assert.Equal(1, other.UniqueCount);
    }

    [Fact]
    public async Task Short_chunks_are_only_matched_exactly()
    {
        var pipeline = Builder().Build();

        var report = await pipeline.DeduplicateAsync("docs", Chunks("Chapter 1", "Chapter 2", "Chapter 1"));

        Assert.Equal(2, report.UniqueCount);
        Assert.Equal(MatchStage.Exact, report.Decisions[2].Stage);
    }

    [Fact]
    public async Task Flag_policy_marks_near_duplicates_and_keeps_them_in_the_index()
    {
        var pipeline = Builder().OnDuplicate(DuplicatePolicy.Flag).Build();

        var report = await pipeline.DeduplicateAsync("docs", Chunks(Original, OneWordChanged));
        var again = await pipeline.DeduplicateAsync("docs", Chunks(OneWordChanged));

        Assert.Equal(DedupOutcome.Flagged, report.Decisions[1].Outcome);
        Assert.Equal(MatchStage.SimHash, report.Decisions[1].Stage);

        // the flagged chunk is stored as itself, so its exact repeat points at itself
        Assert.Equal(report.Decisions[1].Id, again.Decisions[0].DuplicateOf);
    }

    [Fact]
    public async Task Dry_run_reports_near_duplicates_but_stores_nothing()
    {
        var exact = new InMemoryExactHashIndex();
        var near = new SimHashBandIndex(6);
        var dry = Builder().UseExactHashIndex(exact).UseSimHashIndex(near).DryRun().Build();
        var real = Builder().UseExactHashIndex(exact).UseSimHashIndex(near).Build();

        var dryReport = await dry.DeduplicateAsync("docs", Chunks(Original, OneWordChanged));
        var realReport = await real.DeduplicateAsync("docs", Chunks(Original));

        Assert.Equal(MatchStage.SimHash, dryReport.Decisions[1].Stage);
        Assert.Equal(1, realReport.UniqueCount);
        Assert.Equal(1, near.Count("docs"));
    }

    [Fact]
    public async Task Changed_number_blocks_the_merge_when_the_guard_is_on()
    {
        var before = Original + " the notice period is 30 days";
        var after = Original + " the notice period is 60 days";

        var guarded = await Builder().Build().DeduplicateAsync("docs", Chunks(before, after));
        var unguarded = await Builder().GuardNumbersAndNegations(false).Build().DeduplicateAsync("docs", Chunks(before, after));

        Assert.Equal(2, guarded.UniqueCount);
        Assert.Equal(MatchStage.SimHash, unguarded.Decisions[1].Stage);
    }

    [Fact]
    public async Task Added_not_blocks_the_merge_when_the_guard_is_on()
    {
        var before = Original + " this change is allowed";
        var after = Original + " this change is not allowed";

        var guarded = await Builder().Build().DeduplicateAsync("docs", Chunks(before, after));
        var unguarded = await Builder().GuardNumbersAndNegations(false).Build().DeduplicateAsync("docs", Chunks(before, after));

        Assert.Equal(2, guarded.UniqueCount);
        Assert.Equal(MatchStage.SimHash, unguarded.Decisions[1].Stage);
    }

    [Fact]
    public async Task Same_numbers_do_not_stop_a_merge()
    {
        var a = Original + " refunds take 14 days";
        var b = TestText.WithWordChanged(21, Words, 100, "revised") + " refunds take 14 days";

        var report = await Builder().Build().DeduplicateAsync("docs", Chunks(a, b));

        Assert.Equal(MatchStage.SimHash, report.Decisions[1].Stage);
    }

    [Fact]
    public async Task Closest_match_is_chosen()
    {
        // Flag keeps every chunk in the index, so the third one can pick from both of the others
        var pipeline = Builder(10).OnDuplicate(DuplicatePolicy.Flag).Build();
        var other = TestText.WithWordChanged(21, Words, 20, "twist");
        var query = TestText.WithWordChanged(21, Words, 10, "tweak");

        var report = await pipeline.DeduplicateAsync("docs", Chunks(Original, other, query));

        var hasher = new SimHasher();
        ulong H(string t) => hasher.Compute(TextNormalizer.Normalize(t));
        var toOriginal = SimHasher.Distance(H(query), H(Original));
        var toOther = SimHasher.Distance(H(query), H(other));
        var expected = toOriginal <= toOther ? report.Decisions[0].Id : report.Decisions[1].Id;

        Assert.Equal(expected, report.Decisions[2].DuplicateOf);
        Assert.Equal(Math.Min(toOriginal, toOther), report.Decisions[2].HammingDistance);
    }

    [Fact]
    public async Task Dependency_injection_wires_the_second_stage()
    {
        var services = new ServiceCollection();
        services.AddRechunk(o => o.SimHash.Enabled = true);
        using var provider = services.BuildServiceProvider();

        var pipeline = provider.GetRequiredService<RechunkPipeline>();
        var report = await pipeline.DeduplicateAsync("docs", Chunks(Original, OneWordChanged));

        Assert.Equal(MatchStage.SimHash, report.Decisions[1].Stage);
        Assert.Same(provider.GetRequiredService<SimHashBandIndex>(), provider.GetRequiredService<SimHashBandIndex>());
    }

    [Fact]
    public async Task Parallel_batches_agree_on_one_chunk_to_keep()
    {
        var pipeline = Builder().Build();

        var reports = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            pipeline.DeduplicateAsync("docs", Chunks(Original)).AsTask()));

        // the exact hash index decides races, so exactly one of them gets to keep the text
        Assert.Equal(1, reports.Sum(r => r.UniqueCount));
    }
}
