using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.VectorData;

namespace Rechunk.Extensions.VectorData;

/// <summary>
/// How Rechunk talks to your record type. Rechunk does not know your schema, so you say how an embedded chunk becomes
/// a record and how a chunk id becomes a key.
/// </summary>
public sealed class VectorDataMapping<TKey, TRecord>
    where TKey : notnull
    where TRecord : class
{
    /// <summary>Builds the record to store. Fill in the key with ToKey, so a repeated upsert replaces the same record.</summary>
    public required Func<EmbeddedChunk, TRecord> ToRecord { get; init; }

    /// <summary>
    /// The key of the record for a scope and a chunk id. Chunk ids come from the text, so the same text in two scopes has
    /// the same id. Use ScopedKey, or your own key that includes the scope, so a delete in one scope never touches another.
    /// </summary>
    public required Func<string, ChunkId, TKey> ToKey { get; init; }

    /// <summary>
    /// A filter that matches the records of one scope, and the way back from a record to its chunk id. Both are needed for
    /// ListIdsAsync, which the verify step uses. Leave them out if you never verify.
    /// </summary>
    public Func<string, Expression<Func<TRecord, bool>>>? ScopeFilter { get; init; }

    public Func<TRecord, ChunkId>? ToId { get; init; }

    /// <summary>A string key of the scope, a colon and the chunk id. Two scopes never share a record.</summary>
    public static string ScopedKey(string scope, ChunkId id) => scope + ":" + id.Value;
}

/// <summary>
/// Writes to any Microsoft.Extensions.VectorData collection. Upserts and deletes go in batches, and a batch that fails is
/// tried again as a whole, which is safe because an upsert with a fixed key replaces and a delete of a missing key is
/// ignored by the collection.
/// </summary>
public sealed class VectorDataSink<TKey, TRecord> : IVectorSink
    where TKey : notnull
    where TRecord : class
{
    private readonly VectorStoreCollection<TKey, TRecord> _collection;
    private readonly VectorDataMapping<TKey, TRecord> _mapping;
    private readonly int _batchSize;
    private readonly int _attempts;
    private readonly TimeSpan _retryDelay;

    public VectorDataSink(
        VectorStoreCollection<TKey, TRecord> collection,
        VectorDataMapping<TKey, TRecord> mapping,
        int batchSize = 100,
        int attempts = 3,
        TimeSpan? retryDelay = null)
    {
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        _mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
        var sample = new ChunkId("x");
        if (EqualityComparer<TKey>.Default.Equals(_mapping.ToKey("a", sample), _mapping.ToKey("b", sample)))
        {
            throw new ArgumentException(
                "The key must include the scope. ToKey gave one key for two different scopes, so a delete in one scope would remove the other scope's vector.",
                nameof(mapping));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        _batchSize = batchSize;
        _attempts = attempts;
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(200);
    }

    public async ValueTask UpsertAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        foreach (var batch in chunks.Chunk(_batchSize))
        {
            var records = batch.Select(_mapping.ToRecord).ToList();
            await WithRetryAsync(token => new ValueTask(_collection.UpsertAsync(records, token)), ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DeleteAsync(string scope, IReadOnlyList<ChunkId> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(ids);
        foreach (var batch in ids.Chunk(_batchSize))
        {
            var keys = batch.Select(id => _mapping.ToKey(scope, id)).ToList();
            await WithRetryAsync(token => new ValueTask(_collection.DeleteAsync(keys, token)), ct).ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<ChunkId> ListIdsAsync(string scope, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (_mapping.ScopeFilter is null || _mapping.ToId is null)
        {
            throw new NotSupportedException(
                "Listing ids needs ScopeFilter and ToId in the mapping. Add them, or do not call the verify step.");
        }

        var filter = _mapping.ScopeFilter(scope);
        await foreach (var record in _collection.GetAsync(filter, int.MaxValue, null, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            yield return _mapping.ToId(record);
        }
    }

    private async ValueTask WithRetryAsync(Func<CancellationToken, ValueTask> work, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await work(ct).ConfigureAwait(false);
                return;
            }
            catch (Exception) when (attempt < _attempts && !ct.IsCancellationRequested)
            {
                if (_retryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_retryDelay * attempt, ct).ConfigureAwait(false);
                }
            }
        }
    }
}
