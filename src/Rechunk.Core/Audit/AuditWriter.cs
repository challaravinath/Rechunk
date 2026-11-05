using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rechunk;

/// <summary>What happened to a batch of audit events: written to the log, parked in the outbox, or lost.</summary>
public readonly record struct AuditWriteResult(int Written, int Queued, int Lost)
{
    public static AuditWriteResult None => new(0, 0, 0);
}

/// <summary>
/// Writes audit events the way the rest of Rechunk treats its state. A write that fails is tried again with a back off.
/// When the tries run out the events go to the outbox, and the next call to <see cref="DrainAsync"/> sends them again.
/// WriteAsync never throws because of a failed write: it is called after the vectors and the manifest were committed, and
/// failing then would make a finished revision look like a failed one. Events are lost only when the log and the outbox
/// are both down, and that is logged and counted in <see cref="LostCount"/>.
/// </summary>
public sealed class AuditWriter
{
    private readonly IAuditLog _log;
    private readonly IAuditOutbox? _outbox;
    private readonly AuditOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private long _lost;

    public AuditWriter(
        IAuditLog log,
        IAuditOutbox? outbox = null,
        AuditOptions? options = null,
        ILogger? logger = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _outbox = outbox;
        _options = options ?? new AuditOptions();
        _options.Validate();
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    public IAuditLog Log => _log;

    /// <summary>How many events were lost since this writer was created.</summary>
    public long LostCount => Interlocked.Read(ref _lost);

    public async ValueTask<AuditWriteResult> WriteAsync(IReadOnlyList<AuditEvent> events, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return AuditWriteResult.None;
        }

        var delay = _options.RetryDelay;
        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            try
            {
                await _log.AppendManyAsync(events, ct).ConfigureAwait(false);
                return new AuditWriteResult(events.Count, 0, 0);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audit write failed, attempt {Attempt} of {Max}.", attempt, _options.MaxAttempts);
                if (attempt < _options.MaxAttempts && delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(delay, _time, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    delay = TimeSpan.FromTicks((long)(delay.Ticks * _options.RetryBackoff));
                }
            }
        }

        return await ParkAsync(events).ConfigureAwait(false);
    }

    private async ValueTask<AuditWriteResult> ParkAsync(IReadOnlyList<AuditEvent> events)
    {
        if (_outbox is not null)
        {
            try
            {
                // this runs after a commit and after a possible cancel, so it does not take the caller's token
                await _outbox.EnqueueAsync(events, CancellationToken.None).ConfigureAwait(false);
                return new AuditWriteResult(0, events.Count, 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audit outbox write failed, {Count} audit events are lost.", events.Count);
            }
        }
        else
        {
            _logger.LogError("Audit write failed and there is no outbox, {Count} audit events are lost.", events.Count);
        }

        Interlocked.Add(ref _lost, events.Count);
        return new AuditWriteResult(0, 0, events.Count);
    }

    /// <summary>
    /// Sends what waits in the outbox to the log, oldest first. It stops at the first failure and keeps the rest.
    /// Returns how many events were sent. It does not throw because of the log being down.
    /// </summary>
    public async ValueTask<int> DrainAsync(CancellationToken ct = default)
    {
        if (_outbox is null)
        {
            return 0;
        }

        var sent = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var batch = await _outbox.PeekAsync(_options.DrainBatchSize, ct).ConfigureAwait(false);
                if (batch.Count == 0)
                {
                    return sent;
                }

                await _log.AppendManyAsync(batch, ct).ConfigureAwait(false);
                await _outbox.RemoveAsync(batch.Select(e => e.Id).ToList(), ct).ConfigureAwait(false);
                sent += batch.Count;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Draining the audit outbox stopped, the rest stays queued.");
            return sent;
        }
    }

    /// <summary>How many events wait in the outbox.</summary>
    public async ValueTask<int> PendingAsync(CancellationToken ct = default)
    {
        if (_outbox is null)
        {
            return 0;
        }

        try
        {
            return await _outbox.CountAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not count the audit outbox.");
            return 0;
        }
    }
}

/// <summary>An outbox that lives in this process. It is the default, so it does not survive a restart.</summary>
public sealed class InMemoryAuditOutbox : IAuditOutbox
{
    private readonly object _gate = new();
    private readonly List<AuditEvent> _events = new();

    public ValueTask EnqueueAsync(IReadOnlyList<AuditEvent> events, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        lock (_gate)
        {
            foreach (var e in events)
            {
                if (!_events.Any(x => x.Id == e.Id))
                {
                    _events.Add(e);
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<AuditEvent>> PeekAsync(int max, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<AuditEvent>>(_events.Take(max).ToList());
        }
    }

    public ValueTask RemoveAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        var set = new HashSet<string>(ids, StringComparer.Ordinal);
        lock (_gate)
        {
            _events.RemoveAll(e => set.Contains(e.Id));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<int> CountAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_events.Count);
        }
    }
}
