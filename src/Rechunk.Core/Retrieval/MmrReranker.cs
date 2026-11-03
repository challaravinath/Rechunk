namespace Rechunk;

public sealed record MmrCandidate(ChunkId Id, ReadOnlyMemory<float> Vector, double Score);

/// <summary>MMR style diversity for search results. Not written yet.</summary>
public static class MmrReranker
{
    public static IReadOnlyList<MmrCandidate> Rerank(
        ReadOnlyMemory<float> queryVector, IReadOnlyList<MmrCandidate> candidates, double lambda, int take)
    {
        // TODO:
        //  - lambda 1 has to give the plain similarity order
        //  - lower lambda pushes near copies down
        throw new NotImplementedException("MMR is not written yet.");
    }
}
