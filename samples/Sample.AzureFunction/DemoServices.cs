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

/// <summary>Only logs what it would write. Swap it for your vector store.</summary>
public sealed class DemoVectorSink(ILogger<DemoVectorSink> logger) : IVectorSink
{
    public ValueTask UpsertAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken ct = default)
    {
        foreach (var chunk in chunks)
        {
            logger.LogInformation("Would upsert chunk {Id} of document {Document}.", chunk.Id.Value, chunk.Document.Value);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(IReadOnlyList<ChunkId> ids, CancellationToken ct = default) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<ChunkId> ListIdsAsync(string scope, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
