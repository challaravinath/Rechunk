using Microsoft.Extensions.Logging;
using Rechunk;

namespace Sample.AzureFunction;

/// <summary>Gives every text a tiny made up vector. Swap it for a real embedding model.</summary>
public sealed class DemoEmbedder : IChunkEmbedder
{
    public string ModelId => "demo";

    public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        IReadOnlyList<ReadOnlyMemory<float>> vectors = texts
            .Select(t => (ReadOnlyMemory<float>)new float[] { t.Length, t.Count(char.IsWhiteSpace) })
            .ToList();
        return ValueTask.FromResult(vectors);
    }
}

/// <summary>Logs what it would write and keeps the rows in memory, keyed by scope and id. Swap it for your vector store.</summary>
public sealed class DemoVectorSink(ILogger<DemoVectorSink> logger) : IVectorSink
{
    private readonly Dictionary<string, EmbeddedChunk> _rows = new();

    public ValueTask UpsertAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken ct = default)
    {
        foreach (var chunk in chunks)
        {
            _rows[Key(chunk.Scope, chunk.Id)] = chunk;
            logger.LogInformation("Would upsert chunk {Id} of document {Document} in scope {Scope}.", chunk.Id.Value, chunk.Document.Value, chunk.Scope);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string scope, IReadOnlyList<ChunkId> ids, CancellationToken ct = default)
    {
        foreach (var id in ids)
        {
            _rows.Remove(Key(scope, id));
            logger.LogInformation("Would delete chunk {Id} in scope {Scope}.", id.Value, scope);
        }

        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<ChunkId> ListIdsAsync(string scope, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        foreach (var chunk in _rows.Values.Where(c => c.Scope == scope).ToList())
        {
            yield return chunk.Id;
        }
    }

    private static string Key(string? scope, ChunkId id) => (scope ?? "") + ":" + id.Value;
}
