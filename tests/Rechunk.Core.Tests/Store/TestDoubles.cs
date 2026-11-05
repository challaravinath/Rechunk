namespace Rechunk.Core.Tests.Store;

/// <summary>A clock that only moves when the test says so. Leases are tested with it.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Gives every text its own small vector, and fails when the test tells it to.</summary>
public sealed class ScriptedEmbedder : IChunkEmbedder
{
    public string ModelId => "test-model";

    public int Calls;

    /// <summary>Called with the texts of each call, return normally to succeed or throw to fail.</summary>
    public Action<IReadOnlyList<string>, int>? Hook { get; set; }

    public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var call = Interlocked.Increment(ref Calls);
        Hook?.Invoke(texts, call);
        IReadOnlyList<ReadOnlyMemory<float>> vectors = texts.Select(t => (ReadOnlyMemory<float>)new float[] { t.Length, 1 }).ToList();
        return ValueTask.FromResult(vectors);
    }
}

public sealed class RecordingSink : IVectorSink
{
    private readonly object _gate = new();
    private readonly Dictionary<string, EmbeddedChunk> _stored = new();

    public int Calls;

    public Action<IReadOnlyList<EmbeddedChunk>, int>? Hook { get; set; }

    public IReadOnlyCollection<string> StoredIds
    {
        get
        {
            lock (_gate)
            {
                return _stored.Keys.ToList();
            }
        }
    }

    public ValueTask UpsertAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken ct = default)
    {
        var call = Interlocked.Increment(ref Calls);
        Hook?.Invoke(chunks, call);
        lock (_gate)
        {
            foreach (var chunk in chunks)
            {
                _stored[chunk.Id.Value] = chunk;
            }
        }

        return ValueTask.CompletedTask;
    }

    public int DeleteCalls;

    /// <summary>Set by a test to make deletes fail.</summary>
    public Func<IReadOnlyList<ChunkId>, Exception?>? OnDelete { get; set; }

    public ValueTask DeleteAsync(IReadOnlyList<ChunkId> ids, CancellationToken ct = default)
    {
        Interlocked.Increment(ref DeleteCalls);
        if (OnDelete?.Invoke(ids) is { } error)
        {
            throw error;
        }

        lock (_gate)
        {
            foreach (var id in ids)
            {
                _stored.Remove(id.Value);
            }
        }

        return ValueTask.CompletedTask;
    }

    public EmbeddedChunk? Get(string id)
    {
        lock (_gate)
        {
            return _stored.GetValueOrDefault(id);
        }
    }

    public async IAsyncEnumerable<ChunkId> ListIdsAsync(string scope, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        foreach (var id in StoredIds)
        {
            yield return new ChunkId(id);
        }
    }
}

/// <summary>Wraps a store so a test can make single calls fail.</summary>
public sealed class FlakyStore(IFingerprintStore inner) : IFingerprintStore
{
    public Func<ClaimRequest, Exception?>? OnClaim { get; set; }

    public Func<CommitRequest, Exception?>? OnCommit { get; set; }

    public IExactHashIndex Exact => inner.Exact;

    public INearDuplicateIndex SimHash => inner.SimHash;

    public IMinHashIndex MinHash => inner.MinHash;

    public ValueTask<ClaimResult> ClaimAsync(ClaimRequest request, CancellationToken ct = default) =>
        OnClaim?.Invoke(request) is { } error ? throw error : inner.ClaimAsync(request, ct);

    public ValueTask<CommitStatus> CommitAsync(CommitRequest request, CancellationToken ct = default) =>
        OnCommit?.Invoke(request) is { } error ? throw error : inner.CommitAsync(request, ct);

    public ValueTask<bool> ReleaseAsync(ReleaseRequest request, CancellationToken ct = default) => inner.ReleaseAsync(request, ct);

    public ValueTask<int> ForgetAsync(
        string scope, ChunkId owner, IReadOnlyList<ReadOnlyMemory<byte>> exactHashes, CancellationToken ct = default) =>
        inner.ForgetAsync(scope, owner, exactHashes, ct);
}
