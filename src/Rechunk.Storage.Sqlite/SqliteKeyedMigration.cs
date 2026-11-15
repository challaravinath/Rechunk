using Microsoft.Data.Sqlite;

namespace Rechunk.Storage.Sqlite;

internal sealed class SqliteKeyedMigration : IMigratableFingerprints
{
    private readonly SqliteDatabase _db;

    public SqliteKeyedMigration(SqliteDatabase db) => _db = db;

    public static void ThrowIfBusy(SqliteConnection connection, string scope)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT migrating FROM rechunk_scope_hash WHERE scope = $scope";
        command.Parameters.AddWithValue("$scope", scope);
        if (command.ExecuteScalar() is long flag && flag != 0)
        {
            throw new MigrationInProgressException(scope);
        }
    }

    public ValueTask<MigrationPreview> PreviewAsync(string userScope, ScopeKey key, RechunkOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _db.Open();
        var scopes = Scopes(connection, userScope);
        var legacy = Rows(connection, userScope, version1Only: true);
        var pairs = new List<IdPair>();
        var bands = 0;
        foreach (var row in legacy)
        {
            var (chunk, text) = LegacyIds.Rewrite(row.Scope, row.Hash, row.Chunk, row.TextId, key);
            pairs.Add(new IdPair(row.Chunk, chunk));
            if (!string.IsNullOrEmpty(row.TextId) && row.TextId != row.Chunk)
            {
                pairs.Add(new IdPair(row.TextId, text));
            }

            if (HasBand(connection, row.Scope, row.Chunk))
            {
                bands++;
            }
        }

        var migrating = scopes.Any(s => Busy(connection, s));
        var already = legacy.Count == 0 && scopes.Count > 0 && scopes.All(s => Version(connection, s) == ChunkIds.HmacHashVersion) && !migrating;
        return ValueTask.FromResult(new MigrationPreview(already, migrating, legacy.Count, bands, pairs));
    }

    public ValueTask BeginAsync(string userScope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _db.InTransaction(connection =>
        {
            foreach (var scope in Scopes(connection, userScope).Append(userScope).Distinct(StringComparer.Ordinal))
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO rechunk_scope_hash (scope, hash_version, key_version, migrating)
                    VALUES ($scope, 1, 0, 1)
                    ON CONFLICT(scope) DO UPDATE SET migrating = 1
                    """;
                command.Parameters.AddWithValue("$scope", scope);
                command.ExecuteNonQuery();
            }

            return true;
        });
        return ValueTask.CompletedTask;
    }

    public ValueTask<FingerprintMove> MoveNextAsync(string userScope, ScopeKey key, RechunkOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        var layout = new SimHashBandLayout(options.SimHash.MaxHammingDistance);
        var lsh = options.MinHash.Bands is { } bands && options.MinHash.Rows is { } rows
            ? new LshParameters(bands, rows)
            : LshParameters.Derive(options.MinHash.Permutations, options.MinHash.Threshold);

        var moved = _db.InTransaction(connection =>
        {
            var row = Rows(connection, userScope, version1Only: true).FirstOrDefault();
            if (row.Hash is null)
            {
                return new FingerprintMove(false, string.Empty, string.Empty, null, null, false);
            }

            var (newChunk, newText) = LegacyIds.Rewrite(row.Scope, row.Hash, row.Chunk, row.TextId, key);
            using (var update = connection.CreateCommand())
            {
                update.CommandText = """
                    UPDATE rechunk_exact
                    SET chunk = $chunk, text_id = $text, hash_version = 2, key_version = $version
                    WHERE scope = $scope AND hash = $hash AND hash_version = 1
                    """;
                update.Parameters.AddWithValue("$chunk", newChunk);
                update.Parameters.AddWithValue("$text", newText);
                update.Parameters.AddWithValue("$version", key.Version);
                update.Parameters.AddWithValue("$scope", row.Scope);
                update.Parameters.AddWithValue("$hash", row.Hash);
                if (update.ExecuteNonQuery() == 0)
                {
                    return new FingerprintMove(false, string.Empty, string.Empty, null, null, false);
                }
            }

            Remember(connection, row.Scope, row.Chunk, newChunk);
            string? alsoOld = null;
            string? alsoNew = null;
            if (!string.IsNullOrEmpty(row.TextId) && row.TextId != row.Chunk)
            {
                Remember(connection, row.Scope, row.TextId, newText);
                alsoOld = row.TextId;
                alsoNew = newText;
            }

            var band = RewriteBands(connection, row.Scope, row.Chunk, newChunk, key, layout, lsh);
            return new FingerprintMove(true, row.Chunk, newChunk, alsoOld, alsoNew, band);
        });
        return ValueTask.FromResult(moved);
    }

    public ValueTask<IReadOnlyDictionary<string, string>> ReadMapAsync(string userScope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT old_id, new_id FROM rechunk_id_map WHERE scope = $scope OR scope LIKE $like ESCAPE '\\'";
        command.Parameters.AddWithValue("$scope", userScope);
        command.Parameters.AddWithValue("$like", Like(userScope));
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            map[reader.GetString(0)] = reader.GetString(1);
        }

        return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(map);
    }

    public ValueTask CompleteAsync(string userScope, int keyVersion, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _db.InTransaction(connection =>
        {
            if (Rows(connection, userScope, version1Only: true).Count > 0)
            {
                throw new InvalidOperationException("Scope '" + userScope + "' still has a digest id, so the version was not changed.");
            }

            foreach (var scope in Scopes(connection, userScope).Append(userScope).Distinct(StringComparer.Ordinal))
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO rechunk_scope_hash (scope, hash_version, key_version, migrating)
                    VALUES ($scope, 2, $version, 0)
                    ON CONFLICT(scope) DO UPDATE SET hash_version = 2, key_version = $version, migrating = 0
                    """;
                command.Parameters.AddWithValue("$scope", scope);
                command.Parameters.AddWithValue("$version", keyVersion);
                command.ExecuteNonQuery();
            }

            return true;
        });
        return ValueTask.CompletedTask;
    }

    private static bool RewriteBands(
        SqliteConnection connection, string scope, string oldId, string newId, ScopeKey key, SimHashBandLayout layout, LshParameters lsh)
    {
        var wrote = false;
        wrote |= MoveKind(connection, scope, oldId, newId, BandKind.SimHash, payload => LegacyBands.SimHash(payload, layout, key), key);
        wrote |= MoveKind(connection, scope, oldId, newId, BandKind.MinHash, payload => LegacyBands.MinHash(payload, lsh, key), key);
        return wrote;
    }

    private static bool MoveKind(SqliteConnection connection, string scope, string oldId, string newId, BandKind kind, Func<byte[], BandKey[]> keysFor, ScopeKey key)
    {
        byte[]? payload;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT payload FROM rechunk_band_entry WHERE scope = $scope AND kind = $kind AND id = $id";
            read.Parameters.AddWithValue("$scope", scope);
            read.Parameters.AddWithValue("$kind", (int)kind);
            read.Parameters.AddWithValue("$id", oldId);
            payload = read.ExecuteScalar() as byte[];
        }

        if (payload is null)
        {
            return false;
        }

        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM rechunk_band_key WHERE scope = $scope AND kind = $kind AND id = $id";
            delete.Parameters.AddWithValue("$scope", scope);
            delete.Parameters.AddWithValue("$kind", (int)kind);
            delete.Parameters.AddWithValue("$id", oldId);
            delete.ExecuteNonQuery();
        }

        var sealedPayload = PayloadCipher.Encrypt(key, scope, newId, kind == BandKind.SimHash ? "simhash" : "minhash", payload);
        using (var rename = connection.CreateCommand())
        {
            rename.CommandText = "UPDATE rechunk_band_entry SET id = $new, payload = $payload WHERE scope = $scope AND kind = $kind AND id = $old";
            rename.Parameters.AddWithValue("$new", newId);
            rename.Parameters.AddWithValue("$payload", sealedPayload);
            rename.Parameters.AddWithValue("$scope", scope);
            rename.Parameters.AddWithValue("$kind", (int)kind);
            rename.Parameters.AddWithValue("$old", oldId);
            rename.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT OR IGNORE INTO rechunk_band_key (scope, kind, band, key, id) VALUES ($scope, $kind, $band, $key, $id)";
        var band = insert.Parameters.Add("$band", SqliteType.Integer);
        var value = insert.Parameters.Add("$key", SqliteType.Integer);
        insert.Parameters.AddWithValue("$scope", scope);
        insert.Parameters.AddWithValue("$kind", (int)kind);
        insert.Parameters.AddWithValue("$id", newId);
        foreach (var item in keysFor(payload))
        {
            band.Value = item.Band;
            value.Value = unchecked((long)item.Key);
            insert.ExecuteNonQuery();
        }

        return true;
    }

    private static void Remember(SqliteConnection connection, string scope, string oldId, string newId)
    {
        if (oldId == newId)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO rechunk_id_map (scope, old_id, new_id) VALUES ($scope, $old, $new)";
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$old", oldId);
        command.Parameters.AddWithValue("$new", newId);
        command.ExecuteNonQuery();
    }

    private static List<Exact> Rows(SqliteConnection connection, string userScope, bool version1Only)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT scope, hash, chunk, text_id FROM rechunk_exact
            WHERE (scope = $scope OR scope LIKE $like ESCAPE '\')
              AND ($all = 1 OR hash_version = 1)
            """;
        command.Parameters.AddWithValue("$scope", userScope);
        command.Parameters.AddWithValue("$like", Like(userScope));
        command.Parameters.AddWithValue("$all", version1Only ? 0 : 1);
        var rows = new List<Exact>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new Exact(reader.GetString(0), (byte[])reader[1], reader.GetString(2), reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));
        }

        return rows;
    }

    private static List<string> Scopes(SqliteConnection connection, string userScope)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT scope FROM rechunk_exact WHERE scope = $scope OR scope LIKE $like ESCAPE '\'
            UNION
            SELECT scope FROM rechunk_scope_hash WHERE scope = $scope OR scope LIKE $like ESCAPE '\'
            UNION
            SELECT scope FROM rechunk_band_entry WHERE scope = $scope OR scope LIKE $like ESCAPE '\'
            """;
        command.Parameters.AddWithValue("$scope", userScope);
        command.Parameters.AddWithValue("$like", Like(userScope));
        var scopes = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            scopes.Add(reader.GetString(0));
        }

        return scopes;
    }

    private static bool HasBand(SqliteConnection connection, string scope, string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM rechunk_band_entry WHERE scope = $scope AND id = $id LIMIT 1";
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is not null;
    }

    private static bool Busy(SqliteConnection connection, string scope)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT migrating FROM rechunk_scope_hash WHERE scope = $scope";
        command.Parameters.AddWithValue("$scope", scope);
        return command.ExecuteScalar() is long flag && flag != 0;
    }

    private static int Version(SqliteConnection connection, string scope)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT hash_version FROM rechunk_scope_hash WHERE scope = $scope";
        command.Parameters.AddWithValue("$scope", scope);
        return command.ExecuteScalar() is long version ? (int)version : 0;
    }

    private static string Like(string userScope)
    {
        var escaped = userScope.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
        return escaped + StoreScopes.LanguageSeparator + "%";
    }

    private readonly record struct Exact(string Scope, byte[]? Hash, string Chunk, string TextId);
}
