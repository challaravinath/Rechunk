using StackExchange.Redis;

namespace Rechunk.Storage.Redis;

internal sealed class RedisKeyedMigration : IMigratableFingerprints
{
    private readonly IConnectionMultiplexer _redis;
    private readonly int _database;
    private readonly RedisKeys _keys;

    public RedisKeyedMigration(IConnectionMultiplexer redis, int database, RedisKeys keys)
    {
        _redis = redis;
        _database = database;
        _keys = keys;
    }

    private IDatabase Db => _redis.GetDatabase(_database);

    public static async ValueTask ThrowIfBusyAsync(IDatabase db, RedisKeys keys, string scope)
    {
        if (await db.HashGetAsync(keys.ScopeVersion(scope), "migrating").ConfigureAwait(false) == "1")
        {
            throw new MigrationInProgressException(scope);
        }
    }

    public async ValueTask<MigrationPreview> PreviewAsync(string userScope, ScopeKey key, RechunkOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        var rows = await LegacyRows(userScope).ConfigureAwait(false);
        var pairs = new List<IdPair>();
        var bands = 0;
        foreach (var row in rows)
        {
            var (chunk, text) = LegacyIds.Rewrite(row.Scope, row.Hash, row.Chunk, row.TextId, key);
            pairs.Add(new IdPair(row.Chunk, chunk));
            if (!string.IsNullOrEmpty(row.TextId) && row.TextId != row.Chunk)
            {
                pairs.Add(new IdPair(row.TextId, text));
            }

            if (await Db.KeyExistsAsync(_keys.Entry(row.Scope, BandKind.SimHash, row.Chunk)).ConfigureAwait(false)
                || await Db.KeyExistsAsync(_keys.Entry(row.Scope, BandKind.MinHash, row.Chunk)).ConfigureAwait(false))
            {
                bands++;
            }
        }

        var scopes = rows.Select(r => r.Scope).Append(userScope).Distinct(StringComparer.Ordinal).ToList();
        var migrating = false;
        var versions = new List<int>();
        foreach (var scope in scopes)
        {
            var fields = await Db.HashGetAsync(_keys.ScopeVersion(scope), ["hv", "migrating"]).ConfigureAwait(false);
            if (fields[1] == "1")
            {
                migrating = true;
            }

            if (fields[0].HasValue)
            {
                versions.Add(int.Parse((string)fields[0]!, System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        var already = rows.Count == 0 && versions.Count > 0 && versions.All(v => v == ChunkIds.HmacHashVersion) && !migrating;
        return new MigrationPreview(already, migrating, rows.Count, bands, pairs);
    }

    public async ValueTask BeginAsync(string userScope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var scopes = (await LegacyRows(userScope).ConfigureAwait(false)).Select(r => r.Scope).Append(userScope).Distinct(StringComparer.Ordinal);
        foreach (var scope in scopes)
        {
            var version = _keys.ScopeVersion(scope);
            if (!await Db.HashExistsAsync(version, "hv").ConfigureAwait(false))
            {
                await Db.HashSetAsync(version, [new HashEntry("hv", "1"), new HashEntry("kv", "0")]).ConfigureAwait(false);
            }

            await Db.HashSetAsync(version, "migrating", "1").ConfigureAwait(false);
        }
    }

    public async ValueTask<FingerprintMove> MoveNextAsync(string userScope, ScopeKey key, RechunkOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        var row = (await LegacyRows(userScope).ConfigureAwait(false)).FirstOrDefault();
        if (row is null)
        {
            return new FingerprintMove(false, string.Empty, string.Empty, null, null, false);
        }

        var layout = new SimHashBandLayout(options.SimHash.MaxHammingDistance);
        var lsh = options.MinHash.Bands is { } bands && options.MinHash.Rows is { } rows
            ? new LshParameters(bands, rows)
            : LshParameters.Derive(options.MinHash.Permutations, options.MinHash.Threshold);
        var (newChunk, newText) = LegacyIds.Rewrite(row.Scope, row.Hash, row.Chunk, row.TextId, key);
        var band = await MoveBands(row.Scope, row.Chunk, newChunk, key, layout, lsh).ConfigureAwait(false);
        await Db.HashSetAsync(_keys.Exact(row.Scope, row.Hash),
        [
            new HashEntry("chunk", newChunk),
            new HashEntry("text", newText),
            new HashEntry("hv", "2"),
            new HashEntry("kv", key.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ]).ConfigureAwait(false);
        await Db.HashSetAsync(_keys.Map(row.Scope), row.Chunk, newChunk).ConfigureAwait(false);
        string? alsoOld = null;
        string? alsoNew = null;
        if (!string.IsNullOrEmpty(row.TextId) && row.TextId != row.Chunk)
        {
            await Db.HashSetAsync(_keys.Map(row.Scope), row.TextId, newText).ConfigureAwait(false);
            alsoOld = row.TextId;
            alsoNew = newText;
        }

        if (await Db.KeyExistsAsync(_keys.Owned(row.Scope, row.Chunk)).ConfigureAwait(false))
        {
            var members = await Db.SetMembersAsync(_keys.Owned(row.Scope, row.Chunk)).ConfigureAwait(false);
            if (members.Length > 0 && row.Chunk != newChunk)
            {
                await Db.SetAddAsync(_keys.Owned(row.Scope, newChunk), members).ConfigureAwait(false);
                await Db.KeyDeleteAsync(_keys.Owned(row.Scope, row.Chunk)).ConfigureAwait(false);
            }
        }

        return new FingerprintMove(true, row.Chunk, newChunk, alsoOld, alsoNew, band);
    }

    public async ValueTask<IReadOnlyDictionary<string, string>> ReadMapAsync(string userScope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var scope in (await KnownScopes(userScope).ConfigureAwait(false)).Append(userScope).Distinct(StringComparer.Ordinal))
        {
            foreach (var entry in await Db.HashGetAllAsync(_keys.Map(scope)).ConfigureAwait(false))
            {
                map[entry.Name!] = entry.Value!;
            }
        }

        return map;
    }

    public async ValueTask CompleteAsync(string userScope, int keyVersion, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if ((await LegacyRows(userScope).ConfigureAwait(false)).Count > 0)
        {
            throw new InvalidOperationException("Scope '" + userScope + "' still has a digest id, so the version was not changed.");
        }

        foreach (var scope in (await KnownScopes(userScope).ConfigureAwait(false)).Append(userScope).Distinct(StringComparer.Ordinal))
        {
            await Db.HashSetAsync(_keys.ScopeVersion(scope),
            [
                new HashEntry("hv", "2"),
                new HashEntry("kv", keyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new HashEntry("migrating", "0"),
            ]).ConfigureAwait(false);
        }
    }

    private async Task<bool> MoveBands(string scope, string oldId, string newId, ScopeKey key, SimHashBandLayout layout, LshParameters lsh)
    {
        var moved = false;
        moved |= await MoveKind(scope, oldId, newId, BandKind.SimHash, payload => LegacyBands.SimHash(payload, layout, key), payload => RawSim(payload, layout), key).ConfigureAwait(false);
        moved |= await MoveKind(scope, oldId, newId, BandKind.MinHash, payload => LegacyBands.MinHash(payload, lsh, key), payload => RawMin(payload, lsh), key).ConfigureAwait(false);
        return moved;
    }

    private async Task<bool> MoveKind(
        string scope, string oldId, string newId, BandKind kind, Func<byte[], BandKey[]> next, Func<byte[], BandKey[]> raw, ScopeKey key)
    {
        var entry = _keys.Entry(scope, kind, oldId);
        var payload = await Db.StringGetAsync(entry).ConfigureAwait(false);
        if (!payload.HasValue)
        {
            return oldId != newId && await Db.KeyExistsAsync(_keys.Entry(scope, kind, newId)).ConfigureAwait(false);
        }

        var bytes = (byte[])payload!;
        var sealedPayload = PayloadCipher.Encrypt(key, scope, newId, kind == BandKind.SimHash ? "simhash" : "minhash", bytes);
        if (oldId != newId)
        {
            await Db.StringSetAsync(_keys.Entry(scope, kind, newId), sealedPayload).ConfigureAwait(false);
        }

        foreach (var band in next(bytes))
        {
            await Db.SetAddAsync(_keys.Band(scope, kind, band), newId).ConfigureAwait(false);
        }

        foreach (var band in raw(bytes))
        {
            await Db.SetRemoveAsync(_keys.Band(scope, kind, band), oldId).ConfigureAwait(false);
        }

        if (oldId != newId)
        {
            await Db.KeyDeleteAsync(entry).ConfigureAwait(false);
        }

        return true;
    }

    private static BandKey[] RawSim(byte[] payload, SimHashBandLayout layout)
    {
        if (payload.Length < 8)
        {
            return Array.Empty<BandKey>();
        }

        var sim = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(payload);
        var keys = new BandKey[layout.BandCount];
        for (var band = 0; band < layout.BandCount; band++)
        {
            keys[band] = new BandKey(band, layout.Slice(sim, band));
        }

        return keys;
    }

    private static BandKey[] RawMin(byte[] payload, LshParameters lsh)
    {
        const int Header = 24;
        if (payload.Length < Header + 4)
        {
            return Array.Empty<BandKey>();
        }

        var slots = (payload.Length - Header) / 4;
        var signature = new uint[slots];
        for (var i = 0; i < slots; i++)
        {
            signature[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(Header + (4 * i)));
        }

        var keys = new BandKey[lsh.Bands];
        for (var band = 0; band < lsh.Bands; band++)
        {
            keys[band] = new BandKey(band, lsh.BandKey(signature, band));
        }

        return keys;
    }

    private async Task<List<LegacyRow>> LegacyRows(string userScope)
    {
        var rows = new List<LegacyRow>();
        foreach (var key in Scan(_keys.AnyExact))
        {
            if (!TryParseExact(key, out var scope, out var hash) || !StoreScopes.IsUserOrLanguage(scope, userScope))
            {
                continue;
            }

            var fields = await Db.HashGetAsync(key, ["chunk", "text", "hv"]).ConfigureAwait(false);
            if (!fields[0].HasValue || fields[2] == "2")
            {
                continue;
            }

            rows.Add(new LegacyRow(scope, hash, fields[0]!, fields[1].HasValue ? fields[1]! : string.Empty));
        }

        return rows;
    }

    private async Task<List<string>> KnownScopes(string userScope)
    {
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in await LegacyRows(userScope).ConfigureAwait(false))
        {
            scopes.Add(row.Scope);
        }

        foreach (var key in Scan(_keys.Prefix + ":*:hv"))
        {
            if (TryParseTag(key.ToString() ?? string.Empty, out var scope) && StoreScopes.IsUserOrLanguage(scope, userScope))
            {
                scopes.Add(scope);
            }
        }

        return scopes.ToList();
    }

    private IEnumerable<RedisKey> Scan(string pattern)
    {
        foreach (var endpoint in _redis.GetEndPoints())
        {
            var server = _redis.GetServer(endpoint);
            if (server.IsReplica)
            {
                continue;
            }

            foreach (var key in server.Keys(_database, pattern))
            {
                yield return key;
            }
        }
    }

    private bool TryParseExact(RedisKey key, out string scope, out byte[] hash)
    {
        scope = string.Empty;
        hash = Array.Empty<byte>();
        var text = (string?)key;
        if (text is null || !text.StartsWith(_keys.Prefix + ":", StringComparison.Ordinal))
        {
            return false;
        }

        var mark = text.LastIndexOf(":x:", StringComparison.Ordinal);
        if (mark < 0 || !TryParseTag(text[..mark], out scope))
        {
            return false;
        }

        try
        {
            hash = Convert.FromHexString(text[(mark + 3)..]);
            return hash.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private bool TryParseTag(string text, out string scope)
    {
        scope = string.Empty;
        var open = text.IndexOf('{');
        var close = text.IndexOf('}');
        if (open < 0 || close <= open)
        {
            return false;
        }

        scope = text[(open + 1)..close];
        return scope.Length > 0;
    }

    private sealed record LegacyRow(string Scope, byte[] Hash, string Chunk, string TextId);
}
