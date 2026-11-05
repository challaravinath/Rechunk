using Xunit;

namespace Rechunk.Core.Tests.Audit;

/// <summary>What every audit log has to do, whatever it stores the events in.</summary>
public abstract class AuditLogContract : IDisposable
{
    protected static readonly DateTimeOffset T0 = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    protected abstract IAuditLog CreateLog();

    protected abstract IAuditOutbox CreateOutbox();

    public virtual void Dispose()
    {
    }

    protected static AuditEvent Event(
        string chunk = "c1",
        string document = "d1",
        int revision = 1,
        string source = "wiki",
        int seconds = 0,
        AuditKind kind = AuditKind.New,
        string scope = "tenant-a",
        string? language = null,
        IReadOnlyList<PiiHit>? findings = null,
        string? id = null)
    {
        var e = new AuditEvent(new ChunkId(chunk), new DocumentId(document), revision, source, T0.AddSeconds(seconds), "model-1", kind, "because")
        {
            Scope = scope,
            Language = language,
            Stage = AuditStages.Embed,
            Actor = "revision",
            Findings = findings ?? Array.Empty<PiiHit>(),
        };
        return id is null ? e : e with { Id = id };
    }

    protected static async Task<List<AuditEvent>> Query(IAuditLog log, AuditQuery query)
    {
        var all = new List<AuditEvent>();
        await foreach (var e in log.QueryAsync(query))
        {
            all.Add(e);
        }

        return all;
    }

    private static async Task<List<AuditEvent>> Everything(IAuditLog log) => await Query(log, new AuditQuery());

    [SkippableFact]
    public async Task An_event_comes_back_with_everything_that_was_written()
    {
        var log = CreateLog();
        var hits = new[] { new PiiHit(PiiType.Card, 4, 19, PiiPolicy.Mask), new PiiHit(new PiiType("custom"), 30, 5, PiiPolicy.Flag) };
        var written = Event(language: "en", findings: hits, kind: AuditKind.Flagged) with
        {
            Related = new ChunkId("other"),
            Counts = new Dictionary<string, int> { ["documents"] = 3, ["chunks"] = 9 },
        };

        await log.AppendAsync(written);

        var read = Assert.Single(await Everything(log));
        Assert.Equal(written.Id, read.Id);
        Assert.Equal(written.Chunk, read.Chunk);
        Assert.Equal(written.Document, read.Document);
        Assert.Equal(written.Revision, read.Revision);
        Assert.Equal(written.Source, read.Source);
        Assert.Equal(written.Scope, read.Scope);
        Assert.Equal(written.Language, read.Language);
        Assert.Equal(written.Stage, read.Stage);
        Assert.Equal(written.Actor, read.Actor);
        Assert.Equal(written.Kind, read.Kind);
        Assert.Equal(written.Reason, read.Reason);
        Assert.Equal(written.EmbeddingModel, read.EmbeddingModel);
        Assert.Equal(written.Related, read.Related);
        Assert.Equal(written.At.ToUnixTimeMilliseconds(), read.At.ToUnixTimeMilliseconds());
        Assert.Equal(hits, read.Findings);
        Assert.Equal(3, read.Counts["documents"]);
        Assert.Equal(9, read.Counts["chunks"]);
    }

    [SkippableFact]
    public async Task Every_kind_can_be_written_and_read_back()
    {
        var log = CreateLog();
        var kinds = Enum.GetValues<AuditKind>();
        for (var i = 0; i < kinds.Length; i++)
        {
            await log.AppendAsync(Event(chunk: "c" + i, kind: kinds[i], seconds: i));
        }

        var read = await Everything(log);

        Assert.Equal(kinds, read.Select(e => e.Kind));
    }

    [SkippableFact]
    public async Task Writing_the_same_event_twice_keeps_one()
    {
        var log = CreateLog();
        var e = Event(findings: new[] { new PiiHit(PiiType.Email, 0, 5, PiiPolicy.Flag) });

        await log.AppendAsync(e);
        await log.AppendAsync(e);
        await log.AppendManyAsync(new[] { e, e });

        Assert.Single(await Everything(log));
        Assert.Single(await Query(log, new AuditQuery { FindingType = PiiType.Email }));
    }

    [SkippableFact]
    public async Task A_batch_is_written_as_a_whole()
    {
        var log = CreateLog();

        await log.AppendManyAsync(Enumerable.Range(0, 25).Select(i => Event(chunk: "c" + i, seconds: i)).ToList());

        Assert.Equal(25, (await Everything(log)).Count);
    }

    [SkippableFact]
    public async Task Events_come_back_oldest_first_whatever_the_order_they_were_written_in()
    {
        var log = CreateLog();
        await log.AppendAsync(Event(chunk: "late", seconds: 30));
        await log.AppendAsync(Event(chunk: "early", seconds: 10));
        await log.AppendAsync(Event(chunk: "mid", seconds: 20));

        var read = await Everything(log);

        Assert.Equal(new[] { "early", "mid", "late" }, read.Select(e => e.Chunk.Value));
    }

    [SkippableFact]
    public async Task Events_with_the_same_time_keep_a_stable_order()
    {
        var log = CreateLog();
        foreach (var id in new[] { "e3", "e1", "e2" })
        {
            await log.AppendAsync(Event(chunk: id, id: id));
        }

        var first = (await Everything(log)).Select(e => e.Id).ToList();
        var second = (await Everything(log)).Select(e => e.Id).ToList();

        Assert.Equal(new[] { "e1", "e2", "e3" }, first);
        Assert.Equal(first, second);
    }

    [SkippableFact]
    public async Task A_query_by_document_returns_only_that_document()
    {
        var log = CreateLog();
        await log.AppendAsync(Event(document: "d1", seconds: 1));
        await log.AppendAsync(Event(document: "d2", seconds: 2));
        await log.AppendAsync(Event(document: "d1", seconds: 3, chunk: "c2"));

        var read = await Query(log, new AuditQuery(Document: new DocumentId("d1")));

        Assert.Equal(2, read.Count);
        Assert.All(read, e => Assert.Equal("d1", e.Document.Value));
    }

    [SkippableFact]
    public async Task A_query_by_chunk_returns_the_history_of_that_chunk_across_documents()
    {
        var log = CreateLog();
        await log.AppendAsync(Event(chunk: "x", document: "d1", seconds: 1, kind: AuditKind.New));
        await log.AppendAsync(Event(chunk: "y", document: "d1", seconds: 2));
        await log.AppendAsync(Event(chunk: "x", document: "d2", seconds: 3, kind: AuditKind.Skipped));
        await log.AppendAsync(Event(chunk: "x", document: "d2", seconds: 4, kind: AuditKind.Erased));

        var read = await Query(log, new AuditQuery(Chunk: new ChunkId("x")));

        Assert.Equal(new[] { AuditKind.New, AuditKind.Skipped, AuditKind.Erased }, read.Select(e => e.Kind));
    }

    [SkippableFact]
    public async Task A_query_by_source_returns_only_that_source()
    {
        var log = CreateLog();
        await log.AppendAsync(Event(source: "wiki", seconds: 1));
        await log.AppendAsync(Event(source: "drive", seconds: 2, chunk: "c2"));
        await log.AppendAsync(Event(source: "wiki", seconds: 3, chunk: "c3"));

        var read = await Query(log, new AuditQuery { Source = "wiki" });

        Assert.Equal(new[] { "c1", "c3" }, read.Select(e => e.Chunk.Value));
    }

    [SkippableFact]
    public async Task A_time_range_includes_both_ends_to_the_millisecond()
    {
        var log = CreateLog();
        for (var i = 0; i < 5; i++)
        {
            await log.AppendAsync(Event(chunk: "c" + i, seconds: i * 10));
        }

        var read = await Query(log, new AuditQuery(From: T0.AddSeconds(10), To: T0.AddSeconds(30)));

        Assert.Equal(new[] { "c1", "c2", "c3" }, read.Select(e => e.Chunk.Value));
        Assert.Equal(new[] { "c3", "c4" }, (await Query(log, new AuditQuery(From: T0.AddSeconds(30)))).Select(e => e.Chunk.Value));
        Assert.Equal(new[] { "c0", "c1" }, (await Query(log, new AuditQuery(To: T0.AddSeconds(10)))).Select(e => e.Chunk.Value));
        Assert.Empty(await Query(log, new AuditQuery(From: T0.AddSeconds(31), To: T0.AddSeconds(39))));
    }

    [SkippableFact]
    public async Task Time_is_stored_without_the_offset_it_was_given_in()
    {
        var log = CreateLog();
        var offset = new DateTimeOffset(T0.UtcDateTime).ToOffset(TimeSpan.FromHours(5.5));
        await log.AppendAsync(Event() with { At = offset });

        var read = await Query(log, new AuditQuery(From: T0, To: T0));

        Assert.Equal(T0.ToUnixTimeMilliseconds(), Assert.Single(read).At.ToUnixTimeMilliseconds());
    }

    [SkippableFact]
    public async Task A_query_by_kind_scope_and_finding_type_narrows_the_result()
    {
        var log = CreateLog();
        var card = new[] { new PiiHit(PiiType.Card, 0, 19, PiiPolicy.Flag) };
        var email = new[] { new PiiHit(PiiType.Email, 0, 10, PiiPolicy.Mask) };
        await log.AppendAsync(Event(chunk: "a", seconds: 1, kind: AuditKind.New, findings: card));
        await log.AppendAsync(Event(chunk: "b", seconds: 2, kind: AuditKind.Blocked, findings: card));
        await log.AppendAsync(Event(chunk: "c", seconds: 3, kind: AuditKind.New, findings: email));
        await log.AppendAsync(Event(chunk: "d", seconds: 4, kind: AuditKind.New, scope: "tenant-b"));

        Assert.Equal(new[] { "b" }, (await Query(log, new AuditQuery { Kind = AuditKind.Blocked })).Select(e => e.Chunk.Value));
        Assert.Equal(new[] { "d" }, (await Query(log, new AuditQuery { Scope = "tenant-b" })).Select(e => e.Chunk.Value));
        Assert.Equal(new[] { "a", "b" }, (await Query(log, new AuditQuery { FindingType = PiiType.Card })).Select(e => e.Chunk.Value));
        Assert.Equal(new[] { "a" }, (await Query(log, new AuditQuery { FindingType = PiiType.Card, Kind = AuditKind.New })).Select(e => e.Chunk.Value));
        Assert.Empty(await Query(log, new AuditQuery { FindingType = PiiType.Ssn }));
    }

    [SkippableFact]
    public async Task Filters_can_be_combined()
    {
        var log = CreateLog();
        await log.AppendAsync(Event(document: "d1", chunk: "a", seconds: 1, source: "wiki"));
        await log.AppendAsync(Event(document: "d1", chunk: "b", seconds: 2, source: "wiki"));
        await log.AppendAsync(Event(document: "d2", chunk: "a", seconds: 3, source: "wiki"));
        await log.AppendAsync(Event(document: "d1", chunk: "a", seconds: 40, source: "wiki"));

        var read = await Query(log, new AuditQuery(new DocumentId("d1"), new ChunkId("a"), T0, T0.AddSeconds(10)) { Source = "wiki" });

        var one = Assert.Single(read);
        Assert.Equal(T0.AddSeconds(1).ToUnixTimeMilliseconds(), one.At.ToUnixTimeMilliseconds());
    }

    [SkippableFact]
    public async Task Skip_and_take_page_through_the_result()
    {
        var log = CreateLog();
        for (var i = 0; i < 12; i++)
        {
            await log.AppendAsync(Event(chunk: "c" + i.ToString("00"), seconds: i));
        }

        var page1 = await Query(log, new AuditQuery { Take = 5 });
        var page2 = await Query(log, new AuditQuery { Skip = 5, Take = 5 });
        var page3 = await Query(log, new AuditQuery { Skip = 10, Take = 5 });
        var past = await Query(log, new AuditQuery { Skip = 50, Take = 5 });

        Assert.Equal(5, page1.Count);
        Assert.Equal(5, page2.Count);
        Assert.Equal(2, page3.Count);
        Assert.Empty(past);
        Assert.Equal(12, page1.Concat(page2).Concat(page3).Select(e => e.Id).Distinct().Count());
        Assert.Equal("c00", page1[0].Chunk.Value);
        Assert.Equal("c11", page3[1].Chunk.Value);
    }

    [SkippableFact]
    public async Task Take_zero_returns_nothing()
    {
        var log = CreateLog();
        await log.AppendAsync(Event());

        Assert.Empty(await Query(log, new AuditQuery { Take = 0 }));
    }

    [SkippableFact]
    public async Task A_query_that_matches_nothing_returns_an_empty_sequence()
    {
        var log = CreateLog();
        await log.AppendAsync(Event());

        Assert.Empty(await Query(log, new AuditQuery(Document: new DocumentId("nope"))));
        Assert.Empty(await Query(log, new AuditQuery(Chunk: new ChunkId("nope"))));
        Assert.Empty(await Query(log, new AuditQuery { Source = "nope" }));
    }

    [SkippableFact]
    public async Task A_big_result_is_returned_complete()
    {
        var log = CreateLog();
        await log.AppendManyAsync(Enumerable.Range(0, 1300).Select(i => Event(chunk: "c" + i, seconds: i)).ToList());

        var read = await Everything(log);

        Assert.Equal(1300, read.Count);
        Assert.Equal(1300, read.Select(e => e.Id).Distinct().Count());
        Assert.Equal(read.OrderBy(e => e.At).Select(e => e.Id), read.Select(e => e.Id));
    }

    [SkippableFact]
    public async Task Odd_characters_in_ids_sources_and_reasons_survive()
    {
        var log = CreateLog();
        var odd = Event(chunk: "c:1|2\"3", document: "doc/with spaces\u00e9\U0001F600", source: "s'o;u--rce") with { Reason = "line\nbreak \"quoted\" \\ backslash" };

        await log.AppendAsync(odd);

        var read = Assert.Single(await Query(log, new AuditQuery(Document: odd.Document, Chunk: odd.Chunk) { Source = odd.Source }));
        Assert.Equal(odd.Reason, read.Reason);
        Assert.Equal(odd.Document, read.Document);
        Assert.Equal(odd.Source, read.Source);
    }

    [SkippableFact]
    public async Task Concurrent_writers_do_not_lose_events()
    {
        var log = CreateLog();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                await log.AppendAsync(Event(chunk: $"w{w}-{i}", seconds: i));
            }
        })));

        Assert.Equal(160, (await Everything(log)).Count);
    }

    [SkippableFact]
    public async Task A_cancelled_query_stops()
    {
        var log = CreateLog();
        await log.AppendAsync(Event());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var unused in log.QueryAsync(new AuditQuery(), cts.Token))
            {
            }
        });
    }

    // ---- outbox ----

    [SkippableFact]
    public async Task The_outbox_hands_events_back_in_the_order_they_went_in()
    {
        var outbox = CreateOutbox();
        await outbox.EnqueueAsync(new[] { Event(chunk: "a", id: "e1"), Event(chunk: "b", id: "e2") });
        await outbox.EnqueueAsync(new[] { Event(chunk: "c", id: "e3") });

        var peek = await outbox.PeekAsync(10);

        Assert.Equal(new[] { "e1", "e2", "e3" }, peek.Select(e => e.Id));
        Assert.Equal(3, await outbox.CountAsync());
    }

    [SkippableFact]
    public async Task Peeking_does_not_remove_and_removing_is_by_id()
    {
        var outbox = CreateOutbox();
        await outbox.EnqueueAsync(new[] { Event(id: "e1"), Event(chunk: "b", id: "e2"), Event(chunk: "c", id: "e3") });

        Assert.Equal(2, (await outbox.PeekAsync(2)).Count);
        Assert.Equal(3, await outbox.CountAsync());

        await outbox.RemoveAsync(new[] { "e2", "unknown" });

        Assert.Equal(new[] { "e1", "e3" }, (await outbox.PeekAsync(10)).Select(e => e.Id));
        Assert.Equal(2, await outbox.CountAsync());
    }

    [SkippableFact]
    public async Task The_same_event_queued_twice_is_held_once()
    {
        var outbox = CreateOutbox();
        var e = Event(id: "e1");

        await outbox.EnqueueAsync(new[] { e });
        await outbox.EnqueueAsync(new[] { e });

        Assert.Equal(1, await outbox.CountAsync());
    }

    [SkippableFact]
    public async Task An_empty_outbox_has_nothing_to_peek()
    {
        var outbox = CreateOutbox();

        Assert.Empty(await outbox.PeekAsync(10));
        Assert.Equal(0, await outbox.CountAsync());
        await outbox.RemoveAsync(Array.Empty<string>());
    }

    [SkippableFact]
    public async Task An_event_that_went_through_the_outbox_is_equal_to_the_one_that_went_in()
    {
        var outbox = CreateOutbox();
        var e = Event(findings: new[] { new PiiHit(PiiType.Iban, 3, 22, PiiPolicy.Mask) }, id: "e1", language: "hi");
        await outbox.EnqueueAsync(new[] { e });

        var back = Assert.Single(await outbox.PeekAsync(5));

        Assert.Equal(e.Id, back.Id);
        Assert.Equal(e.Findings, back.Findings);
        Assert.Equal(e.Language, back.Language);
        Assert.Equal(e.At.ToUnixTimeMilliseconds(), back.At.ToUnixTimeMilliseconds());
    }
}
