using System.Collections.Concurrent;

namespace Rechunk;

/// <summary>Dead letters in memory. Fine for tests and a single process, gone when it ends.</summary>
public sealed class InMemoryDeadLetterStore : IDeadLetterStore
{
    private readonly ConcurrentDictionary<string, DeadLetter> _items = new(StringComparer.Ordinal);

    public ValueTask AddAsync(DeadLetter deadLetter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deadLetter);
        ct.ThrowIfCancellationRequested();
        _items[deadLetter.Id] = deadLetter;
        return ValueTask.CompletedTask;
    }

    public ValueTask<DeadLetter?> GetAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_items.TryGetValue(id, out var found) ? found : null);
    }

    public ValueTask<IReadOnlyList<DeadLetter>> ListAsync(DeadLetterQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<DeadLetter> page = Filter(query)
            .OrderBy(d => d.FailedAt)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .Skip(Math.Max(0, query.Skip))
            .Take(Math.Max(0, query.Take))
            .ToList();
        return ValueTask.FromResult(page);
    }

    public ValueTask<long> CountAsync(DeadLetterQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult((long)Filter(query).Count());
    }

    public ValueTask<bool> RemoveAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_items.TryRemove(id, out _));
    }

    private IEnumerable<DeadLetter> Filter(DeadLetterQuery q) => _items.Values.Where(d =>
        (q.Scope is null || d.Scope == q.Scope)
        && (q.Stage is null || d.Stage == q.Stage)
        && (q.Chunk is null || d.Chunk == q.Chunk)
        && (q.Document is null || d.Document == q.Document));
}
