using Xunit;

namespace Rechunk.Core.Tests.Audit;

public class AuditWriterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    private static AuditEvent Event(string chunk, int seconds = 0) =>
        new(new ChunkId(chunk), new DocumentId("d1"), 1, "wiki", T0.AddSeconds(seconds), "m", AuditKind.New, "r") { Scope = "tenant-a" };

    private static AuditOptions Options(int attempts = 3) => new() { MaxAttempts = attempts, RetryDelay = TimeSpan.Zero };

    private sealed class ScriptedLog : IAuditLog
    {
        public InMemoryAuditLog Inner { get; } = new();

        public int Calls { get; private set; }

        /// <summary>The call numbers (from 1) that fail.</summary>
        public Func<int, bool> Fails { get; set; } = _ => false;

        public ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken ct = default)
        {
            Calls++;
            return Fails(Calls) ? throw new IOException("down") : Inner.AppendAsync(auditEvent, ct);
        }

        public IAsyncEnumerable<AuditEvent> QueryAsync(AuditQuery query, CancellationToken ct = default) => Inner.QueryAsync(query, ct);
    }

    private sealed class BrokenOutbox : IAuditOutbox
    {
        public ValueTask EnqueueAsync(IReadOnlyList<AuditEvent> events, CancellationToken ct = default) => throw new IOException("outbox down");

        public ValueTask<IReadOnlyList<AuditEvent>> PeekAsync(int max, CancellationToken ct = default) => throw new IOException("outbox down");

        public ValueTask RemoveAsync(IReadOnlyList<string> ids, CancellationToken ct = default) => throw new IOException("outbox down");

        public ValueTask<int> CountAsync(CancellationToken ct = default) => throw new IOException("outbox down");
    }

    [Fact]
    public async Task A_write_that_works_is_written_once_and_nothing_waits()
    {
        var log = new ScriptedLog();
        var writer = new AuditWriter(log, new InMemoryAuditOutbox(), Options());

        var result = await writer.WriteAsync(new[] { Event("a"), Event("b") });

        Assert.Equal(new AuditWriteResult(2, 0, 0), result);
        Assert.Equal(2, log.Inner.Events.Count);
        Assert.Equal(0, await writer.PendingAsync());
    }

    [Fact]
    public async Task An_empty_batch_does_nothing()
    {
        var log = new ScriptedLog();
        var writer = new AuditWriter(log, new InMemoryAuditOutbox(), Options());

        Assert.Equal(AuditWriteResult.None, await writer.WriteAsync(Array.Empty<AuditEvent>()));
        Assert.Equal(0, log.Calls);
    }

    [Fact]
    public async Task A_write_that_fails_once_is_tried_again_and_succeeds()
    {
        var log = new ScriptedLog { Fails = n => n == 1 };
        var writer = new AuditWriter(log, new InMemoryAuditOutbox(), Options());

        var result = await writer.WriteAsync(new[] { Event("a") });

        Assert.Equal(new AuditWriteResult(1, 0, 0), result);
        Assert.Single(log.Inner.Events);
        Assert.Equal(0, await writer.PendingAsync());
    }

    [Fact]
    public async Task A_write_that_keeps_failing_goes_to_the_outbox_after_the_tries_and_does_not_throw()
    {
        var log = new ScriptedLog { Fails = _ => true };
        var writer = new AuditWriter(log, new InMemoryAuditOutbox(), Options(attempts: 3));

        var result = await writer.WriteAsync(new[] { Event("a"), Event("b") });

        Assert.Equal(new AuditWriteResult(0, 2, 0), result);
        Assert.Equal(3, log.Calls);
        Assert.Equal(2, await writer.PendingAsync());
        Assert.Equal(0, writer.LostCount);
    }

    [Fact]
    public async Task Events_are_lost_and_counted_only_when_the_log_and_the_outbox_are_both_down()
    {
        var log = new ScriptedLog { Fails = _ => true };
        var writer = new AuditWriter(log, new BrokenOutbox(), Options(attempts: 1));

        var result = await writer.WriteAsync(new[] { Event("a"), Event("b") });

        Assert.Equal(new AuditWriteResult(0, 0, 2), result);
        Assert.Equal(2, writer.LostCount);
        Assert.Equal(0, await writer.PendingAsync());
    }

    [Fact]
    public async Task Without_an_outbox_a_failed_write_is_lost_and_counted()
    {
        var log = new ScriptedLog { Fails = _ => true };
        var writer = new AuditWriter(log, null, Options(attempts: 2));

        var result = await writer.WriteAsync(new[] { Event("a") });

        Assert.Equal(1, result.Lost);
        Assert.Equal(1, writer.LostCount);
        Assert.Equal(0, await writer.DrainAsync());
    }

    [Fact]
    public async Task Draining_sends_the_waiting_events_oldest_first_and_empties_the_outbox()
    {
        var log = new ScriptedLog { Fails = _ => true };
        var writer = new AuditWriter(log, new InMemoryAuditOutbox(), Options(attempts: 3));
        await writer.WriteAsync(new[] { Event("a", 1), Event("b", 2) });
        await writer.WriteAsync(new[] { Event("c", 3) });
        Assert.Equal(3, await writer.PendingAsync());

        log.Fails = _ => false;
        var sent = await writer.DrainAsync();

        Assert.Equal(3, sent);
        Assert.Equal(0, await writer.PendingAsync());
        Assert.Equal(new[] { "a", "b", "c" }, log.Inner.Events.Select(e => e.Chunk.Value));
    }

    [Fact]
    public async Task Draining_stops_at_the_first_failure_and_keeps_the_rest()
    {
        var log = new ScriptedLog { Fails = _ => true };
        var writer = new AuditWriter(log, new InMemoryAuditOutbox(), new AuditOptions { MaxAttempts = 1, RetryDelay = TimeSpan.Zero, DrainBatchSize = 2 });
        await writer.WriteAsync(new[] { Event("a", 1), Event("b", 2), Event("c", 3), Event("d", 4) });

        // the first batch of two goes, the second batch fails
        var calls = 0;
        log.Fails = _ => ++calls > 2;
        var sent = await writer.DrainAsync();

        Assert.Equal(2, sent);
        Assert.Equal(2, await writer.PendingAsync());
        log.Fails = _ => false;
        Assert.Equal(2, await writer.DrainAsync());
        Assert.Equal(new[] { "a", "b", "c", "d" }, log.Inner.Events.Select(e => e.Chunk.Value));
    }

    [Fact]
    public async Task Draining_twice_does_not_write_an_event_twice()
    {
        var log = new ScriptedLog { Fails = _ => true };
        var writer = new AuditWriter(log, new InMemoryAuditOutbox(), Options(attempts: 1));
        await writer.WriteAsync(new[] { Event("a") });
        log.Fails = _ => false;

        Assert.Equal(1, await writer.DrainAsync());
        Assert.Equal(0, await writer.DrainAsync());
        Assert.Single(log.Inner.Events);
    }

    [Fact]
    public async Task A_drain_with_a_broken_outbox_does_not_throw()
    {
        var writer = new AuditWriter(new ScriptedLog(), new BrokenOutbox(), Options());

        Assert.Equal(0, await writer.DrainAsync());
        Assert.Equal(0, await writer.PendingAsync());
    }

    [Fact]
    public async Task A_cancelled_write_is_parked_in_the_outbox_so_the_events_are_kept()
    {
        var log = new ScriptedLog { Fails = _ => true };
        var writer = new AuditWriter(log, new InMemoryAuditOutbox(), Options());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await writer.WriteAsync(new[] { Event("a") }, cts.Token);

        Assert.Equal(1, result.Queued);
        Assert.Equal(1, await writer.PendingAsync());
    }

    [Fact]
    public async Task A_cancelled_drain_throws()
    {
        var log = new ScriptedLog { Fails = _ => true };
        var writer = new AuditWriter(log, new InMemoryAuditOutbox(), Options(attempts: 1));
        await writer.WriteAsync(new[] { Event("a") });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writer.DrainAsync(cts.Token));
        Assert.Equal(1, await writer.PendingAsync());
    }

    [Fact]
    public void Options_that_make_no_sense_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AuditWriter(new ScriptedLog(), null, new AuditOptions { MaxAttempts = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AuditWriter(new ScriptedLog(), null, new AuditOptions { RetryDelay = TimeSpan.FromSeconds(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AuditWriter(new ScriptedLog(), null, new AuditOptions { DrainBatchSize = 0 }));
    }
}
