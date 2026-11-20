using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rechunk;

/// <summary>
/// Runs chunks through the dedup stages. A chunk that matches in an earlier stage never reaches the later ones.
///
///  Stage 1, exact hash: always on. Catches the same text after cleanup.
///  Stage 2, SimHash: quick and rough. Finds chunks that are only a few bits away.
///  Stage 3, MinHash with LSH: finds chunks whose word shingles overlap by at least the threshold.
///
/// When both near stages are on, MinHash does two jobs. It confirms what SimHash found (unless
/// SimHash.ConfirmWithMinHash is false), and it catches looser duplicates that SimHash missed.
/// The number guard applies to both, and so does the identifier guard: a pair whose only difference
/// is a name or a snippet id is not merged. Chunks that are too short, or mostly code or markup,
/// never reach these stages. The exact stage still sees them.
///
/// A chunk that is skipped as a near duplicate is not added to the near indexes. The chunk it matched
/// stands for it from then on, and its exact hash is pointed at that chunk. This keeps the indexes small
/// but also means a chain of small edits (A, then B close to A, then C close to B but not to A) is not followed.
/// </summary>
public sealed partial class RechunkPipeline
{
    private readonly RechunkOptions _options;
    private readonly IChunkFingerprinter _fingerprinter;
    private readonly IExactHashIndex _exactIndex;
    private readonly INearDuplicateIndex? _simHashIndex;
    private readonly IMinHashIndex? _minHashIndex;
    private readonly IFingerprintStore? _store;
    private readonly IKeyProvider? _keys;
    private readonly ISubjectIndex? _subjects;
    private readonly IRevisionStore? _revisions;
    private readonly AuditWriter? _audit;
    private readonly BandKeyDirectory? _bands;
    private readonly ILogger _logger;
    private readonly PiiScanner _pii;

    public RechunkPipeline(
        RechunkOptions options,
        IChunkFingerprinter fingerprinter,
        IExactHashIndex exactIndex,
        ILogger? logger = null,
        INearDuplicateIndex? simHashIndex = null,
        IMinHashIndex? minHashIndex = null,
        IFingerprintStore? store = null,
        IKeyProvider? keys = null,
        ISubjectIndex? subjects = null,
        IRevisionStore? revisions = null,
        AuditWriter? audit = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _fingerprinter = fingerprinter ?? throw new ArgumentNullException(nameof(fingerprinter));
        _exactIndex = exactIndex ?? throw new ArgumentNullException(nameof(exactIndex));
        _logger = logger ?? NullLogger.Instance;
        _store = store;
        _keys = keys ?? options.KeyProvider;
        _subjects = subjects;
        _revisions = revisions;
        _audit = audit;
        _bands = _keys is null ? null : BandKeys.For(_keys);
        options.Validate();
        _pii = new PiiScanner(options.Pii);

        if (options.SimHash.Enabled)
        {
            _simHashIndex = simHashIndex ?? store?.SimHash ?? new SimHashBandIndex(options.SimHash.MaxHammingDistance, _bands);
        }

        if (options.MinHash.Enabled)
        {
            _minHashIndex = minHashIndex ?? store?.MinHash ?? new MinHashLshIndex(options.MinHash, _bands);
        }
    }

    public RechunkOptions Options => _options;

    /// <summary>The shared fingerprint store, or null when the pipeline only uses its own indexes.</summary>
    public IFingerprintStore? Store => _store;

    /// <summary>Subject links, or null when this pipeline was built without an index.</summary>
    internal ISubjectIndex? Subjects => _subjects;

    public static RechunkPipelineBuilder CreateBuilder() => new();

    /// <summary>The id a chunk with this text gets. Same text after cleanup, same id. Uses <see cref="RechunkOptions.DefaultScope"/> when a key provider is set.</summary>
    public ChunkId IdFor(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return IdFor(_options.DefaultScope, text, null);
    }

    /// <summary>The id a chunk gets, with its language. Same text and same language, same id.</summary>
    public ChunkId IdFor(ChunkInput chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return IdFor(_options.DefaultScope, chunk.Text, chunk.Language);
    }

    /// <summary>
    /// The id for this scope. With a key provider the key has to be loaded already, or this throws <see cref="KeyUnavailableException"/>.
    /// With no provider the id is the hex digest, as in 0.1.
    /// </summary>
    public ChunkId IdFor(string scope, string text, string? language = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(text);
        var key = CachedKey(scope);
        var tag = _options.Language.Resolve(language, text);
        return IdFrom(_fingerprinter.Compute(text).ExactHash.Span, tag, key);
    }

    /// <summary>
    /// The id a chunk gets when it goes through the pipeline. With PII scanning on, the id comes from the masked text, so
    /// use this and not <see cref="IdFor(ChunkInput)"/> to find a chunk that was stored.
    /// </summary>
    public ValueTask<ChunkId> IdForAsync(ChunkInput chunk, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return IdForAsync(_options.DefaultScope, chunk, ct);
    }

    /// <summary>The id the pipeline will store for this chunk in this scope, after the same scan the write path uses.</summary>
    public async ValueTask<ChunkId> IdForAsync(string scope, ChunkInput chunk, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var key = await KeyForAsync(scope, ct).ConfigureAwait(false);
        var (_, id, _) = await IdentifyAsync(chunk, key, ct).ConfigureAwait(false);
        return id;
    }

    // Scans the chunk if that is on, then works out its id and language from what is left. Returns the scanned chunk too.
    internal async ValueTask<(ChunkInput Chunk, ChunkId Id, string? Language)> IdentifyAsync(
        ChunkInput chunk, ScopeKey? key, CancellationToken ct = default)
    {
        chunk = (await PrepareAsync(new[] { chunk }, ct).ConfigureAwait(false))[0];
        var (id, language) = IdentifyPrepared(chunk, key);
        return (chunk, id, language);
    }

    // The id and language of a chunk that was scanned already, or that needs no scan.
    internal (ChunkId Id, string? Language) IdentifyPrepared(ChunkInput chunk, ScopeKey? key = null)
    {
        var language = _options.Language.Resolve(chunk.Language, chunk.Text);
        return (IdFrom(_fingerprinter.Compute(chunk.Text).ExactHash.Span, language, key), language);
    }

    /// <summary>The SHA-256 of the cleaned text. This is still the exact-row key. The keyed id is not reversible to it.</summary>
    internal byte[] Digest(string text) => _fingerprinter.Compute(text).ExactHash.ToArray();

    /// <summary>Id, digest and language of the text the write path would store, after the same scan.</summary>
    internal async ValueTask<(ChunkId Id, byte[] Digest, string? Language)> DescribeAsync(
        string scope, ChunkInput chunk, CancellationToken ct)
    {
        var key = await KeyForAsync(scope, ct).ConfigureAwait(false);
        var (prepared, id, language) = await IdentifyAsync(chunk, key, ct).ConfigureAwait(false);
        if (prepared.Scan?.Blocked == true)
        {
            return (id, Array.Empty<byte>(), language);
        }

        return (id, Digest(prepared.Text), language);
    }

    internal async ValueTask<ScopeKey?> KeyForAsync(string scope, CancellationToken ct)
    {
        if (_keys is null)
        {
            return null;
        }

        var key = await _keys.GetRequiredAsync(scope, ct).ConfigureAwait(false);
        _bands?.Warm(scope, key);
        return key;
    }

    /// <summary>
    /// Runs the PII scan on a batch and returns the chunks as the pipeline will see them: masked where the policy says so,
    /// with PiiFindings set. Nothing is stored or remembered. Does nothing when scanning is off.
    /// </summary>
    public async ValueTask<IReadOnlyList<ChunkInput>> ScanAsync(IReadOnlyList<ChunkInput> chunks, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        return await PrepareAsync(chunks, ct).ConfigureAwait(false);
    }

    internal async ValueTask<IReadOnlyList<ChunkInput>> PrepareAsync(IReadOnlyList<ChunkInput> chunks, CancellationToken ct)
    {
        if (!_pii.Enabled)
        {
            return chunks;
        }

        return await _pii.ScanAsync(chunks, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Looks for stored chunks that this text matches, without claiming or changing anything. The first result is the
    /// exact match when there is one (Existing is the chunk the text points at, which is another chunk when the text was
    /// an alias). With includeNear, chunks that are close by SimHash or MinHash follow. The text is masked the same way
    /// as on the way in, so a text with a secret in it finds the chunk that was stored without it.
    /// </summary>
    public async ValueTask<IReadOnlyList<StoredMatch>> FindStoredAsync(
        string? scope, ChunkInput chunk, bool includeNear = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        scope ??= _options.DefaultScope;
        var key = await KeyForAsync(scope, ct).ConfigureAwait(false);
        var (prepared, id, language) = await IdentifyAsync(chunk, key, ct).ConfigureAwait(false);
        var matches = new List<StoredMatch>();
        if (prepared.Scan?.Blocked == true)
        {
            return matches;
        }

        var effective = LanguageTags.ScopeFor(scope, language);
        var fingerprint = _fingerprinter.Compute(prepared.Text);
        var exact = await _exactIndex.FindAsync(effective, fingerprint.ExactHash, ct).ConfigureAwait(false);
        if (exact is { } owner)
        {
            matches.Add(new StoredMatch(id, owner, MatchStage.Exact, null, null, language));
        }

        if (includeNear)
        {
            var near = await FindNearAsync(effective, fingerprint, null, null, prepared, null, ct).ConfigureAwait(false);
            if (near is not null && !(exact is { } o && o == near.Existing))
            {
                matches.Add(new StoredMatch(id, near.Existing, near.Stage, near.HammingDistance, near.EstimatedJaccard, language));
            }
        }

        return matches;
    }

    /// <summary>The language the chunk is matched under: its own tag, else the detector, else null.</summary>
    public string? LanguageFor(ChunkInput chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return _options.Language.Resolve(chunk.Language, chunk.Text);
    }

    /// <summary>Looks at a batch of chunks and says which ones are new and which ones are repeats.</summary>
    public ValueTask<DedupReport> DeduplicateAsync(
        string? scope, IReadOnlyList<ChunkInput> chunks, CancellationToken ct = default) =>
        DeduplicateCoreAsync(scope, chunks, null, ct);

    /// <summary>
    /// Same, for a pipeline with a shared store. New chunks are claimed (pending) for the holder instead of being
    /// remembered right away. Commit the claims in the report once the vectors are written, release them if it failed.
    /// A holder that asks again for the same chunk (a retry) gets the claim back.
    /// </summary>
    public ValueTask<DedupReport> DeduplicateAsync(
        string? scope, IReadOnlyList<ChunkInput> chunks, DedupClaimOptions claim, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return DeduplicateCoreAsync(scope, chunks, claim, ct);
    }

    private async ValueTask<DedupReport> DeduplicateCoreAsync(
        string? userScope, IReadOnlyList<ChunkInput> chunks, DedupClaimOptions? claimOptions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        userScope ??= _options.DefaultScope;
        var scopeKey = await KeyForAsync(userScope, ct).ConfigureAwait(false);
        var hashVersion = scopeKey is null ? ChunkIds.LegacyHashVersion : ChunkIds.HmacHashVersion;
        var keyVersion = scopeKey?.Version ?? 0;
        chunks = await PrepareAsync(chunks, ct).ConfigureAwait(false);
        var excluded = claimOptions?.NearExclude;
        var languages = new List<string?>(chunks.Count);
        var holder = claimOptions?.Holder ?? "run-" + Guid.NewGuid().ToString("N");
        var lease = claimOptions?.Lease ?? _options.Store.LeaseDuration;

        // In a dry run nothing is written to the indexes, so repeats inside the batch are tracked here.
        var dryRunExact = new Dictionary<string, ChunkId>();
        // With a shared store the same overlays hold the chunks claimed in this batch, because the store only knows
        // about chunks that are committed.
        var overlays = _options.DryRun || _store is not null;
        var batchIds = new HashSet<string>();
        var batchEmbedded = new HashSet<string>();
        var dryRunSimHash = overlays && _simHashIndex is not null
            ? new SimHashBandIndex(_options.SimHash.MaxHammingDistance, _bands)
            : null;
        var dryRunMinHash = overlays && _minHashIndex is not null
            ? new MinHashLshIndex(_options.MinHash, _bands)
            : null;

        var skipNearDuplicates = _options.OnDuplicate != DuplicatePolicy.Flag;
        var decisions = new List<DedupDecision>(chunks.Count);

        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();

            // every stage and every claim works inside the scope of the chunk's language, so chunks of different
            // languages can not match each other
            var language = _options.Language.Resolve(chunk.Language, chunk.Text);
            var scope = LanguageTags.ScopeFor(userScope, language);
            languages.Add(language);

            if (chunk.Scan?.Blocked == true)
            {
                // never fingerprinted, claimed or stored. What is left of the chunk is already masked.
                decisions.Add(new DedupDecision(chunk, new ChunkId(string.Empty), DedupOutcome.Blocked, null, null));
                _logger.LogDebug("Chunk {Ordinal} was blocked by the PII policy ({Count} findings)", chunk.Ordinal, chunk.Scan.Hits.Count);
                continue;
            }

            var fingerprint = _fingerprinter.Compute(chunk.Text);
            var id = IdFrom(fingerprint.ExactHash.Span, language, scopeKey);

            // Stage 1: have we seen exactly this text?
            var exact = await _exactIndex.FindAsync(scope, fingerprint.ExactHash, ct).ConfigureAwait(false);
            if (exact is null && dryRunExact.TryGetValue(id.Value, out var earlier))
            {
                exact = earlier;
            }

            if (exact is { } owner)
            {
                decisions.Add(Repeat(chunk, id, MatchStage.Exact, owner, null, null));
                LogMatch(chunk, MatchStage.Exact, owner, null, null);
                continue;
            }

            if (_store is not null && !_options.DryRun && batchIds.Contains(id.Value))
            {
                // the same text twice in one batch, the first one is handling it
                decisions.Add(Repeat(chunk, id, MatchStage.Exact, id, null, null));
                LogMatch(chunk, MatchStage.Exact, id, null, null);
                continue;
            }

            ChunkClaim? claim = null;

            // Stages 2 and 3: have we seen something very close to it?
            var near = await FindNearAsync(scope, fingerprint, dryRunSimHash, dryRunMinHash, chunk, excluded, ct).ConfigureAwait(false);

            // A near duplicate that is not embedded stands for the canonical chunk from now on. A flagged one is
            // embedded like any new chunk, so it is stored as itself.
            var storedAs = near is not null && skipNearDuplicates ? near.Existing : id;
            var embedded = near is null || !skipNearDuplicates;

            if (_options.DryRun)
            {
                dryRunExact[id.Value] = storedAs;
                if (embedded)
                {
                    AddToNearIndexes(id, scope, fingerprint, dryRunSimHash, dryRunMinHash);
                }
            }
            else if (_store is not null)
            {
                var claimed = await _store.ClaimAsync(
                    new ClaimRequest(scope, id, fingerprint.ExactHash, holder, lease)
                    {
                        HashVersion = hashVersion,
                        KeyVersion = keyVersion,
                    }, ct).ConfigureAwait(false);

                if (claimed.Status == ClaimStatus.InFlight)
                {
                    decisions.Add(new DedupDecision(chunk, id, DedupOutcome.InFlight, MatchStage.Exact, claimed.Owner));
                    _logger.LogDebug(
                        "Chunk {Ordinal} is being handled by another worker ({Existing}), try again later",
                        chunk.Ordinal, Short(claimed.Owner.Value));
                    continue;
                }

                if (!claimed.Won)
                {
                    // finished by another worker between our lookup and the claim
                    decisions.Add(Repeat(chunk, id, MatchStage.Exact, claimed.Owner, null, null));
                    LogMatch(chunk, MatchStage.Exact, claimed.Owner, null, null);
                    continue;
                }

                batchIds.Add(id.Value);
                if (embedded)
                {
                    claim = new ChunkClaim(scope, id, fingerprint.ExactHash, holder, fingerprint);
                    batchEmbedded.Add(id.Value);
                    AddToNearIndexes(id, scope, fingerprint, dryRunSimHash, dryRunMinHash);
                }
                else if (batchEmbedded.Contains(near!.Existing.Value))
                {
                    // close to a chunk of this same batch that is not committed yet, so this one has to wait for it
                    claim = new ChunkClaim(scope, id, fingerprint.ExactHash, holder, fingerprint, near.Existing);
                }
                else
                {
                    // not embedded, so nothing to wait for: the hash is final and points at the chunk that stands for it
                    await _store.CommitAsync(
                        new CommitRequest(scope, id, fingerprint.ExactHash, holder, near!.Existing), ct).ConfigureAwait(false);
                }
            }
            else
            {
                var added = await _exactIndex.AddIfAbsentAsync(scope, fingerprint.ExactHash, storedAs, ct).ConfigureAwait(false);
                if (!added.Added)
                {
                    // another caller stored the same text between our lookup and now
                    decisions.Add(Repeat(chunk, id, MatchStage.Exact, added.Owner, null, null));
                    LogMatch(chunk, MatchStage.Exact, added.Owner, null, null);
                    continue;
                }

                if (embedded)
                {
                    if (_simHashIndex is not null && fingerprint.HasSimHash)
                    {
                        await _simHashIndex.AddAsync(id, scope, fingerprint, ct).ConfigureAwait(false);
                    }

                    if (_minHashIndex is not null && fingerprint.HasMinHash)
                    {
                        await _minHashIndex.AddAsync(id, scope, fingerprint, ct).ConfigureAwait(false);
                    }
                }
            }

            if (near is null)
            {
                decisions.Add(new DedupDecision(chunk, id, DedupOutcome.Unique, null, null, Claim: claim));
                continue;
            }

            decisions.Add(Repeat(chunk, id, near.Stage, near.Existing, near.HammingDistance, near.EstimatedJaccard) with { Claim = claim });
            LogMatch(chunk, near.Stage, near.Existing, near.HammingDistance, near.EstimatedJaccard);
        }

        for (var i = 0; i < decisions.Count; i++)
        {
            decisions[i] = decisions[i] with { Language = languages[i] };
        }

        if (scopeKey is { } linkedKey && _subjects is not null && !_options.DryRun)
        {
            await SubjectLinker.ApplyAsync(_subjects, userScope, linkedKey, StoredNow(decisions), ct).ConfigureAwait(false);
        }

        return new DedupReport(userScope, _options.DryRun, decisions);
    }

    // A claim is not stored until it is committed. A duplicate of a claim in this same batch waits with it.
    internal static List<SubjectLink> StoredNow(IReadOnlyList<DedupDecision> decisions)
    {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var decision in decisions)
        {
            if (decision.Claim is not null)
            {
                pending.Add(decision.Claim.Id.Value);
            }
        }

        var links = new List<SubjectLink>();
        foreach (var decision in decisions)
        {
            if (decision.Claim is not null || decision.Outcome is not (DedupOutcome.Unique or DedupOutcome.Flagged or DedupOutcome.Duplicate))
            {
                continue;
            }

            var stored = decision.Outcome == DedupOutcome.Duplicate ? decision.DuplicateOf ?? decision.Id : decision.Id;
            if (pending.Contains(stored.Value))
            {
                continue;
            }

            links.Add(new SubjectLink(stored, decision.Input, stored == decision.Id));
        }

        return links;
    }

    private void AddToNearIndexes(
        ChunkId id, string scope, ChunkFingerprint fingerprint, SimHashBandIndex? simHashOverlay, MinHashLshIndex? minHashOverlay)
    {
        if (fingerprint.HasSimHash)
        {
            simHashOverlay?.Add(
                id, scope, fingerprint.SimHash, fingerprint.GuardSignature, fingerprint.ContentSignature, fingerprint.IdentifierSignature);
        }

        if (fingerprint.HasMinHash)
        {
            minHashOverlay?.Add(
                id, scope, fingerprint.MinHash.Span, fingerprint.GuardSignature, fingerprint.ContentSignature, fingerprint.IdentifierSignature);
        }
    }

    private async ValueTask<NearDuplicateMatch?> FindNearAsync(
        string scope,
        ChunkFingerprint fingerprint,
        SimHashBandIndex? simHashOverlay,
        MinHashLshIndex? minHashOverlay,
        ChunkInput chunk,
        IReadOnlySet<ChunkId>? excluded,
        CancellationToken ct)
    {
        var minHashOn = _minHashIndex is not null && fingerprint.HasMinHash;

        // Stage 2: SimHash candidates, closest first.
        if (_simHashIndex is not null && fingerprint.HasSimHash)
        {
            var found = await _simHashIndex.FindAsync(fingerprint, scope, ct).ConfigureAwait(false);
            IEnumerable<NearDuplicateMatch> all = found;
            if (simHashOverlay is not null)
            {
                all = all.Concat(simHashOverlay.Find(scope, fingerprint.SimHash));
            }

            foreach (var candidate in all
                .Where(c => excluded is null || !excluded.Contains(c.Existing))
                .OrderBy(c => c.HammingDistance).ThenBy(c => c.Existing.Value, StringComparer.Ordinal))
            {
                if (Blocked(fingerprint, candidate, chunk))
                {
                    continue;
                }

                double? jaccard = null;
                if (minHashOn)
                {
                    jaccard = await EstimateAsync(scope, candidate.Existing, fingerprint, minHashOverlay, ct).ConfigureAwait(false);
                }

                if (minHashOn && _options.SimHash.ConfirmWithMinHash && (jaccard is null || jaccard < _options.MinHash.Threshold))
                {
                    _logger.LogDebug(
                        "Chunk {Ordinal} is close to {Existing} by SimHash but MinHash does not confirm it (Jaccard {Jaccard})",
                        chunk.Ordinal, Short(candidate.Existing.Value), jaccard);
                    continue;
                }

                return candidate with { EstimatedJaccard = jaccard };
            }
        }

        // Stage 3: MinHash on its own, for what SimHash did not catch.
        if (minHashOn)
        {
            var found = await _minHashIndex!.FindAsync(fingerprint, scope, ct).ConfigureAwait(false);
            IEnumerable<NearDuplicateMatch> all = found;
            if (minHashOverlay is not null)
            {
                all = all.Concat(minHashOverlay.Find(scope, fingerprint.MinHash.Span));
            }

            foreach (var candidate in all
                .Where(c => excluded is null || !excluded.Contains(c.Existing))
                .OrderByDescending(c => c.EstimatedJaccard)
                .ThenBy(c => c.Existing.Value, StringComparer.Ordinal))
            {
                if (!Blocked(fingerprint, candidate, chunk))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private async ValueTask<double?> EstimateAsync(
        string scope, ChunkId id, ChunkFingerprint fingerprint, MinHashLshIndex? overlay, CancellationToken ct)
    {
        var stored = await _minHashIndex!.EstimateJaccardAsync(scope, id, fingerprint.MinHash, ct).ConfigureAwait(false);
        return stored ?? overlay?.EstimateJaccard(scope, id, fingerprint.MinHash.Span);
    }

    // close wording, but different numbers, negations, or only an identifier
    private bool Blocked(ChunkFingerprint fingerprint, NearDuplicateMatch candidate, ChunkInput chunk)
    {
        if (_options.GuardNumbersAndNegations && candidate.GuardSignature != fingerprint.GuardSignature)
        {
            _logger.LogDebug(
                "Chunk {Ordinal} is close to {Existing} but differs in numbers or negations, keeping both",
                chunk.Ordinal, Short(candidate.Existing.Value));
            return true;
        }

        if (_options.GuardIdentifierOnlyChanges
            && candidate.ContentSignature == fingerprint.ContentSignature
            && candidate.IdentifierSignature != fingerprint.IdentifierSignature)
        {
            _logger.LogDebug(
                "Chunk {Ordinal} is close to {Existing} but differs only in identifiers, keeping both",
                chunk.Ordinal, Short(candidate.Existing.Value));
            return true;
        }

        return false;
    }

    private DedupDecision Repeat(
        ChunkInput chunk, ChunkId id, MatchStage stage, ChunkId of, int? distance, double? jaccard)
    {
        var outcome = _options.OnDuplicate == DuplicatePolicy.Flag ? DedupOutcome.Flagged : DedupOutcome.Duplicate;
        return new DedupDecision(chunk, id, outcome, stage, of, distance, jaccard);
    }

    // never log the text, only a short piece of the hash
    private void LogMatch(ChunkInput chunk, MatchStage stage, ChunkId existing, int? distance, double? jaccard) =>
        _logger.LogDebug(
            "Chunk {Ordinal} matched {Stage} with {Existing} (distance {Distance}, Jaccard {Jaccard})",
            chunk.Ordinal, stage, Short(existing.Value), distance, jaccard);

    private static string Short(string value) => value.Length <= 8 ? value : value[..8];

    private ScopeKey? CachedKey(string scope)
    {
        if (_keys is null)
        {
            return null;
        }

        if (!_keys.TryGetCached(scope, out var key))
        {
            throw new KeyUnavailableException(scope);
        }

        _bands?.Warm(scope, key);
        return key;
    }

    private static ChunkId IdFrom(ReadOnlySpan<byte> digest, string? language, ScopeKey? key) =>
        key is { } scopeKey ? ChunkIds.Create(digest, language, scopeKey) : LanguageTags.IdFor(digest, language);
}
