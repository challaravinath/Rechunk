namespace Rechunk;

/// <summary>64 bit SimHash of a chunk. Not written yet.</summary>
public sealed class SimHasher
{
    public ulong Compute(ReadOnlySpan<char> normalizedText)
    {
        // TODO:
        //  - split into word features, hash each one with a stable 64 bit hash
        //  - add up the bits and keep the sign of each column
        //  - golden file tests so the output never changes between versions
        //  - cross check against the SimhashLib package in the tests
        throw new NotImplementedException("SimHash is not written yet.");
    }
}
