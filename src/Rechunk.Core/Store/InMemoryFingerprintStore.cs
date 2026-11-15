namespace Rechunk;

/// <summary>
/// The default store. It keeps everything in this process, using the same in memory indexes the pipeline
/// uses on its own. Good for tests, a single instance and local runs. It does nothing for workers in different
/// processes, use one of the storage packages for that.
/// </summary>
public sealed partial class InMemoryFingerprintStore : IFingerprintStore, IMigratableFingerprints
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Scope, string Hash), Row> _rows = new();
    private readonly Dictionary<string, (int Hash, int Key)> _scopeVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Scope, string TextId), string> _textIds = new();
    private readonly Dictionary<(string Scope, string Owner), HashSet<string>> _owned = new();
    private readonly TimeProvider _time;

    public InMemoryFingerprintStore(RechunkOptions? options = null, TimeProvider? time = null, IKeyProvider? keys = null)
    {
        options ??= new RechunkOptions();
        _time = time ?? TimeProvider.System;
        var bands = (keys ?? options.KeyProvider) is { } provider ? BandKeys.For(provider) : null;

        SimHashIndex = new SimHashBandIndex(options.SimHash.MaxHammingDistance, bands);
        MinHashIndex = new MinHashLshIndex(options.MinHash, bands);
        Exact = new StoreExactIndex(this, FindCommitted, RemoveRow);
    }

    public IExactHashIndex Exact { get; }

    public INearDuplicateIndex SimHash => SimHashIndex;

    public IMinHashIndex MinHash => MinHashIndex;

    public SimHashBandIndex SimHashIndex { get; }

    public MinHashLshIndex MinHashIndex { get; }

    public ValueTask<ClaimResult> ClaimAsync(ClaimRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        request.CheckVersions();
        var now = _time.GetUtcNow();
        var until = now + request.Lease;
        var key = Key(request.Scope, request.ExactHash);
        var hashHex = key.Item2;

        lock (_gate)
        {
            ThrowIfMigrating(request.Scope);
            EnsureScope(request.Scope, request.HashVersion, request.KeyVersion);
            if (!_rows.TryGetValue(key, out var row))
            {
                _rows[key] = new Row(request.Id, false, request.Holder, until, request.HashVersion, request.KeyVersion, request.Id.Value);
                _textIds[(request.Scope, request.Id.Value)] = hashHex;
                return ValueTask.FromResult(new ClaimResult(ClaimStatus.Claimed, request.Id, until));
            }

            if (row.HashVersion != request.HashVersion || row.KeyVersion != request.KeyVersion)
            {
                throw new MixedHashVersionException(request.Scope, row.HashVersion, row.KeyVersion, request.HashVersion, request.KeyVersion);
            }

            if (row.Committed)
            {
                return ValueTask.FromResult(new ClaimResult(ClaimStatus.AlreadyCommitted, row.Chunk));
            }

            if (row.Holder == request.Holder && row.Chunk == request.Id)
            {
                _rows[key] = row with { LeaseUntil = until };
                return ValueTask.FromResult(new ClaimResult(ClaimStatus.Renewed, request.Id, until));
            }

            if (row.LeaseUntil <= now)
            {
                if (row.TextId != request.Id.Value)
                {
                    _textIds.Remove((request.Scope, row.TextId));
                }

                _rows[key] = new Row(request.Id, false, request.Holder, until, request.HashVersion, request.KeyVersion, request.Id.Value);
                _textIds[(request.Scope, request.Id.Value)] = hashHex;
                return ValueTask.FromResult(new ClaimResult(ClaimStatus.TakenOver, request.Id, until));
            }

            return ValueTask.FromResult(new ClaimResult(ClaimStatus.InFlight, row.Chunk, row.LeaseUntil));
        }
    }

    public async ValueTask<CommitStatus> CommitAsync(CommitRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var owner = request.AliasOf ?? request.Id;
        var key = Key(request.Scope, request.ExactHash);
        CommitStatus status;

        lock (_gate)
        {
            ThrowIfMigrating(request.Scope);
            if (!_rows.TryGetValue(key, out var row))
            {
                status = CommitStatus.NotHeld;
            }
            else if (row.Committed)
            {
                if (row.Chunk == owner)
                {
                    RememberOwned(request.Scope, owner.Value, key.Item2);
                    status = CommitStatus.AlreadyCommitted;
                }
                else
                {
                    status = CommitStatus.NotHeld;
                }
            }
            else if (row.Holder == request.Holder && row.Chunk == request.Id)
            {
                _rows[key] = row with { Chunk = owner, Committed = true, Holder = string.Empty, LeaseUntil = default };
                RememberOwned(request.Scope, owner.Value, key.Item2);
                status = CommitStatus.Committed;
            }
            else
            {
                status = CommitStatus.NotHeld;
            }
        }

        // The near indexes are written after the hash is final. Doing it again on a repeated commit repairs a
        // run that stopped half way, and adding the same id twice just replaces the entry.
        if (status != CommitStatus.NotHeld && request.AliasOf is null && request.Fingerprint is { } fingerprint)
        {
            if (fingerprint.HasSimHash)
            {
                await SimHashIndex.AddAsync(request.Id, request.Scope, fingerprint, ct).ConfigureAwait(false);
            }

            if (fingerprint.HasMinHash)
            {
                await MinHashIndex.AddAsync(request.Id, request.Scope, fingerprint, ct).ConfigureAwait(false);
            }
        }

        return status;
    }

    public ValueTask<bool> ReleaseAsync(ReleaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var key = Key(request.Scope, request.ExactHash);

        lock (_gate)
        {
            ThrowIfMigrating(request.Scope);
            if (_rows.TryGetValue(key, out var row)
                && !row.Committed
                && row.Holder == request.Holder
                && row.Chunk == request.Id)
            {
                _rows.Remove(key);
                _textIds.Remove((request.Scope, row.TextId));
                ForgetScopeIfEmpty(request.Scope);
                return ValueTask.FromResult(true);
            }
        }

        return ValueTask.FromResult(false);
    }

    public async ValueTask<int> ForgetAsync(
        string scope, ChunkId owner, IReadOnlyList<ReadOnlyMemory<byte>> exactHashes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(exactHashes);
        ct.ThrowIfCancellationRequested();

        await SimHashIndex.RemoveAsync(owner, scope, ct).ConfigureAwait(false);
        await MinHashIndex.RemoveAsync(owner, scope, ct).ConfigureAwait(false);

        var removed = 0;
        lock (_gate)
        {
            ThrowIfMigrating(scope);
            foreach (var hash in exactHashes)
            {
                var key = Key(scope, hash);
                if (DropOwned(scope, owner, key))
                {
                    removed++;
                }
            }
        }

        return removed;
    }

    public ValueTask<int> ForgetExactAsync(
        string scope, ChunkId owner, IReadOnlyList<ReadOnlyMemory<byte>> exactHashes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(exactHashes);
        ct.ThrowIfCancellationRequested();

        var removed = 0;
        lock (_gate)
        {
            ThrowIfMigrating(scope);
            foreach (var hash in exactHashes)
            {
                var key = Key(scope, hash);
                if (DropOwned(scope, owner, key))
                {
                    removed++;
                }
            }
        }

        return ValueTask.FromResult(removed);
    }

    public ValueTask<IReadOnlyList<byte[]>> ListOwnedHashesAsync(string scope, ChunkId owner, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_owned.TryGetValue((scope, owner.Value), out var hashes))
            {
                return ValueTask.FromResult<IReadOnlyList<byte[]>>(Array.Empty<byte[]>());
            }

            return ValueTask.FromResult<IReadOnlyList<byte[]>>(hashes.Select(Convert.FromHexString).ToList());
        }
    }

    public ValueTask<byte[]?> FindHashByTextIdAsync(string scope, ChunkId textId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_textIds.TryGetValue((scope, textId.Value), out var hex) ? Convert.FromHexString(hex) : null);
        }
    }

    private ValueTask<ChunkId?> FindCommitted(string scope, ReadOnlyMemory<byte> hash, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ChunkId? found = _rows.TryGetValue(Key(scope, hash), out var row) && row.Committed ? row.Chunk : null;
            return ValueTask.FromResult(found);
        }
    }

    private ValueTask<bool> RemoveRow(string scope, ReadOnlyMemory<byte> hash, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfMigrating(scope);
            var key = Key(scope, hash);
            if (!_rows.TryGetValue(key, out var row))
            {
                return ValueTask.FromResult(false);
            }

            _rows.Remove(key);
            _textIds.Remove((scope, row.TextId));
            if (_owned.TryGetValue((scope, row.Chunk.Value), out var set))
            {
                set.Remove(key.Item2);
            }

            ForgetScopeIfEmpty(scope);
            return ValueTask.FromResult(true);
        }
    }

    private void EnsureScope(string scope, int hashVersion, int keyVersion)
    {
        if (_scopeVersions.TryGetValue(scope, out var stored))
        {
            if (stored.Hash != hashVersion || stored.Key != keyVersion)
            {
                throw new MixedHashVersionException(scope, stored.Hash, stored.Key, hashVersion, keyVersion);
            }

            return;
        }

        foreach (var pair in _rows)
        {
            if (!string.Equals(pair.Key.Scope, scope, StringComparison.Ordinal))
            {
                continue;
            }

            if (pair.Value.HashVersion != hashVersion || pair.Value.KeyVersion != keyVersion)
            {
                throw new MixedHashVersionException(scope, pair.Value.HashVersion, pair.Value.KeyVersion, hashVersion, keyVersion);
            }

            _scopeVersions[scope] = (pair.Value.HashVersion, pair.Value.KeyVersion);
            return;
        }

        _scopeVersions[scope] = (hashVersion, keyVersion);
    }

    private void RememberOwned(string scope, string owner, string hashHex)
    {
        if (!_owned.TryGetValue((scope, owner), out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _owned[(scope, owner)] = set;
        }

        set.Add(hashHex);
    }

    private bool DropOwned(string scope, ChunkId owner, (string Scope, string Hash) key)
    {
        if (!_rows.TryGetValue(key, out var row) || !row.Committed || row.Chunk != owner)
        {
            return false;
        }

        _rows.Remove(key);
        _textIds.Remove((scope, row.TextId));
        if (_owned.TryGetValue((scope, owner.Value), out var set))
        {
            set.Remove(key.Hash);
        }

        ForgetScopeIfEmpty(scope);
        return true;
    }

    private void ForgetScopeIfEmpty(string scope)
    {
        foreach (var pair in _rows)
        {
            if (string.Equals(pair.Key.Scope, scope, StringComparison.Ordinal))
            {
                return;
            }
        }

        _scopeVersions.Remove(scope);
    }

    private static (string Scope, string Hash) Key(string scope, ReadOnlyMemory<byte> hash) =>
        (scope, Convert.ToHexString(hash.Span));

    private sealed record Row(ChunkId Chunk, bool Committed, string Holder, DateTimeOffset LeaseUntil, int HashVersion, int KeyVersion, string TextId);
}
