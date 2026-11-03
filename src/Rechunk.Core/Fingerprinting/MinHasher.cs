namespace Rechunk;

/// <summary>MinHash signature of a chunk. Not written yet.</summary>
public sealed class MinHasher
{
    public MinHasher(MinHashOptions options)
    {
        Options = options;
    }

    public MinHashOptions Options { get; }

    public ReadOnlyMemory<uint> Compute(ReadOnlySpan<char> normalizedText)
    {
        // TODO:
        //  - word shingles of Options.ShingleSize
        //  - Options.Permutations hash functions with fixed, stored seeds
        //  - check the estimate against exact Jaccard on a labelled set of pairs
        //  - cross check against MinHashSharp in the tests
        throw new NotImplementedException("MinHash is not written yet.");
    }
}
