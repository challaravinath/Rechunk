namespace Rechunk;

/// <summary>
/// Keeps audit events in a list in this process. It is the default log, so audit works without any setup, but the events
/// are gone when the process ends. A limit keeps it from growing for ever: when it is reached the oldest events are
/// dropped. Use the SQLite or Redis log when the trail has to last.
/// </summary>
public sealed class InMemoryAuditLog : IAuditLog
{
    private readonly object _gate = new();
    private readonly List<AuditEvent> _events = new();
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly int _limit;

    public InMemoryAuditLog(int limit = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        _limit = limit;
    }

    public IReadOnlyList<AuditEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToList();
            }
        }
    }

    public ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_ids.Add(auditEvent.Id))
            {
                _events.Add(auditEvent);
                while (_events.Count > _limit)
                {
                    _ids.Remove(_events[0].Id);
                    _events.RemoveAt(0);
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<AuditEvent> QueryAsync(
        AuditQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await Task.CompletedTask;
        foreach (var e in AuditOrdering.Page(Events.Where(query.Matches), query))
        {
            ct.ThrowIfCancellationRequested();
            yield return e;
        }
    }
}

/// <summary>The order and the paging every audit log has to use.</summary>
internal static class AuditOrdering
{
    public static IEnumerable<AuditEvent> Sort(IEnumerable<AuditEvent> events) =>
        events.OrderBy(e => AuditQuery.Ms(e.At)).ThenBy(e => e.Id, StringComparer.Ordinal);

    public static IEnumerable<AuditEvent> Page(IEnumerable<AuditEvent> events, AuditQuery query)
    {
        var sorted = Sort(events);
        if (query.Skip > 0)
        {
            sorted = sorted.Skip(query.Skip);
        }

        return query.Take is { } take ? sorted.Take(take) : sorted;
    }
}
