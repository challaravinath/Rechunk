namespace Rechunk;

/// <summary>What happened to a chunk or a document. The numbers are stored by the SQLite and Redis logs, do not reorder them.</summary>
public enum AuditKind
{
    /// <summary>A chunk was embedded and stored for the first time.</summary>
    New = 0,

    /// <summary>A duplicate was left out under the Skip policy.</summary>
    Skipped = 1,

    /// <summary>A duplicate was left out and remembered next to the chunk it matched.</summary>
    Aliased = 2,

    /// <summary>A duplicate was embedded anyway and flagged, under the Flag policy.</summary>
    Flagged = 3,

    /// <summary>A revision replaced a chunk with a new one.</summary>
    Revised = 4,

    /// <summary>A chunk was deleted because its document changed or was deleted.</summary>
    Deleted = 5,

    /// <summary>A chunk was erased on request.</summary>
    Erased = 6,

    /// <summary>A chunk could not be processed and went to the dead letters.</summary>
    DeadLettered = 7,

    /// <summary>A chunk was kept out because of a Block finding.</summary>
    Blocked = 8,

    /// <summary>The record of an erase request. It holds counts and who asked, never the erased content.</summary>
    ErasureReceipt = 9,
}

/// <summary>The names used for AuditEvent.Stage.</summary>
public static class AuditStages
{
    public const string Pii = "pii";

    public const string Exact = "exact";

    public const string SimHash = "simhash";

    public const string MinHash = "minhash";

    public const string Dedup = "dedup";

    public const string Embed = "embed";

    public const string VectorWrite = "vector-write";

    public const string Commit = "commit";

    public const string Revision = "revision";

    public const string Erasure = "erasure";
}

/// <summary>
/// One line in the audit trail. It never holds chunk text. Chunk is the id, which is a hash of the normalized text, and
/// Findings give the type and the span of what the PII scan found, not the value. Source is the data source of the
/// document (a folder, a wiki, a connector name), Actor is what caused the event, for example "revision" or the name in
/// an erase request. Id makes an append idempotent: writing the same event twice stores it once.
/// </summary>
public sealed record AuditEvent(
    ChunkId Chunk,
    DocumentId Document,
    int Revision,
    string Source,
    DateTimeOffset At,
    string EmbeddingModel,
    AuditKind Kind,
    string Reason)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>The scope the caller used. Language tags are kept apart in Language.</summary>
    public string Scope { get; init; } = string.Empty;

    public string? Language { get; init; }

    /// <summary>One of <see cref="AuditStages"/>, or a stage name of your own.</summary>
    public string Stage { get; init; } = string.Empty;

    public string Actor { get; init; } = string.Empty;

    /// <summary>The chunk this one duplicates or replaces, when there is one.</summary>
    public ChunkId? Related { get; init; }

    public IReadOnlyList<PiiHit> Findings { get; init; } = Array.Empty<PiiHit>();

    /// <summary>Counts for a receipt, for example documents, chunks, vectors and state_rows. Empty for other events.</summary>
    public IReadOnlyDictionary<string, int> Counts { get; init; } = EmptyCounts;

    /// <summary>The effect of the event on the stored chunk, for code that only cares about that.</summary>
    public ChunkAction Action => Kind switch
    {
        AuditKind.New => ChunkAction.Embed,
        AuditKind.Revised => ChunkAction.Replace,
        AuditKind.Aliased => ChunkAction.Alias,
        AuditKind.Deleted or AuditKind.Erased => ChunkAction.Delete,
        _ => ChunkAction.Keep,
    };

    internal static readonly IReadOnlyDictionary<string, int> EmptyCounts = new Dictionary<string, int>();
}

/// <summary>
/// Which events to return. From and To are both inclusive. Source and Scope compare exactly. FindingType matches events
/// that have a finding of that type. Results come oldest first (to the millisecond), with the event id as the tie break,
/// so Skip and Take page through them in a stable order.
/// </summary>
public sealed record AuditQuery(
    DocumentId? Document = null,
    ChunkId? Chunk = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null)
{
    public string? Source { get; init; }

    public string? Scope { get; init; }

    public AuditKind? Kind { get; init; }

    public PiiType? FindingType { get; init; }

    public int Skip { get; init; }

    public int? Take { get; init; }

    /// <summary>Time is compared to the millisecond, which is what all the logs store.</summary>
    public static long Ms(DateTimeOffset at) => at.ToUnixTimeMilliseconds();

    /// <summary>True when the event passes every filter except Skip and Take.</summary>
    public bool Matches(AuditEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return (Document is null || e.Document == Document)
            && (Chunk is null || e.Chunk == Chunk)
            && (From is null || Ms(e.At) >= Ms(From.Value))
            && (To is null || Ms(e.At) <= Ms(To.Value))
            && (Source is null || e.Source == Source)
            && (Scope is null || e.Scope == Scope)
            && (Kind is null || e.Kind == Kind)
            && (FindingType is null || e.Findings.Any(f => f.Type == FindingType));
    }
}
