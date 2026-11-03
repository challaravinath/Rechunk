using Microsoft.Extensions.VectorData;

namespace Rechunk.Extensions.VectorData;

/// <summary>
/// Rechunk's view of a Microsoft.Extensions.VectorData collection. Not written yet.
/// </summary>
public sealed class VectorDataSink<TKey, TRecord> : IVectorSink
    where TKey : notnull
    where TRecord : class
{
    private readonly VectorStoreCollection<TKey, TRecord> _collection;

    public VectorDataSink(VectorStoreCollection<TKey, TRecord> collection)
    {
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
    }

    // TODO:
    //  - map EmbeddedChunk to TRecord and back (needs a small mapper the caller provides)
    //  - upsert in batches, delete in batches, and retry the whole batch if one fails
    //  - a failed batch delete can be half done, so deletes have to be safe to repeat
    //  - list ids for a scope so the verify scan can compare with the state store

    public ValueTask UpsertAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken ct = default) =>
        throw new NotImplementedException("Vector sink is not written yet.");

    public ValueTask DeleteAsync(IReadOnlyList<ChunkId> ids, CancellationToken ct = default) =>
        throw new NotImplementedException("Vector sink is not written yet.");

    public IAsyncEnumerable<ChunkId> ListIdsAsync(string scope, CancellationToken ct = default) =>
        throw new NotImplementedException("Vector sink is not written yet.");
}
