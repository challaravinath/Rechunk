using Microsoft.Extensions.DependencyInjection;

namespace Rechunk.Core.Tests;

public class ExactStageTests
{
    private static List<ChunkInput> Chunks(params string[] texts) =>
        texts.Select((t, i) => new ChunkInput(i, t)).ToList();

    [Fact]
    public async Task Repeated_chunk_in_one_batch_is_marked_duplicate()
    {
        var pipeline = RechunkPipeline.CreateBuilder().Build();

        var report = await pipeline.DeduplicateAsync("docs", Chunks("Confidential, Acme Corp", "Leave policy", "Confidential,  Acme Corp"));

        Assert.Equal(2, report.UniqueCount);
        Assert.Equal(1, report.DuplicateCount);
        var dup = report.Decisions[2];
        Assert.Equal(DedupOutcome.Duplicate, dup.Outcome);
        Assert.Equal(MatchStage.Exact, dup.Stage);
        Assert.Equal(report.Decisions[0].Id, dup.DuplicateOf);
    }

    [Fact]
    public async Task Second_run_of_the_same_chunks_finds_everything_already_seen()
    {
        var pipeline = RechunkPipeline.CreateBuilder().Build();
        var chunks = Chunks("one", "two", "three");

        var first = await pipeline.DeduplicateAsync("docs", chunks);
        var second = await pipeline.DeduplicateAsync("docs", chunks);

        Assert.Equal(3, first.UniqueCount);
        Assert.Equal(0, second.UniqueCount);
        Assert.Equal(3, second.DuplicateCount);
    }

    [Fact]
    public async Task Same_text_in_another_scope_is_not_a_duplicate()
    {
        var pipeline = RechunkPipeline.CreateBuilder().Build();

        await pipeline.DeduplicateAsync("tenant-a", Chunks("shared text"));
        var other = await pipeline.DeduplicateAsync("tenant-b", Chunks("shared text"));

        Assert.Equal(1, other.UniqueCount);
    }

    [Fact]
    public async Task Dry_run_does_not_remember_anything()
    {
        var index = new InMemoryExactHashIndex();
        var dry = RechunkPipeline.CreateBuilder().UseExactHashIndex(index).DryRun().Build();
        var real = RechunkPipeline.CreateBuilder().UseExactHashIndex(index).Build();

        var dryReport = await dry.DeduplicateAsync("docs", Chunks("a", "a"));
        var realReport = await real.DeduplicateAsync("docs", Chunks("a"));

        // the repeat inside the batch is still reported in a dry run
        Assert.Equal(1, dryReport.DuplicateCount);
        Assert.True(dryReport.DryRun);
        // but nothing was stored, so the real run sees it as new
        Assert.Equal(1, realReport.UniqueCount);
    }

    [Fact]
    public async Task Flag_policy_reports_the_duplicate_but_does_not_call_it_a_skip()
    {
        var pipeline = RechunkPipeline.CreateBuilder().OnDuplicate(DuplicatePolicy.Flag).Build();

        var report = await pipeline.DeduplicateAsync("docs", Chunks("x", "x"));

        Assert.Equal(1, report.FlaggedCount);
        Assert.Equal(0, report.DuplicateCount);
    }

    [Fact]
    public async Task Near_miss_text_is_not_treated_as_exact_duplicate()
    {
        var pipeline = RechunkPipeline.CreateBuilder().Build();

        var report = await pipeline.DeduplicateAsync("docs", Chunks("Notice period is 30 days", "Notice period is 60 days"));

        Assert.Equal(2, report.UniqueCount);
    }

    [Fact]
    public async Task Cancelled_token_stops_the_run()
    {
        var pipeline = RechunkPipeline.CreateBuilder().Build();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await pipeline.DeduplicateAsync("docs", Chunks("a"), cts.Token));
    }

    [Fact]
    public async Task Parallel_batches_agree_on_a_single_owner_for_the_same_text()
    {
        var pipeline = RechunkPipeline.CreateBuilder().Build();

        var reports = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(async () => await pipeline.DeduplicateAsync("docs", Chunks("same text")))));

        Assert.Equal(1, reports.Sum(r => r.UniqueCount));
        Assert.Equal(31, reports.Sum(r => r.DuplicateCount));
    }

    [Fact]
    public async Task AddRechunk_registers_a_working_pipeline()
    {
        var services = new ServiceCollection();
        services.AddRechunk(o => o.DefaultScope = "from-di");
        using var provider = services.BuildServiceProvider();

        var pipeline = provider.GetRequiredService<RechunkPipeline>();
        var report = await pipeline.DeduplicateAsync(null, Chunks("hello"));

        Assert.Equal("from-di", report.Scope);
        Assert.Equal(1, report.UniqueCount);
    }

    [Fact]
    public void Options_reject_bands_and_rows_that_do_not_fit_the_permutations()
    {
        var options = new RechunkOptions();
        options.MinHash.Permutations = 128;
        options.MinHash.Bands = 20;
        options.MinHash.Rows = 10;

        Assert.Throws<ArgumentException>(() => options.Validate());
    }
}
