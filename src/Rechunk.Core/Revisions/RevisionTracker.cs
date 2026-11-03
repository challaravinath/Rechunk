namespace Rechunk;

/// <summary>Revision tracking and clean deletes. Not written yet.</summary>
public sealed class RevisionTracker : IRevisionTracker
{
    // TODO:
    //  - compare old and new chunk lists, decide Keep, Embed, Replace or Delete per chunk
    //  - write the new vectors first, then log the delete, then delete the old ones
    //  - reference counts for chunks that are shared between documents
    //  - a verify scan that finds orphans
    //  - fault injection tests that kill the store half way through a delete

    public ValueTask<RevisionPlan> PlanAsync(
        DocumentId document, string scope, IReadOnlyList<ChunkInput> newChunks, CancellationToken ct = default) =>
        throw new NotImplementedException("Revision tracking is not written yet.");

    public ValueTask<RevisionResult> ApplyAsync(
        RevisionPlan plan, IChunkEmbedder embedder, IVectorSink sink, CancellationToken ct = default) =>
        throw new NotImplementedException("Revision tracking is not written yet.");

    public ValueTask<EraseResult> EraseAsync(EraseRequest request, IVectorSink sink, CancellationToken ct = default) =>
        throw new NotImplementedException("Erasure is not written yet.");

    public ValueTask<VerifyReport> VerifyAsync(string scope, IVectorSink sink, CancellationToken ct = default) =>
        throw new NotImplementedException("Verify is not written yet.");
}
