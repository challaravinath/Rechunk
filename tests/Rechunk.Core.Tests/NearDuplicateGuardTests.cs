namespace Rechunk.Core.Tests;

public class NearDuplicateGuardTests
{
    private static List<ChunkInput> Chunks(params string[] texts) =>
        texts.Select((t, i) => new ChunkInput(i, t)).ToList();

    private static RechunkPipelineBuilder Both() =>
        RechunkPipeline.CreateBuilder().UseSimHash(o => o.MaxHammingDistance = 6).UseMinHash();

    private const string IncludeA =
        ":::code language=\"csharp\" source=\"snippets/built-in-types/Program.cs\" ID=\"UnsignedTypes\":::";

    private const string IncludeB =
        ":::code language=\"csharp\" source=\"snippets/built-in-types/Program.cs\" ID=\"VarKeyword\":::";

    private static string Fenced(string word) =>
        "```text\n" + TestText.Paragraph(4, 40) + "\nvalue is " + word + "\n```";

    private static string Breadcrumb(string leaf) =>
        "[`<configuration>`](../configuration-element.md)\\\n" +
        "&nbsp;&nbsp;[`<system.serviceModel>`](system-servicemodel.md)\\\n" +
        "&nbsp;&nbsp;&nbsp;&nbsp;[`<bindings>`](bindings.md)\\\n" +
        "&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;`<" + leaf + ">`";

    [Fact]
    public async Task Short_chunks_skip_near_duplicate_stages_and_exact_repeats_still_match()
    {
        const string line = "An expression that uses the += operator, such as";
        var other = "An expression that uses the -= operator, such as";

        var guarded = await Both().Build().DeduplicateAsync("docs", Chunks(line, other, line));
        Assert.Equal(DedupOutcome.Unique, guarded.Decisions[0].Outcome);
        Assert.Equal(DedupOutcome.Unique, guarded.Decisions[1].Outcome);
        Assert.Equal(MatchStage.Exact, guarded.Decisions[2].Stage);

        var open = await Both().NearDuplicateMinTokens(1).SkipCodeLikeChunks(false).GuardIdentifierOnlyChanges(false)
            .Build().DeduplicateAsync("docs", Chunks(line, other));
        Assert.Equal(MatchStage.SimHash, open.Decisions[1].Stage);
    }

    [Fact]
    public async Task Code_includes_are_not_near_duplicates_unless_the_text_matches_exactly()
    {
        var skipped = await Both().NearDuplicateMinTokens(1).GuardIdentifierOnlyChanges(false)
            .Build().DeduplicateAsync("docs", Chunks(IncludeA, IncludeB, IncludeA));

        Assert.Equal(DedupOutcome.Unique, skipped.Decisions[0].Outcome);
        Assert.Equal(DedupOutcome.Unique, skipped.Decisions[1].Outcome);
        Assert.Equal(MatchStage.Exact, skipped.Decisions[2].Stage);

        var open = await Both().NearDuplicateMinTokens(1).SkipCodeLikeChunks(false).GuardIdentifierOnlyChanges(false)
            .Build().DeduplicateAsync("docs", Chunks(IncludeA, IncludeB));
        Assert.NotEqual(DedupOutcome.Unique, open.Decisions[1].Outcome);
    }

    [Fact]
    public async Task Fenced_code_that_differs_by_one_word_is_not_merged()
    {
        var skipped = await Both().NearDuplicateMinTokens(1).GuardIdentifierOnlyChanges(false)
            .Build().DeduplicateAsync("docs", Chunks(Fenced("false"), Fenced("true")));
        Assert.Equal(2, skipped.UniqueCount);

        var open = await Both().NearDuplicateMinTokens(1).SkipCodeLikeChunks(false).GuardIdentifierOnlyChanges(false)
            .Build().DeduplicateAsync("docs", Chunks(Fenced("false"), Fenced("true")));
        Assert.Equal(MatchStage.SimHash, open.Decisions[1].Stage);
    }

    [Fact]
    public async Task Config_breadcrumbs_are_not_near_duplicates()
    {
        var skipped = await Both().NearDuplicateMinTokens(1).GuardIdentifierOnlyChanges(false)
            .Build().DeduplicateAsync("docs", Chunks(Breadcrumb("security"), Breadcrumb("transport")));
        Assert.Equal(2, skipped.UniqueCount);

        var open = await Both().NearDuplicateMinTokens(1).SkipCodeLikeChunks(false).GuardIdentifierOnlyChanges(false)
            .Build().DeduplicateAsync("docs", Chunks(Breadcrumb("security"), Breadcrumb("transport")));
        Assert.NotEqual(DedupOutcome.Unique, open.Decisions[1].Outcome);
    }

    [Fact]
    public async Task A_sentence_with_one_inline_code_span_can_still_be_a_near_duplicate()
    {
        var a = TestText.Paragraph(8, 200) + " call `Add` once at the end of the step.";
        var b = TestText.WithWordChanged(8, 200, 40, "revised") + " call `Add` once at the end of the step.";

        var report = await Both().Build().DeduplicateAsync("docs", Chunks(a, b));

        Assert.NotEqual(DedupOutcome.Unique, report.Decisions[1].Outcome);
    }

    [Fact]
    public async Task Identifier_only_differences_are_not_merged()
    {
        var menu = TestText.Paragraph(3, 40) + " The notes cover the Menu control type in every host.";
        var item = TestText.Paragraph(3, 40) + " The notes cover the MenuItem control type in every host.";
        var row = TestText.Paragraph(5, 40) + " Read System.Data.DataRow before you change the stored values.";
        var table = TestText.Paragraph(5, 40) + " Read System.Data.DataTable before you change the stored values.";
        var indexes = TestText.Paragraph(9, 40) + " columns were added to the Indexes schema collection for the new types.";
        var columns = TestText.Paragraph(9, 40) + " columns were added to the Columns schema collection for the new types.";

        var guarded = await Both().Build().DeduplicateAsync("docs", Chunks(menu, item, row, table, indexes, columns));
        Assert.Equal(6, guarded.UniqueCount);

        var open = await Both().GuardIdentifierOnlyChanges(false)
            .Build().DeduplicateAsync("docs", Chunks(menu, item));
        Assert.NotEqual(DedupOutcome.Unique, open.Decisions[1].Outcome);
    }

    [Fact]
    public async Task A_wording_change_still_merges_when_an_identifier_is_shared()
    {
        var a = TestText.Paragraph(11, 40) + " The Menu control type stays in the notes for this release.";
        var b = TestText.WithWordChanged(11, 40, 12, "revised") + " The Menu control type stays in the notes for this release.";

        var report = await Both().Build().DeduplicateAsync("docs", Chunks(a, b));

        Assert.Equal(MatchStage.SimHash, report.Decisions[1].Stage);
    }

    [Fact]
    public async Task Different_capitalization_of_an_ordinary_word_still_merges_when_the_chunk_is_long_enough()
    {
        var a = "number " + TestText.Paragraph(13, 30);
        var b = "Number " + TestText.Paragraph(13, 30);

        var report = await Both().Build().DeduplicateAsync("docs", Chunks(a, b));

        Assert.NotEqual(DedupOutcome.Unique, report.Decisions[1].Outcome);
    }

    [Fact]
    public void Options_reject_a_token_minimum_below_one()
    {
        var options = new RechunkOptions { NearDuplicateMinTokens = 0 };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void Algorithm_version_names_the_near_duplicate_guards_that_are_on()
    {
        var on = new RechunkOptions();
        on.SimHash.Enabled = true;
        var off = new RechunkOptions();
        off.SimHash.Enabled = true;
        off.SkipCodeLikeChunks = false;
        off.GuardIdentifierOnlyChanges = false;
        off.NearDuplicateMinTokens = 12;

        var onVersion = new ChunkFingerprinter(on).AlgorithmVersion;
        var offVersion = new ChunkFingerprinter(off).AlgorithmVersion;

        Assert.Contains("near-t20", onVersion);
        Assert.Contains("codelike-v1", onVersion);
        Assert.Contains("idguard-v1", onVersion);
        Assert.Contains("near-t12", offVersion);
        Assert.DoesNotContain("codelike", offVersion);
        Assert.DoesNotContain("idguard", offVersion);
    }
}
