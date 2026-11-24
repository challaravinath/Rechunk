namespace Rechunk;

/// <summary>
/// Links from a stored chunk to the people it is about. The subject id is a keyed id. The value it came from is not stored.
/// A scope with no key provider does not write this index.
/// </summary>
public interface ISubjectIndex
{
    /// <summary>Replaces the subjects linked to this chunk. An empty list removes them.</summary>
    ValueTask LinkAsync(string scope, ChunkId chunk, IReadOnlyList<SubjectKey> subjects, CancellationToken ct = default);

    /// <summary>Adds subjects to this chunk and leaves the ones already there.</summary>
    ValueTask AddAsync(string scope, ChunkId chunk, IReadOnlyList<SubjectKey> subjects, CancellationToken ct = default);

    /// <summary>Removes every subject link for this chunk.</summary>
    ValueTask RemoveAsync(string scope, ChunkId chunk, CancellationToken ct = default);

    /// <summary>How many subject links this chunk has, of every kind.</summary>
    ValueTask<int> CountLinksAsync(string scope, ChunkId chunk, CancellationToken ct = default);

    /// <summary>Chunk ids linked to this subject, in id order.</summary>
    ValueTask<IReadOnlyList<ChunkId>> ListChunksAsync(string scope, SubjectKey subject, CancellationToken ct = default);

    /// <summary>How many subject links this scope has.</summary>
    ValueTask<int> CountAsync(string scope, CancellationToken ct = default);

    /// <summary>How many chunks are linked to this subject.</summary>
    ValueTask<int> CountAsync(string scope, SubjectKey subject, CancellationToken ct = default);
}
