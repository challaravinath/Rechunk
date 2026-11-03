using System.Collections.Concurrent;

namespace Rechunk;

/// <summary>Exact hash index that lives in memory. Fine for tests and small jobs, gone when the process ends.</summary>
public sealed class InMemoryExactHashIndex : IExactHashIndex
{
    private readonly ConcurrentDictionary<(string Scope, string Hash), ChunkId> _items = new();

    public ValueTask<ChunkId?> FindAsync(string scope, ReadOnlyMemory<byte> exactHash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ChunkId? found = _items.TryGetValue(Key(scope, exactHash), out var id) ? id : null;
        return ValueTask.FromResult(found);
    }

    public ValueTask<ExactAddResult> AddIfAbsentAsync(string scope, ReadOnlyMemory<byte> exactHash, ChunkId id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var key = Key(scope, exactHash);

        // TryAdd is atomic, so only one caller can ever win for the same hash
        if (_items.TryAdd(key, id))
        {
            return ValueTask.FromResult(new ExactAddResult(id, true));
        }

        return ValueTask.FromResult(new ExactAddResult(_items[key], false));
    }

    public ValueTask<bool> RemoveAsync(string scope, ReadOnlyMemory<byte> exactHash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_items.TryRemove(Key(scope, exactHash), out _));
    }

    private static (string, string) Key(string scope, ReadOnlyMemory<byte> hash) =>
        (scope, Convert.ToHexString(hash.Span));
}
