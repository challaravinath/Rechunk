using Xunit;

namespace Rechunk.Core.Tests.Pii;

public class PiiServiceDetectorTests
{
    private sealed class Client(Func<IReadOnlyList<PiiRequest>, IReadOnlyList<IReadOnlyList<PiiEntity>>> answer) : IPiiAnalysisClient
    {
        public int Calls;

        public ValueTask<IReadOnlyList<IReadOnlyList<PiiEntity>>> AnalyzeAsync(IReadOnlyList<PiiRequest> requests, CancellationToken ct = default)
        {
            Calls++;
            return ValueTask.FromResult(answer(requests));
        }
    }

    private static IReadOnlyList<IReadOnlyList<PiiEntity>> One(params PiiEntity[] entities) => new[] { (IReadOnlyList<PiiEntity>)entities };

    [Fact]
    public async Task Presidio_and_azure_entity_names_map_to_the_built_in_types()
    {
        var client = new Client(_ => One(
            new PiiEntity("EMAIL_ADDRESS", 0, 4, 0.9),
            new PiiEntity("PhoneNumber", 5, 4, 0.9),
            new PiiEntity("IN_PAN", 10, 4, 0.9)));
        var detector = new PiiServiceDetector(client);

        var found = await detector.DetectAsync("aaaa bbbb cccc dddd", null);

        Assert.Equal(new[] { PiiType.Email, PiiType.Phone, PiiType.Pan }, found.Select(f => f.Type));
    }

    [Fact]
    public async Task An_entity_type_that_is_not_mapped_keeps_its_own_name()
    {
        var detector = new PiiServiceDetector(new Client(_ => One(new PiiEntity("PERSON", 0, 4, 0.9))));

        var found = await detector.DetectAsync("John went home", null);

        Assert.Equal("PERSON", Assert.Single(found).Type.Name);
    }

    [Fact]
    public async Task A_custom_map_overrides_the_default()
    {
        var map = new Dictionary<string, PiiType> { ["EMAIL_ADDRESS"] = new PiiType("contact") };
        var detector = new PiiServiceDetector(new Client(_ => One(new PiiEntity("EMAIL_ADDRESS", 0, 4, 0.9))), map);

        var found = await detector.DetectAsync("abcd efgh", null);

        Assert.Equal("contact", Assert.Single(found).Type.Name);
    }

    [Fact]
    public async Task Entities_below_the_minimum_score_or_with_bad_offsets_are_dropped()
    {
        var client = new Client(_ => One(
            new PiiEntity("EMAIL_ADDRESS", 0, 4, 0.2),
            new PiiEntity("EMAIL_ADDRESS", 0, 0, 0.9),
            new PiiEntity("EMAIL_ADDRESS", -2, 3, 0.9),
            new PiiEntity("EMAIL_ADDRESS", 6, 50, 0.9),
            new PiiEntity("EMAIL_ADDRESS", 5, 4, 0.9)));
        var detector = new PiiServiceDetector(client, minScore: 0.5);

        var found = await detector.DetectAsync("abcd efgh ijkl", null);

        var hit = Assert.Single(found);
        Assert.Equal(5, hit.Start);
        Assert.Equal(4, hit.Length);
        Assert.Equal(0.9, hit.Confidence);
    }

    [Fact]
    public async Task Code_point_offsets_are_turned_into_utf16_offsets()
    {
        // Presidio counts code points. The emoji is one code point and two UTF-16 units.
        var text = "\U0001F600 mail jane@example.org now";
        var client = new Client(_ => One(new PiiEntity("EMAIL_ADDRESS", 7, 16, 0.9)));
        var detector = new PiiServiceDetector(client, offsets: PiiOffsetUnit.CodePoints);

        var found = await detector.DetectAsync(text, "en");

        var hit = Assert.Single(found);
        Assert.Equal("jane@example.org", text.Substring(hit.Start, hit.Length));
    }

    [Fact]
    public async Task Utf16_offsets_are_used_as_they_are()
    {
        var text = "\U0001F600 mail jane@example.org now";
        var client = new Client(_ => One(new PiiEntity("EmailAddress", 8, 16, 0.9)));
        var detector = new PiiServiceDetector(client, new Dictionary<string, PiiType> { ["EmailAddress"] = PiiType.Email });

        var found = await detector.DetectAsync(text, "en");

        var hit = Assert.Single(found);
        Assert.Equal("jane@example.org", text.Substring(hit.Start, hit.Length));
    }

    [Fact]
    public async Task A_batch_makes_one_call_and_a_wrong_number_of_answers_is_an_error()
    {
        var client = new Client(r => r.Select(_ => (IReadOnlyList<PiiEntity>)Array.Empty<PiiEntity>()).ToList());
        var detector = new PiiServiceDetector(client);
        await detector.DetectManyAsync(new[] { new PiiRequest("a", null), new PiiRequest("b", null), new PiiRequest("c", null) });
        Assert.Equal(1, client.Calls);

        var broken = new PiiServiceDetector(new Client(_ => One()));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await broken.DetectManyAsync(new[] { new PiiRequest("a", null), new PiiRequest("b", null) }));
    }

    [Fact]
    public async Task The_composite_detector_joins_the_findings_of_every_detector()
    {
        var service = new PiiServiceDetector(new Client(_ => One(new PiiEntity("PERSON", 0, 4, 0.9))));
        var composite = new CompositePiiDetector(new BuiltInPiiDetector(), service);

        var found = await composite.DetectAsync("John wrote to jane@example.org", null);

        Assert.Contains(found, f => f.Type == PiiType.Email);
        Assert.Contains(found, f => f.Type.Name == "PERSON");
        Assert.Throws<ArgumentException>(() => new CompositePiiDetector());
    }

    [Fact]
    public async Task A_service_detector_plugs_into_the_pipeline_through_the_options()
    {
        var detector = new PiiServiceDetector(new Client(r => r.Select(x =>
            (IReadOnlyList<PiiEntity>)(x.Text.Contains("John", StringComparison.Ordinal) ? new[] { new PiiEntity("PERSON", x.Text.IndexOf("John", StringComparison.Ordinal), 4, 0.95) } : Array.Empty<PiiEntity>())).ToList()));
        var pipeline = RechunkPipeline.CreateBuilder()
            .Configure(o =>
            {
                o.Pii.Enabled = true;
                o.Pii.Detector = detector;
                o.Pii.Set(new PiiType("PERSON"), PiiPolicy.Mask);
            })
            .Build();

        var report = await pipeline.DeduplicateAsync("s", new[] { new ChunkInput(0, "John Smith opened the account in May") });

        Assert.StartsWith("[PERSON] Smith", Assert.Single(report.Decisions).Input.Text, StringComparison.Ordinal);
    }
}
