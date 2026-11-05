namespace Rechunk;

/// <summary>Keeps audit events in a list in this process. For tests and small setups, write your own IAuditLog for real use.</summary>
public sealed class InMemoryAuditLog : IAuditLog
{
    private readonly object _gate = new();
    private readonly List<AuditEvent> _events = new();

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
            _events.Add(auditEvent);
        }

        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<AuditEvent> QueryAsync(
        AuditQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await Task.CompletedTask;
        foreach (var e in Events)
        {
            ct.ThrowIfCancellationRequested();
            if ((query.Document is null || e.Document == query.Document)
                && (query.Chunk is null || e.Chunk == query.Chunk)
                && (query.From is null || e.At >= query.From)
                && (query.To is null || e.At <= query.To))
            {
                yield return e;
            }
        }
    }
}
