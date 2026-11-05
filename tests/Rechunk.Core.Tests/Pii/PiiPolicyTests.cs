using Rechunk.Core.Tests.Store;
using Xunit;

namespace Rechunk.Core.Tests.Pii;

public class PiiPolicyTests
{
    private const string Scope = "tenant-a";
    private const string Card = "4111 1111 1111 1111";
    private const string Email = "jane.doe@example.org";

    private readonly RecordingSink _sink = new();
    private readonly ScriptedEmbedder _embedder = new();
    private readonly InMemoryDeadLetterStore _deadLetters = new();

    private static string Plain(int seed) => string.Join(' ', TestText.Words(seed, 60));

    private static RechunkPipeline Pipeline(Action<PiiOptions>? pii = null, Action<RechunkOptions>? other = null) =>
        RechunkPipeline.CreateBuilder()
            .UseSimHash().UseMinHash()
            .Configure(o =>
            {
                o.Pii.Enabled = true;
                pii?.Invoke(o.Pii);
                other?.Invoke(o);
            })
            .Build();

    private static ChunkInput Chunk(string text, int ordinal = 0) => new(ordinal, text);

    [Fact]
    public async Task Scanning_is_off_unless_it_is_switched_on()
    {
        var pipeline = RechunkPipeline.CreateBuilder().Build();
        var report = await pipeline.DeduplicateAsync(Scope, new[] { Chunk(Plain(1) + " card " + Card) });

        var decision = Assert.Single(report.Decisions);
        Assert.Empty(decision.Findings);
        Assert.Contains(Card, decision.Input.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_default_policy_flags_the_finding_and_leaves_the_text_alone()
    {
        var pipeline = Pipeline();
        var text = Plain(1) + " card " + Card + " end";

        var report = await pipeline.DeduplicateAsync(Scope, new[] { Chunk(text) });

        var decision = Assert.Single(report.Decisions);
        Assert.Equal(DedupOutcome.Unique, decision.Outcome);
        Assert.Equal(text, decision.Input.Text);
        var hit = Assert.Single(decision.Findings);
        Assert.Equal(PiiType.Card, hit.Type);
        Assert.Equal(PiiPolicy.Flag, hit.Applied);
        Assert.Equal(Card, text.Substring(hit.Start, hit.Length));
    }

    [Fact]
    public async Task Mask_replaces_the_value_before_the_text_is_fingerprinted()
    {
        var pipeline = Pipeline(p => p.Set(PiiType.Card, PiiPolicy.Mask));
        var text = Plain(2) + " card " + Card + " end";

        var report = await pipeline.DeduplicateAsync(Scope, new[] { Chunk(text) });

        var decision = Assert.Single(report.Decisions);
        Assert.DoesNotContain("4111", decision.Input.Text, StringComparison.Ordinal);
        Assert.Contains("[CARD]", decision.Input.Text, StringComparison.Ordinal);
        Assert.Equal(PiiPolicy.Mask, Assert.Single(decision.Findings).Applied);

        // the id is the id of the masked text, not of the original
        var masked = Plain(2) + " card [CARD] end";
        var plainPipeline = RechunkPipeline.CreateBuilder().Build();
        Assert.Equal(plainPipeline.IdFor(Chunk(masked)), decision.Id);
        Assert.NotEqual(plainPipeline.IdFor(Chunk(text)), decision.Id);
    }

    [Fact]
    public async Task Two_texts_that_differ_only_in_the_masked_value_are_one_chunk()
    {
        var pipeline = Pipeline(p => p.Set(PiiType.Card, PiiPolicy.Mask));
        var first = Plain(3) + " card 4111 1111 1111 1111 end";
        var second = Plain(3) + " card 5555 5555 5555 4444 end";

        var a = await pipeline.DeduplicateAsync(Scope, new[] { Chunk(first) });
        var b = await pipeline.DeduplicateAsync(Scope, new[] { Chunk(second) });

        Assert.Equal(DedupOutcome.Unique, Assert.Single(a.Decisions).Outcome);
        var dup = Assert.Single(b.Decisions);
        Assert.Equal(DedupOutcome.Duplicate, dup.Outcome);
        Assert.Equal(MatchStage.Exact, dup.Stage);
    }

    [Fact]
    public async Task Block_stops_the_chunk_and_remembers_nothing_about_it()
    {
        var pipeline = Pipeline(p => p.Set(PiiType.Card, PiiPolicy.Block));
        var text = Plain(4) + " card " + Card + " end";

        var report = await pipeline.DeduplicateAsync(Scope, new[] { Chunk(text), Chunk(Plain(5), 1) });

        Assert.Equal(1, report.BlockedCount);
        Assert.Equal(1, report.UniqueCount);
        var blocked = report.Decisions.Single(d => d.Outcome == DedupOutcome.Blocked);
        Assert.Equal(PiiType.Card, Assert.Single(blocked.Findings).Type);
        Assert.Null(blocked.Claim);

        // not stored: the text is not found by exact or near match, in this scope or any other
        var found = await pipeline.FindStoredAsync(Scope, Chunk(text), includeNear: true);
        Assert.Empty(found);
    }

    [Fact]
    public async Task A_blocked_chunk_is_not_masked_into_a_second_chance()
    {
        var pipeline = Pipeline(p =>
        {
            p.Set(PiiType.Card, PiiPolicy.Block);
            p.Set(PiiType.Email, PiiPolicy.Mask);
        });
        var text = Plain(6) + " " + Email + " card " + Card;

        var report = await pipeline.DeduplicateAsync(Scope, new[] { Chunk(text) });

        var decision = Assert.Single(report.Decisions);
        Assert.Equal(DedupOutcome.Blocked, decision.Outcome);
        Assert.Equal(2, decision.Findings.Count);
        Assert.DoesNotContain("4111", decision.Input.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("example.org", decision.Input.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Policies_are_set_per_type_and_the_default_covers_the_rest()
    {
        var pipeline = Pipeline(p =>
        {
            p.DefaultPolicy = PiiPolicy.Mask;
            p.Set(PiiType.Email, PiiPolicy.Flag);
        });
        var text = Plain(7) + " mail " + Email + " card " + Card;

        var report = await pipeline.DeduplicateAsync(Scope, new[] { Chunk(text) });

        var decision = Assert.Single(report.Decisions);
        Assert.Contains(Email, decision.Input.Text, StringComparison.Ordinal);
        Assert.Contains("[CARD]", decision.Input.Text, StringComparison.Ordinal);
        Assert.Equal(PiiPolicy.Flag, decision.Findings.Single(h => h.Type == PiiType.Email).Applied);
        Assert.Equal(PiiPolicy.Mask, decision.Findings.Single(h => h.Type == PiiType.Card).Applied);
    }

    [Fact]
    public async Task An_ignored_type_is_not_reported()
    {
        var pipeline = Pipeline(p => p.Ignore(PiiType.Email));
        var text = Plain(8) + " mail " + Email;

        var report = await pipeline.DeduplicateAsync(Scope, new[] { Chunk(text) });

        Assert.Empty(Assert.Single(report.Decisions).Findings);
    }

    [Fact]
    public async Task A_custom_placeholder_is_used()
    {
        var pipeline = Pipeline(p =>
        {
            p.Set(PiiType.Email, PiiPolicy.Mask);
            p.Placeholder = t => "<" + t.Name + ">";
        });

        var report = await pipeline.DeduplicateAsync(Scope, new[] { Chunk(Plain(9) + " " + Email) });

        Assert.EndsWith("<email>", Assert.Single(report.Decisions).Input.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Findings_below_the_minimum_confidence_are_dropped()
    {
        var detector = new FixedDetector(new PiiFinding(PiiType.Email, 0, 5, 0.4), new PiiFinding(PiiType.Phone, 6, 5, 0.9));
        var pipeline = Pipeline(p =>
        {
            p.Detector = detector;
            p.MinConfidence = 0.5;
        });

        var report = await pipeline.DeduplicateAsync(Scope, new[] { Chunk("aaaaa bbbbb ccccc ddddd eeeee fffff") });

        Assert.Equal(PiiType.Phone, Assert.Single(Assert.Single(report.Decisions).Findings).Type);
    }

    [Fact]
    public async Task Overlapping_findings_are_resolved_to_one_with_the_more_specific_type_winning()
    {
        var detector = new FixedDetector(new PiiFinding(PiiType.Phone, 0, 10), new PiiFinding(PiiType.Card, 2, 12));
        var pipeline = Pipeline(p => p.Detector = detector);

        var report = await pipeline.DeduplicateAsync(Scope, new[] { Chunk("0123456789abcdefghij and more text here") });

        Assert.Equal(PiiType.Card, Assert.Single(Assert.Single(report.Decisions).Findings).Type);
    }

    [Fact]
    public async Task Findings_that_run_outside_the_text_are_ignored()
    {
        var detector = new FixedDetector(new PiiFinding(PiiType.Email, 3, 500), new PiiFinding(PiiType.Email, -1, 3));
        var pipeline = Pipeline(p => p.Detector = detector);

        var report = await pipeline.DeduplicateAsync(Scope, new[] { Chunk("short text with no personal data") });

        Assert.Empty(Assert.Single(report.Decisions).Findings);
    }

    [Fact]
    public async Task Scanning_a_batch_asks_the_detector_once_per_chunk_and_keeps_the_order()
    {
        var pipeline = Pipeline(p => p.Set(PiiType.Email, PiiPolicy.Mask));
        var chunks = new[]
        {
            Chunk(Plain(10), 0),
            Chunk(Plain(11) + " " + Email, 1),
            Chunk(Plain(12), 2),
        };

        var scanned = await pipeline.ScanAsync(chunks);

        Assert.Equal(new[] { 0, 1, 2 }, scanned.Select(c => c.Ordinal));
        Assert.Equal(Plain(10), scanned[0].Text);
        Assert.Contains("[EMAIL]", scanned[1].Text, StringComparison.Ordinal);
        Assert.Empty(scanned[2].PiiFindings!);
    }

    [Fact]
    public async Task A_masked_text_is_found_again_with_the_original()
    {
        var pipeline = Pipeline(p => p.Set(PiiType.Card, PiiPolicy.Mask));
        var original = Plain(13) + " card " + Card + " end";
        await pipeline.DeduplicateAsync(Scope, new[] { Chunk(original) });

        var found = await pipeline.FindStoredAsync(Scope, Chunk(original));

        Assert.Equal(MatchStage.Exact, Assert.Single(found).Stage);
    }

    // ---- ingestor ----

    private ChunkIngestor Ingestor(RechunkPipeline pipeline, AuditWriter? audit = null) =>
        new(pipeline, _embedder, _sink, _deadLetters, audit: audit);

    [Fact]
    public async Task The_ingestor_embeds_the_masked_text_and_never_the_original()
    {
        var texts = new List<string>();
        _embedder.Hook = (t, _) => texts.AddRange(t);
        var ingestor = Ingestor(Pipeline(p => p.Set(PiiType.Card, PiiPolicy.Mask)));

        var result = await ingestor.IngestAsync(new IngestRequest(Scope, new DocumentId("d1"), new[] { Chunk(Plain(14) + " card " + Card) }, "h1"));

        Assert.Equal(1, result.Embedded);
        Assert.DoesNotContain(texts, t => t.Contains("4111", StringComparison.Ordinal));
        Assert.All(_sink.StoredIds, id => Assert.DoesNotContain("4111", _sink.Get(id)!.Text, StringComparison.Ordinal));
        Assert.Contains("[CARD]", _sink.Get(_sink.StoredIds.Single())!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_ingestor_reports_a_blocked_chunk_and_does_not_embed_it()
    {
        var ingestor = Ingestor(Pipeline(p => p.Set(PiiType.Card, PiiPolicy.Block)));

        var result = await ingestor.IngestAsync(new IngestRequest(
            Scope, new DocumentId("d1"), new[] { Chunk(Plain(15) + " card " + Card), Chunk(Plain(16), 1) }, "h1"));

        Assert.Equal(1, result.Blocked);
        Assert.Equal(1, result.Embedded);
        Assert.Equal(ChunkStatus.Blocked, result.Outcomes.Single(o => o.Ordinal == 0).Status);
        Assert.True(result.IsComplete);
        Assert.Single(_sink.StoredIds);
        Assert.Equal(0, await _deadLetters.CountAsync(new DeadLetterQuery()));
    }

    [Fact]
    public async Task A_detector_that_fails_sends_the_chunk_to_the_dead_letters_without_its_text()
    {
        var ingestor = Ingestor(Pipeline(
            p => p.Detector = new ThrowingDetector(),
            o =>
            {
                o.Failures.MaxAttempts = 2;
                o.Failures.RetryDelay = TimeSpan.Zero;
                o.Failures.StoreTextInDeadLetters = true;
            }));
        var text = Plain(17) + " card " + Card;

        var result = await ingestor.IngestAsync(new IngestRequest(Scope, new DocumentId("d1"), new[] { Chunk(text) }, "h1"));

        Assert.Equal(0, result.Embedded);
        var letter = Assert.Single(result.DeadLetters);
        Assert.Equal(DeadLetterStage.Dedup, letter.Stage);
        Assert.StartsWith("unscanned-", letter.Chunk.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("4111", letter.Text ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(_sink.StoredIds);
    }

    private sealed class FixedDetector(params PiiFinding[] findings) : IPiiDetector
    {
        public ValueTask<IReadOnlyList<PiiFinding>> DetectAsync(string text, string? language, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<PiiFinding>>(findings);
    }

    private sealed class ThrowingDetector : IPiiDetector
    {
        public ValueTask<IReadOnlyList<PiiFinding>> DetectAsync(string text, string? language, CancellationToken ct = default) =>
            throw new InvalidOperationException("detector is down");
    }
}
