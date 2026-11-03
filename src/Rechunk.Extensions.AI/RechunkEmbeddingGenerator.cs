using Microsoft.Extensions.AI;

namespace Rechunk.Extensions.AI;

/// <summary>
/// Wraps an embedding generator so text that was already embedded is not sent again.
/// Right now it only passes everything through to the inner generator.
/// </summary>
public sealed class RechunkEmbeddingGenerator : DelegatingEmbeddingGenerator<string, Embedding<float>>
{
    public RechunkEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> innerGenerator)
        : base(innerGenerator)
    {
    }

    // TODO:
    //  - look up each input by exact hash and only send the new ones to the inner generator
    //  - put the results back in the original order
    //  - an adapter from IEmbeddingGenerator to Rechunk's IChunkEmbedder
}
