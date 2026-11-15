namespace Rechunk;

/// <summary>What a migration run should do. DryRun writes nothing and does not block the scope.</summary>
public sealed class MigrationRun
{
    public bool DryRun { get; init; }

    /// <summary>Called with each old id and the keyed id that replaces it. Not called again for a row that was already saved.</summary>
    public Action<string, string>? OnMapped { get; init; }

    /// <summary>
    /// Called after a fingerprint row is saved, after a revision document is rewritten, and once before the scope version flips.
    /// Throw to stop. A later run continues from the saved rows.
    /// </summary>
    public Action<MigrationStep>? OnStep { get; init; }
}

/// <summary>Phase is fingerprint, revision, or complete. Count is how many of that phase this run has finished.</summary>
public readonly record struct MigrationStep(string Phase, int Count);

/// <summary>Counts from one run. AlreadyDone means the scope was already on hash version 2 and nothing was changed.</summary>
public sealed record MigrationReport(
    bool DryRun,
    bool Completed,
    bool AlreadyDone,
    int FingerprintRows,
    int BandEntries,
    int RevisionReferences,
    int Mappings);

public readonly record struct IdPair(string OldId, string NewId);

/// <summary>A fingerprint store that can rewrite version 1 rows to version 2.</summary>
public interface IMigratableFingerprints
{
    ValueTask<MigrationPreview> PreviewAsync(string userScope, ScopeKey key, RechunkOptions options, CancellationToken ct = default);

    ValueTask BeginAsync(string userScope, CancellationToken ct = default);

    ValueTask<FingerprintMove> MoveNextAsync(string userScope, ScopeKey key, RechunkOptions options, CancellationToken ct = default);

    ValueTask<IReadOnlyDictionary<string, string>> ReadMapAsync(string userScope, CancellationToken ct = default);

    ValueTask CompleteAsync(string userScope, int keyVersion, CancellationToken ct = default);
}

public sealed record MigrationPreview(
    bool AlreadyKeyed,
    bool Migrating,
    int LegacyRows,
    int BandEntries,
    IReadOnlyList<IdPair> Pairs);

public readonly record struct FingerprintMove(bool Moved, string OldId, string NewId, string? AlsoOldId, string? AlsoNewId, bool Band);

/// <summary>A revision store that can replace chunk ids and refuse commits while a migration is running.</summary>
public interface IMigratableRevisions
{
    ValueTask SetBusyAsync(string userScope, bool busy, CancellationToken ct = default);

    ValueTask<int> CountOldIdsAsync(string userScope, IReadOnlyDictionary<string, string> map, CancellationToken ct = default);

    /// <summary>Rewrites one document, or returns -1 when none of the remaining documents contain an old id.</summary>
    ValueTask<int> RewriteNextAsync(string userScope, IReadOnlyDictionary<string, string> map, CancellationToken ct = default);
}
