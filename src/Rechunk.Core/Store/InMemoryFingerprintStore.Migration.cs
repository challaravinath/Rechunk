namespace Rechunk;

public sealed partial class InMemoryFingerprintStore
{
    private readonly HashSet<string> _migrating = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _idMap = new(StringComparer.Ordinal);
    private readonly List<PendingSim> _pendingSim = new();
    private readonly List<PendingMin> _pendingMin = new();
    private bool _bandsAttached;

    public ValueTask<MigrationPreview> PreviewAsync(string userScope, ScopeKey key, RechunkOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var scopes = ScopesOf(userScope).ToList();
            var already = scopes.Count > 0 && scopes.All(s => _scopeVersions.TryGetValue(s, out var v) && v.Hash == ChunkIds.HmacHashVersion);
            var legacy = new List<(string Scope, string Hash, Row Row)>();
            foreach (var pair in _rows)
            {
                if (!StoreScopes.IsUserOrLanguage(pair.Key.Scope, userScope) || pair.Value.HashVersion != ChunkIds.LegacyHashVersion)
                {
                    continue;
                }

                legacy.Add((pair.Key.Scope, pair.Key.Hash, pair.Value));
            }

            var pairs = new List<IdPair>();
            var bands = 0;
            foreach (var row in legacy)
            {
                var hash = Convert.FromHexString(row.Hash);
                var (chunk, text) = LegacyIds.Rewrite(row.Scope, hash, row.Row.Chunk.Value, row.Row.TextId, key);
                pairs.Add(new IdPair(row.Row.Chunk.Value, chunk));
                if (!string.IsNullOrEmpty(row.Row.TextId) && row.Row.TextId != row.Row.Chunk.Value)
                {
                    pairs.Add(new IdPair(row.Row.TextId, text));
                }

                if (SimHashIndex.Contains(row.Scope, row.Row.Chunk) || MinHashIndex.Contains(row.Scope, row.Row.Chunk))
                {
                    bands++;
                }
            }

            var migrating = scopes.Any(s => _migrating.Contains(s)) || _migrating.Contains(userScope);
            return ValueTask.FromResult(new MigrationPreview(already && legacy.Count == 0 && !migrating, migrating, legacy.Count, bands, pairs));
        }
    }

    public ValueTask BeginAsync(string userScope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _migrating.Add(userScope);
            foreach (var scope in ScopesOf(userScope))
            {
                _migrating.Add(scope);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<FingerprintMove> MoveNextAsync(string userScope, ScopeKey key, RechunkOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            foreach (var pair in _rows.ToList())
            {
                if (!StoreScopes.IsUserOrLanguage(pair.Key.Scope, userScope) || pair.Value.HashVersion != ChunkIds.LegacyHashVersion)
                {
                    continue;
                }

                var hash = Convert.FromHexString(pair.Key.Hash);
                var oldChunk = pair.Value.Chunk.Value;
                var oldText = pair.Value.TextId;
                var (newChunk, newText) = LegacyIds.Rewrite(pair.Key.Scope, hash, oldChunk, oldText, key);
                var band = false;
                var id = new ChunkId(oldChunk);
                if (SimHashIndex.TryRemove(pair.Key.Scope, id, out var sim, out var guard, out var content, out var ident))
                {
                    _pendingSim.Add(new PendingSim(pair.Key.Scope, newChunk, sim, guard, content, ident));
                    band = true;
                }

                if (MinHashIndex.TryRemove(pair.Key.Scope, id, out var signature, out var mGuard, out var mContent, out var mIdent))
                {
                    _pendingMin.Add(new PendingMin(pair.Key.Scope, newChunk, signature, mGuard, mContent, mIdent));
                    band = true;
                }

                _rows[pair.Key] = pair.Value with
                {
                    Chunk = new ChunkId(newChunk),
                    TextId = newText,
                    HashVersion = ChunkIds.HmacHashVersion,
                    KeyVersion = key.Version,
                };
                if (!string.IsNullOrEmpty(oldText))
                {
                    _textIds.Remove((pair.Key.Scope, oldText));
                }

                _textIds[(pair.Key.Scope, newText)] = pair.Key.Hash;
                MoveOwned(pair.Key.Scope, oldChunk, newChunk, pair.Key.Hash);
                Remember(oldChunk, newChunk);
                if (!string.IsNullOrEmpty(oldText) && oldText != oldChunk)
                {
                    Remember(oldText, newText);
                }

                var alsoOld = !string.IsNullOrEmpty(oldText) && oldText != oldChunk ? oldText : null;
                var alsoNew = alsoOld is null ? null : newText;
                return ValueTask.FromResult(new FingerprintMove(true, oldChunk, newChunk, alsoOld, alsoNew, band));
            }

            FlushBands(userScope, key);
            return ValueTask.FromResult(new FingerprintMove(false, string.Empty, string.Empty, null, null, false));
        }
    }

    public ValueTask<IReadOnlyDictionary<string, string>> ReadMapAsync(string userScope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyDictionary<string, string> copy = new Dictionary<string, string>(_idMap, StringComparer.Ordinal);
            return ValueTask.FromResult(copy);
        }
    }

    public ValueTask CompleteAsync(string userScope, int keyVersion, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var scopes = ScopesOf(userScope).Append(userScope).Distinct(StringComparer.Ordinal);
            foreach (var scope in scopes)
            {
                if (_rows.Any(p => p.Key.Scope == scope && p.Value.HashVersion == ChunkIds.LegacyHashVersion))
                {
                    throw new InvalidOperationException("Scope '" + scope + "' still has a digest id, so the version was not changed.");
                }

                _scopeVersions[scope] = (ChunkIds.HmacHashVersion, keyVersion);
                _migrating.Remove(scope);
            }

            _migrating.Remove(userScope);
        }

        return ValueTask.CompletedTask;
    }

    private void FlushBands(string userScope, ScopeKey key)
    {
        if (_bandsAttached)
        {
            return;
        }

        var directory = new BandKeyDirectory(new WarmKey(null, key, userScope));
        directory.Warm(userScope, key);
        SimHashIndex.Attach(directory);
        MinHashIndex.Attach(directory);
        foreach (var item in _pendingSim)
        {
            SimHashIndex.Add(new ChunkId(item.Id), item.Scope, item.SimHash, item.Guard, item.Content, item.Identifier);
        }

        foreach (var item in _pendingMin)
        {
            MinHashIndex.Add(new ChunkId(item.Id), item.Scope, item.Signature, item.Guard, item.Content, item.Identifier);
        }

        _pendingSim.Clear();
        _pendingMin.Clear();
        _bandsAttached = true;
    }

    private void ThrowIfMigrating(string scope)
    {
        foreach (var busy in _migrating)
        {
            if (StoreScopes.IsUserOrLanguage(scope, busy))
            {
                throw new MigrationInProgressException(scope);
            }
        }
    }

    private IEnumerable<string> ScopesOf(string userScope)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in _rows)
        {
            if (StoreScopes.IsUserOrLanguage(pair.Key.Scope, userScope))
            {
                seen.Add(pair.Key.Scope);
            }
        }

        foreach (var scope in _scopeVersions.Keys)
        {
            if (StoreScopes.IsUserOrLanguage(scope, userScope))
            {
                seen.Add(scope);
            }
        }

        return seen;
    }

    private void MoveOwned(string scope, string oldOwner, string newOwner, string hashHex)
    {
        if (_owned.TryGetValue((scope, oldOwner), out var set))
        {
            set.Remove(hashHex);
        }

        RememberOwned(scope, newOwner, hashHex);
    }

    private void Remember(string oldId, string newId)
    {
        if (oldId != newId)
        {
            _idMap[oldId] = newId;
        }
    }

    private sealed record PendingSim(string Scope, string Id, ulong SimHash, ulong Guard, ulong Content, ulong Identifier);

    private sealed record PendingMin(string Scope, string Id, uint[] Signature, ulong Guard, ulong Content, ulong Identifier);

    private sealed class WarmKey : IKeyProvider
    {
        private readonly IKeyProvider? _inner;
        private readonly ScopeKey _key;
        private readonly string _scope;

        public WarmKey(IKeyProvider? inner, ScopeKey key, string scope)
        {
            _inner = inner;
            _key = key;
            _scope = scope;
        }

        public ValueTask<ScopeKey> GetRequiredAsync(string scope, CancellationToken ct = default) =>
            _inner?.GetRequiredAsync(scope, ct) ?? ValueTask.FromResult(_key);

        public bool TryGetCached(string scope, out ScopeKey key)
        {
            if (string.Equals(scope, _scope, StringComparison.Ordinal) || StoreScopes.IsUserOrLanguage(scope, _scope))
            {
                key = _key;
                return true;
            }

            if (_inner is not null && _inner.TryGetCached(scope, out key))
            {
                return true;
            }

            key = default;
            return false;
        }
    }
}
