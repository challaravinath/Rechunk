namespace Rechunk;

/// <summary>
/// One chunk of a document revision. Id is the id of the text as it appears in the document. Target is the chunk whose
/// vector stands for it: the same as Id, unless the text was close enough to another chunk to be stored as an alias of it.
/// FirstRevision is the revision of the document in which this chunk first showed up, and stays the same while the chunk
/// stays. PreviousOrdinal is the position it had in the revision before (null for a new chunk), so a paragraph that moved
/// can be followed. Replaces is the id of the chunk that was at the same position before and is gone now (an edit).
/// </summary>
public sealed record ManifestEntry(
    int Ordinal,
    ChunkId Id,
    ChunkId Target,
    string? Language,
    int FirstRevision,
    int? PreviousOrdinal = null,
    ChunkId? Replaces = null);

/// <summary>The chunks of a document as of one revision. A deleted document keeps a manifest with no entries.</summary>
public sealed record DocumentManifest(
    string Scope,
    DocumentId Document,
    int Revision,
    IReadOnlyList<ManifestEntry> Entries,
    DateTimeOffset UpdatedAt,
    string? Source = null)
{
    public bool IsDeleted => Entries.Count == 0;
}

/// <summary>
/// Moves a document to the next revision. ExpectedRevision is the revision the caller based its plan on (0 for a document
/// that has no manifest yet). Empty entries delete the document. Source names where the document comes from. Null keeps
/// the source the document has.
/// </summary>
public sealed record CommitRevisionRequest(
    string Scope,
    DocumentId Document,
    int ExpectedRevision,
    IReadOnlyList<ManifestEntry> Entries,
    DateTimeOffset At,
    string? Source = null);

/// <summary>A document in the revision store: its revision and where it came from (null when it was never given a source).</summary>
public sealed record DocumentRef(DocumentId Document, int Revision, string? Source);

public enum CommitRevisionStatus
{
    Committed,

    /// <summary>The document is not at ExpectedRevision any more. Nothing changed. Plan again.</summary>
    Conflict,

    /// <summary>
    /// The new revision uses a chunk that is being deleted right now. Nothing changed. Let the delete finish, then
    /// plan again: the chunk is stored from scratch.
    /// </summary>
    Blocked,
}

/// <summary>
/// Revision is the document's revision after the call (or the current one when nothing changed). Orphaned lists the
/// chunks that no document uses any more, they are waiting to be swept. Blocking lists the chunks behind a Blocked answer.
/// </summary>
public sealed record CommitRevisionResult(
    CommitRevisionStatus Status,
    int Revision,
    IReadOnlyList<ChunkId> Orphaned,
    IReadOnlyList<ChunkId> Blocking);

public enum ChunkRefState
{
    /// <summary>At least one document uses the chunk.</summary>
    Active,

    /// <summary>No document uses it. It is waiting for a sweep.</summary>
    Orphaned,

    /// <summary>A sweeper owns it and is deleting it.</summary>
    Retiring,
}

/// <summary>How many documents use a chunk (a document counts once however many times the text repeats in it).</summary>
public sealed record ChunkRef(ChunkId Chunk, int Count, ChunkRefState State);

/// <summary>What a sweeper gets when it wins a chunk: the duplicates that were stored as aliases of it.</summary>
public sealed record RetireClaim(ChunkId Chunk, IReadOnlyList<ChunkId> Aliases);

/// <summary>
/// Which document uses which chunk, and how many documents use each chunk. This is what makes a delete clean: a vector is
/// only removed when the last document that uses it lets go, and every change of a document is one atomic step.
///
/// Everything is scoped, and a document revision is compare and swap: two workers that revise the same document at the
/// same time can not both win. A chunk goes Active, then Orphaned when the last user lets go, then Retiring while a sweeper
/// deletes it, then it is gone. A commit that wants a chunk that is Retiring is refused (Blocked), so a chunk can never
/// be reused while its vector is being deleted.
/// </summary>
public interface IRevisionStore
{
    ValueTask<DocumentManifest?> GetAsync(string scope, DocumentId document, CancellationToken ct = default);

    /// <summary>Atomic. Checks the revision, moves the document, and updates the counts of every chunk it uses or stopped using.</summary>
    ValueTask<CommitRevisionResult> CommitAsync(CommitRevisionRequest request, CancellationToken ct = default);

    /// <summary>Chunks nobody uses (Orphaned), and chunks whose sweeper died (Retiring with a lease that ran out).</summary>
    ValueTask<IReadOnlyList<ChunkId>> ListPendingAsync(string scope, DateTimeOffset now, int take, CancellationToken ct = default);

    /// <summary>
    /// Takes a chunk to delete it. Works only when no document uses it, and no other sweeper holds it (or its lease ran
    /// out). Null otherwise. The same holder asking again just extends the lease.
    /// </summary>
    ValueTask<RetireClaim?> BeginRetireAsync(
        string scope, ChunkId chunk, string holder, TimeSpan lease, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Forgets the chunk and its aliases once its vector and fingerprints are gone. Says whether this holder had it.</summary>
    ValueTask<bool> FinishRetireAsync(string scope, ChunkId chunk, string holder, CancellationToken ct = default);

    ValueTask<ChunkRef?> GetRefAsync(string scope, ChunkId chunk, CancellationToken ct = default);

    /// <summary>Every chunk the store knows in this scope, whatever its state.</summary>
    ValueTask<IReadOnlyList<ChunkRef>> ListRefsAsync(string scope, CancellationToken ct = default);

    /// <summary>
    /// The documents of a scope in the order of their ids, after the given one, at most take of them. With a source only
    /// the documents of that source, whether they are deleted or not. Needed to erase by source.
    /// </summary>
    ValueTask<IReadOnlyList<DocumentRef>> ListDocumentsAsync(
        string scope, string? source, string? afterDocument, int take, CancellationToken ct = default) =>
        throw new NotSupportedException("This revision store can not list documents, so it can not erase by source.");

    /// <summary>
    /// The documents whose current revision has an entry with one of these ids, as its own id or as its target, in the
    /// order of their ids, after the given one, at most take of them. Needed to erase chunks that were found by text or finding.
    /// </summary>
    ValueTask<IReadOnlyList<DocumentRef>> ListDocumentsUsingAsync(
        string scope, IReadOnlyCollection<ChunkId> chunks, string? afterDocument, int take, CancellationToken ct = default) =>
        throw new NotSupportedException("This revision store can not look up the documents that use a chunk, so it can not erase chunks.");
}
