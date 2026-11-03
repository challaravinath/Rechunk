using Microsoft.Extensions.DependencyInjection;

namespace Rechunk.Core.Tests;

/// <summary>
/// All three stages together. The edits below were picked on purpose (the numbers are in the comments):
/// one word changed in 300 words stays close for SimHash (2 bits) and for MinHash (about 0.98), while three changed
/// words in 150 move SimHash 6 bits away, which is too far for the default distance of 3, and MinHash still sees
/// about 0.92 overlap. SimHash is noisy on short edits, so these seeds are fixed and not left to chance.
/// </summary>
public class ThreeStageTests
{
    private static List<ChunkInput> Chunks(params string[] texts) =>
        texts.Select((t, i) => new ChunkInput(i, t)).ToList();

    private static string[] Base(int seed, int words) => TestText.Words(seed, words);

    private static string Edited(string[] words, params int[] positions)
    {
        var copy = (string[])words.Clone();
        foreach (var p in positions)
        {
            // letters only, a digit in the new word would trip the false merge guard
            copy[p] = "zq" + new string(p.ToString().Select(c => (char)('a' + c - '0')).ToArray());
        }

        return string.Join(' ', copy);
    }

    private static string Text(string[] words) => string.Join(' ', words);

    private static RechunkPipelineBuilder All() =>
        RechunkPipeline.CreateBuilder().UseSimHash().UseMinHash();

    // 300 words, one changed: SimHash distance 2, MinHash estimate about 0.98
    private static readonly string[] Long = Base(33, 300);

    // 150 words, three changed: SimHash distance 6 (too far), MinHash estimate about 0.92
    private static readonly string[] Medium = Base(34, 150);

    private static readonly int[] ThreeEdits = { 20, 70, 120 };

    [Fact]
    public async Task Exact_repeat_is_caught_by_the_first_stage_and_never_reaches_the_others()
    {
        var report = await All().Build().DeduplicateAsync(
            "docs", Chunks(Text(Long), "  " + Text(Long).Replace(" ", "  ") + "\n"));

        var second = report.Decisions[1];
        Assert.Equal(MatchStage.Exact, second.Stage);
        Assert.Null(second.HammingDistance);
        Assert.Null(second.EstimatedJaccard);
    }

    [Fact]
    public async Task SimHash_match_is_confirmed_by_MinHash_and_records_both_numbers()
    {
        var report = await All().Build().DeduplicateAsync("docs", Chunks(Text(Long), Edited(Long, 100)));

        var second = report.Decisions[1];
        Assert.Equal(DedupOutcome.Duplicate, second.Outcome);
        Assert.Equal(MatchStage.SimHash, second.Stage);
        Assert.Equal(report.Decisions[0].Id, second.DuplicateOf);
        Assert.InRange(second.HammingDistance!.Value, 0, 6);
        Assert.InRange(second.EstimatedJaccard!.Value, 0.85, 1.0);
    }

    [Fact]
    public async Task SimHash_alone_does_not_catch_the_looser_edit()
    {
        var report = await RechunkPipeline.CreateBuilder().UseSimHash().Build()
            .DeduplicateAsync("docs", Chunks(Text(Medium), Edited(Medium, ThreeEdits)));

        Assert.Equal(2, report.UniqueCount);
    }

    [Fact]
    public async Task MinHash_catches_the_looser_edit_that_SimHash_missed()
    {
        var report = await All().Build().DeduplicateAsync("docs", Chunks(Text(Medium), Edited(Medium, ThreeEdits)));

        var second = report.Decisions[1];
        Assert.Equal(DedupOutcome.Duplicate, second.Outcome);
        Assert.Equal(MatchStage.MinHash, second.Stage);
        Assert.Equal(report.Decisions[0].Id, second.DuplicateOf);
        Assert.Null(second.HammingDistance);
        Assert.InRange(second.EstimatedJaccard!.Value, 0.85, 1.0);
    }

    [Fact]
    public async Task MinHash_works_without_SimHash()
    {
        var report = await RechunkPipeline.CreateBuilder().UseMinHash().Build()
            .DeduplicateAsync("docs", Chunks(Text(Medium), Edited(Medium, ThreeEdits), Text(Base(99, 150))));

        Assert.Equal(MatchStage.MinHash, report.Decisions[1].Stage);
        Assert.Equal(DedupOutcome.Unique, report.Decisions[2].Outcome);
    }

    [Fact]
    public async Task An_edit_that_is_too_big_for_the_threshold_stays_unique()
    {
        // 30 changed words in 150: Jaccard about 0.4
        var many = Enumerable.Range(0, 30).Select(i => i * 5).ToArray();

        var report = await All().Build().DeduplicateAsync("docs", Chunks(Text(Medium), Edited(Medium, many)));

        Assert.Equal(2, report.UniqueCount);
    }

    [Fact]
    public async Task Unrelated_chunks_stay_unique_in_all_three_stages()
    {
        var report = await All().Build().DeduplicateAsync(
            "docs", Chunks(Text(Base(1, 150)), Text(Base(2, 150)), Text(Base(3, 150))));

        Assert.Equal(3, report.UniqueCount);
    }

    [Fact]
    public async Task With_confirm_on_a_SimHash_match_that_MinHash_rejects_is_not_merged()
    {
        // one changed word in 300 gives about 0.98, which is under a 0.995 threshold
        var pipeline = RechunkPipeline.CreateBuilder()
            .UseSimHash()
            .UseMinHash(o => o.Threshold = 0.995)
            .Build();

        var report = await pipeline.DeduplicateAsync("docs", Chunks(Text(Long), Edited(Long, 100)));

        Assert.Equal(2, report.UniqueCount);
    }

    [Fact]
    public async Task With_confirm_off_SimHash_is_trusted_and_MinHash_only_reports_its_number()
    {
        var pipeline = RechunkPipeline.CreateBuilder()
            .UseSimHash(o => o.ConfirmWithMinHash = false)
            .UseMinHash(o => o.Threshold = 0.995)
            .Build();

        var report = await pipeline.DeduplicateAsync("docs", Chunks(Text(Long), Edited(Long, 100)));

        var second = report.Decisions[1];
        Assert.Equal(MatchStage.SimHash, second.Stage);
        Assert.InRange(second.HammingDistance!.Value, 0, 6);
        Assert.InRange(second.EstimatedJaccard!.Value, 0.9, 0.995);
    }

    [Fact]
    public async Task Changed_numbers_are_not_merged_by_any_stage()
    {
        var words = Base(41, 150);
        words[70] = "30";
        var thirty = Text(words);
        words[70] = "60";
        var sixty = Text(words);

        var report = await All().Build().DeduplicateAsync("docs", Chunks(thirty, sixty));

        Assert.Equal(2, report.UniqueCount);
    }

    [Fact]
    public async Task A_negation_is_not_merged_by_any_stage()
    {
        var words = Base(42, 150).ToList();
        var allowed = Text(words.ToArray());
        words.Insert(70, "not");
        var notAllowed = Text(words.ToArray());

        var report = await All().Build().DeduplicateAsync("docs", Chunks(allowed, notAllowed));

        Assert.Equal(2, report.UniqueCount);
    }

    [Fact]
    public async Task Without_the_guard_the_same_number_change_is_merged()
    {
        var words = Base(41, 150);
        words[70] = "30";
        var thirty = Text(words);
        words[70] = "60";
        var sixty = Text(words);

        var pipeline = All().GuardNumbersAndNegations(false).Build();
        var report = await pipeline.DeduplicateAsync("docs", Chunks(thirty, sixty));

        Assert.Equal(DedupOutcome.Duplicate, report.Decisions[1].Outcome);
    }

    [Fact]
    public async Task A_skipped_near_duplicate_points_at_the_kept_chunk_when_it_comes_again()
    {
        var pipeline = All().Build();
        var variant = Edited(Medium, ThreeEdits);

        var report = await pipeline.DeduplicateAsync("docs", Chunks(Text(Medium), variant, variant));

        Assert.Equal(MatchStage.MinHash, report.Decisions[1].Stage);
        Assert.Equal(MatchStage.Exact, report.Decisions[2].Stage);
        Assert.Equal(report.Decisions[0].Id, report.Decisions[2].DuplicateOf);
    }

    [Fact]
    public async Task Batches_remember_each_other_and_scopes_do_not()
    {
        var pipeline = All().Build();
        await pipeline.DeduplicateAsync("tenant-a", Chunks(Text(Medium)));

        var same = await pipeline.DeduplicateAsync("tenant-a", Chunks(Edited(Medium, ThreeEdits)));
        var other = await pipeline.DeduplicateAsync("tenant-b", Chunks(Edited(Medium, ThreeEdits)));

        Assert.Equal(MatchStage.MinHash, same.Decisions[0].Stage);
        Assert.Equal(DedupOutcome.Unique, other.Decisions[0].Outcome);
    }

    [Fact]
    public async Task Short_chunks_are_only_matched_exactly()
    {
        var report = await All().Build().DeduplicateAsync(
            "docs", Chunks("Notice period is thirty days", "Notice period is sixty days", "Notice period is thirty days"));

        Assert.Equal(DedupOutcome.Unique, report.Decisions[1].Outcome);
        Assert.Equal(MatchStage.Exact, report.Decisions[2].Stage);
    }

    [Fact]
    public async Task Dry_run_reports_every_stage_but_leaves_the_indexes_empty()
    {
        var minHashIndex = new MinHashLshIndex(new MinHashOptions());
        var simHashIndex = new SimHashBandIndex(3);
        var pipeline = All().DryRun().UseMinHashIndex(minHashIndex).UseSimHashIndex(simHashIndex).Build();

        var report = await pipeline.DeduplicateAsync(
            "docs", Chunks(Text(Medium), Edited(Medium, ThreeEdits), Text(Long), Edited(Long, 100), Text(Long)));

        Assert.True(report.DryRun);
        Assert.Equal(
            new[] { DedupOutcome.Unique, DedupOutcome.Duplicate, DedupOutcome.Unique, DedupOutcome.Duplicate, DedupOutcome.Duplicate },
            report.Decisions.Select(d => d.Outcome));
        Assert.Equal(MatchStage.MinHash, report.Decisions[1].Stage);
        Assert.Equal(MatchStage.SimHash, report.Decisions[3].Stage);
        Assert.Equal(MatchStage.Exact, report.Decisions[4].Stage);
        Assert.Equal(0, minHashIndex.Count("docs"));

        var again = await pipeline.DeduplicateAsync("docs", Chunks(Text(Medium)));
        Assert.Equal(DedupOutcome.Unique, again.Decisions[0].Outcome);
    }

    [Fact]
    public async Task A_real_run_stores_only_the_chunks_that_are_kept()
    {
        var minHashIndex = new MinHashLshIndex(new MinHashOptions());
        var pipeline = All().UseMinHashIndex(minHashIndex).Build();

        await pipeline.DeduplicateAsync(
            "docs", Chunks(Text(Medium), Edited(Medium, ThreeEdits), Text(Base(77, 150))));

        Assert.Equal(2, minHashIndex.Count("docs"));
    }

    [Fact]
    public async Task Flag_policy_marks_near_duplicates_and_keeps_them_as_chunks_of_their_own()
    {
        var pipeline = All().OnDuplicate(DuplicatePolicy.Flag).Build();
        var variant = Edited(Medium, ThreeEdits);

        var report = await pipeline.DeduplicateAsync("docs", Chunks(Text(Medium), variant, variant));

        Assert.Equal(DedupOutcome.Flagged, report.Decisions[1].Outcome);
        Assert.Equal(MatchStage.MinHash, report.Decisions[1].Stage);
        Assert.Equal(DedupOutcome.Flagged, report.Decisions[2].Outcome);
        Assert.Equal(MatchStage.Exact, report.Decisions[2].Stage);
        Assert.Equal(report.Decisions[1].Id, report.Decisions[2].DuplicateOf);
    }

    [Fact]
    public async Task Dependency_injection_wires_all_three_stages()
    {
        var services = new ServiceCollection();
        services.AddRechunk(o =>
        {
            o.SimHash.Enabled = true;
            o.MinHash.Enabled = true;
        });
        using var provider = services.BuildServiceProvider();

        var pipeline = provider.GetRequiredService<RechunkPipeline>();
        var report = await pipeline.DeduplicateAsync(
            "docs", Chunks(Text(Medium), Edited(Medium, ThreeEdits), Text(Long), Edited(Long, 100), Text(Long)));

        Assert.Equal(
            new MatchStage?[] { null, MatchStage.MinHash, null, MatchStage.SimHash, MatchStage.Exact },
            report.Decisions.Select(d => d.Stage));
        Assert.Equal(2, provider.GetRequiredService<MinHashLshIndex>().Count("docs"));
    }

    [Fact]
    public async Task Parallel_batches_agree_on_one_chunk_to_keep()
    {
        var pipeline = All().Build();
        var variants = Enumerable.Range(0, 8).Select(_ => Text(Medium)).ToArray();

        var reports = await Task.WhenAll(variants.Select(v =>
            Task.Run(async () => await pipeline.DeduplicateAsync("docs", Chunks(v)))));

        Assert.Equal(1, reports.Count(r => r.Decisions[0].Outcome == DedupOutcome.Unique));
    }

    [Fact]
    public void The_algorithm_version_names_the_MinHash_settings()
    {
        var options = new RechunkOptions();
        options.MinHash.Enabled = true;
        options.MinHash.ShingleSize = 4;
        options.MinHash.Permutations = 64;
        options.MinHash.MinWords = 10;

        var version = new ChunkFingerprinter(options).AlgorithmVersion;

        Assert.Contains("minhash-v1-s4-p64-m10", version);
        Assert.Contains("guard-v1", version);
        Assert.DoesNotContain("simhash", version);
    }

    [Fact]
    public void Options_reject_bad_MinHash_values()
    {
        var shingle = new RechunkOptions();
        shingle.MinHash.ShingleSize = 0;

        var minWords = new RechunkOptions();
        minWords.MinHash.MinWords = 0;

        var permutations = new RechunkOptions();
        permutations.MinHash.Permutations = 0;

        Assert.ThrowsAny<ArgumentException>(shingle.Validate);
        Assert.ThrowsAny<ArgumentException>(minWords.Validate);
        Assert.ThrowsAny<ArgumentException>(permutations.Validate);
    }

    [Fact]
    public void Options_accept_bands_and_rows_that_fit_and_reject_one_without_the_other()
    {
        var fits = new RechunkOptions();
        fits.MinHash.Bands = 10;
        fits.MinHash.Rows = 10;
        fits.Validate();

        var alone = new RechunkOptions();
        alone.MinHash.Bands = 10;

        Assert.Throws<ArgumentException>(alone.Validate);
    }
}
