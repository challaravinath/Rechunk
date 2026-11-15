namespace Rechunk;

/// <summary>
/// Rewrites one scope from digest ids (hash version 1) to keyed ids (hash version 2).
/// Nothing runs unless you call <see cref="RunAsync"/>. A dry run only counts and reports the id mapping.
///
/// The scope key is loaded first. If that fails, nothing is marked and the version is not changed.
/// After the key loads, the scope refuses fingerprint writes and revision commits until the run finishes.
/// The version stays 1 while rows are copied, then one step sets it to 2 and clears the block.
/// A later run finds version 2 and does nothing.
///
/// Rows are saved one at a time. Stop in <see cref="MigrationRun.OnStep"/> and call RunAsync again with the same
/// stores. Rows already at version 2 are skipped. Revision ids that were already replaced are skipped.
/// The id map is kept in the fingerprint store so the vector ids can be applied again from <see cref="MigrationRun.OnMapped"/>
/// only for pairs saved in that call.
///
/// Pass the same <see cref="RechunkOptions"/> the fingerprints were built with, so SimHash and MinHash band
/// counts match. A keyed signature payload is encrypted. The exact digest stays as the lookup key. Audit rows and dead letters
/// keep the old ids. Rechunk does not update your vector store. Use the mapping callback for that.
/// Language partitions of the scope (the scope plus the language separator) are included. The key is the one
/// for the scope you pass, not a separate key per language.
/// </summary>
public sealed class KeyedIdMigrator
{
    private readonly IMigratableFingerprints _fingerprints;
    private readonly IMigratableRevisions? _revisions;
    private readonly IKeyProvider _keys;
    private readonly RechunkOptions _options;

    public KeyedIdMigrator(
        IMigratableFingerprints fingerprints,
        IKeyProvider keys,
        RechunkOptions? options = null,
        IMigratableRevisions? revisions = null)
    {
        _fingerprints = fingerprints ?? throw new ArgumentNullException(nameof(fingerprints));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _options = options ?? new RechunkOptions();
        _revisions = revisions;
    }

    public async ValueTask<MigrationReport> RunAsync(string scope, MigrationRun? run = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        run ??= new MigrationRun();
        var key = await _keys.GetRequiredAsync(scope, ct).ConfigureAwait(false);
        var preview = await _fingerprints.PreviewAsync(scope, key, _options, ct).ConfigureAwait(false);
        if (preview.AlreadyKeyed && !preview.Migrating)
        {
            return new MigrationReport(run.DryRun, true, true, 0, 0, 0, 0);
        }

        if (run.DryRun)
        {
            foreach (var pair in preview.Pairs)
            {
                run.OnMapped?.Invoke(pair.OldId, pair.NewId);
            }

            var plannedRevisions = _revisions is null
                ? 0
                : await _revisions.CountOldIdsAsync(scope, ToMap(preview.Pairs), ct).ConfigureAwait(false);
            return new MigrationReport(true, false, false, preview.LegacyRows, preview.BandEntries, plannedRevisions, preview.Pairs.Count);
        }

        await _fingerprints.BeginAsync(scope, ct).ConfigureAwait(false);
        if (_revisions is not null)
        {
            await _revisions.SetBusyAsync(scope, true, ct).ConfigureAwait(false);
        }

        var rows = 0;
        var bands = 0;
        var mappings = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var move = await _fingerprints.MoveNextAsync(scope, key, _options, ct).ConfigureAwait(false);
            if (!move.Moved)
            {
                break;
            }

            rows++;
            if (move.Band)
            {
                bands++;
            }

            run.OnMapped?.Invoke(move.OldId, move.NewId);
            mappings++;
            if (move.AlsoOldId is not null && move.AlsoNewId is not null && move.AlsoOldId != move.OldId)
            {
                run.OnMapped?.Invoke(move.AlsoOldId, move.AlsoNewId);
                mappings++;
            }

            run.OnStep?.Invoke(new MigrationStep("fingerprint", rows));
        }

        var revisionRefs = 0;
        if (_revisions is not null)
        {
            var map = await _fingerprints.ReadMapAsync(scope, ct).ConfigureAwait(false);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var changed = await _revisions.RewriteNextAsync(scope, map, ct).ConfigureAwait(false);
                if (changed < 0)
                {
                    break;
                }

                revisionRefs += changed;
                run.OnStep?.Invoke(new MigrationStep("revision", revisionRefs));
            }
        }

        run.OnStep?.Invoke(new MigrationStep("complete", rows));
        await _fingerprints.CompleteAsync(scope, key.Version, ct).ConfigureAwait(false);
        if (_revisions is not null)
        {
            await _revisions.SetBusyAsync(scope, false, ct).ConfigureAwait(false);
        }

        return new MigrationReport(false, true, false, rows, bands, revisionRefs, mappings);
    }

    private static Dictionary<string, string> ToMap(IReadOnlyList<IdPair> pairs)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            map[pair.OldId] = pair.NewId;
        }

        return map;
    }
}
