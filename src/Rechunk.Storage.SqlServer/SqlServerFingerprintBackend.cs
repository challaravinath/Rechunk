using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Rechunk.Storage.SqlServer;

/// <summary>
/// Exact hashes and band rows in SQL Server. A claim locks the hash row with UPDLOCK and HOLDLOCK, so one
/// of many workers wins and the others see that the hash is in flight. Band lookup is one statement.
/// </summary>
public sealed class SqlServerFingerprintBackend : IFingerprintBackend
{
    private readonly SqlServerDatabase _db;

    public SqlServerFingerprintBackend(SqlServerStorageOptions options)
    {
        _db = new SqlServerDatabase(options);
    }

    public SqlServerFingerprintBackend(string connectionString)
        : this(new SqlServerStorageOptions { ConnectionString = connectionString })
    {
    }

    /// <summary>The CREATE TABLE script, with {{SCHEMA}} where the schema name goes.</summary>
    public static string SchemaSql => SqlServerDatabase.SchemaSql;

    public ValueTask<ClaimResult> ClaimAsync(ClaimRequest request, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        request.CheckVersions();
        var until = now + request.Lease;
        var hash = request.ExactHash.ToArray();

        return new ValueTask<ClaimResult>(_db.InTransactionAsync(async (connection, transaction, token) =>
        {
            await EnsureScopeAsync(connection, transaction, request, token).ConfigureAwait(false);
            var row = await ReadExactAsync(connection, transaction, request.Scope, hash, token).ConfigureAwait(false);
            if (row is null)
            {
                try
                {
                    await WriteExactAsync(connection, transaction, true, request.Scope, hash, request.Id.Value, false, request.Holder, until, request.HashVersion, request.KeyVersion, request.Id.Value, token).ConfigureAwait(false);
                    return new ClaimResult(ClaimStatus.Claimed, request.Id, until);
                }
                catch (SqlException ex) when (SqlServerDatabase.IsDuplicateKey(ex))
                {
                    row = await ReadExactAsync(connection, transaction, request.Scope, hash, token).ConfigureAwait(false);
                    if (row is null)
                    {
                        throw;
                    }
                }
            }

            var current = row.Value;
            if (current.HashVersion != request.HashVersion || current.KeyVersion != request.KeyVersion)
            {
                throw new MixedHashVersionException(request.Scope, current.HashVersion, current.KeyVersion, request.HashVersion, request.KeyVersion);
            }

            if (current.Committed)
            {
                return new ClaimResult(ClaimStatus.AlreadyCommitted, new ChunkId(current.Chunk));
            }

            if (current.Holder == request.Holder && current.Chunk == request.Id.Value)
            {
                await WriteExactAsync(connection, transaction, false, request.Scope, hash, request.Id.Value, false, request.Holder, until, request.HashVersion, request.KeyVersion, request.Id.Value, token).ConfigureAwait(false);
                return new ClaimResult(ClaimStatus.Renewed, request.Id, until);
            }

            if (current.LeaseUntil <= now.UtcTicks)
            {
                await WriteExactAsync(connection, transaction, false, request.Scope, hash, request.Id.Value, false, request.Holder, until, request.HashVersion, request.KeyVersion, request.Id.Value, token).ConfigureAwait(false);
                return new ClaimResult(ClaimStatus.TakenOver, request.Id, until);
            }

            return new ClaimResult(ClaimStatus.InFlight, new ChunkId(current.Chunk), new DateTimeOffset(current.LeaseUntil, TimeSpan.Zero));
        }, ct));
    }

    public ValueTask<CommitStatus> CommitAsync(CommitRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var owner = (request.AliasOf ?? request.Id).Value;
        var hash = request.ExactHash.ToArray();

        return new ValueTask<CommitStatus>(_db.InTransactionAsync(async (connection, transaction, token) =>
        {
            var row = await ReadExactAsync(connection, transaction, request.Scope, hash, token).ConfigureAwait(false);
            if (row is null)
            {
                return CommitStatus.NotHeld;
            }

            var current = row.Value;
            if (current.Committed)
            {
                return current.Chunk == owner ? CommitStatus.AlreadyCommitted : CommitStatus.NotHeld;
            }

            if (current.Holder == request.Holder && current.Chunk == request.Id.Value)
            {
                await WriteExactAsync(connection, transaction, false, request.Scope, hash, owner, true, string.Empty, default, current.HashVersion, current.KeyVersion, current.TextId, token).ConfigureAwait(false);
                return CommitStatus.Committed;
            }

            return CommitStatus.NotHeld;
        }, ct));
    }

    public async ValueTask<bool> ReleaseAsync(ReleaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        return await _db.InTransactionAsync(async (connection, transaction, token) =>
        {
            await using var command = SqlServerDatabase.Command(connection, transaction,
                "DELETE FROM " + _db.Names.Exact + " WHERE scope = @scope AND hash = @hash AND committed = 0 AND holder = @holder AND chunk = @chunk");
            command.Parameters.AddWithValue("@scope", request.Scope);
            command.Parameters.AddWithValue("@hash", request.ExactHash.ToArray());
            command.Parameters.AddWithValue("@holder", request.Holder);
            command.Parameters.AddWithValue("@chunk", request.Id.Value);
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) > 0;
        }, ct).ConfigureAwait(false);
    }

    public async ValueTask<ChunkId?> FindCommittedAsync(string scope, ReadOnlyMemory<byte> exactHash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = SqlServerDatabase.Command(connection, null,
            "SELECT chunk FROM " + _db.Names.Exact + " WHERE scope = @scope AND hash = @hash AND committed = 1");
        command.Parameters.AddWithValue("@scope", scope);
        command.Parameters.AddWithValue("@hash", exactHash.ToArray());
        var found = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return found is string chunk ? new ChunkId(chunk) : null;
    }

    public ValueTask<bool> RemoveAsync(string scope, ReadOnlyMemory<byte> exactHash, CancellationToken ct = default) =>
        new(DeleteExactAsync(scope, exactHash, null, ct));

    public ValueTask<bool> RemoveOwnedAsync(string scope, ReadOnlyMemory<byte> exactHash, ChunkId owner, CancellationToken ct = default) =>
        new(DeleteExactAsync(scope, exactHash, owner, ct));

    public async ValueTask<IReadOnlyList<byte[]>> ListOwnedHashesAsync(string scope, ChunkId owner, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = SqlServerDatabase.Command(connection, null,
            "SELECT hash FROM " + _db.Names.Exact + " WHERE scope = @scope AND chunk_key = HASHBYTES('SHA2_256', @chunk) AND chunk = @chunk AND committed = 1");
        command.Parameters.AddWithValue("@scope", scope);
        command.Parameters.AddWithValue("@chunk", owner.Value);
        var hashes = new List<byte[]>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            hashes.Add((byte[])reader[0]);
        }

        return hashes;
    }

    public async ValueTask<byte[]?> FindHashByTextIdAsync(string scope, ChunkId textId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = SqlServerDatabase.Command(connection, null,
            "SELECT hash FROM " + _db.Names.Exact + " WHERE scope = @scope AND text_key = HASHBYTES('SHA2_256', @text) AND text_id = @text AND text_id <> N''");
        command.Parameters.AddWithValue("@scope", scope);
        command.Parameters.AddWithValue("@text", textId.Value);
        var found = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return found as byte[];
    }

    public ValueTask PutAsync(string scope, BandKind kind, string id, byte[] payload, IReadOnlyList<BandKey> keys, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return new ValueTask(_db.InTransactionAsync(async (connection, transaction, token) =>
        {
            await using (var entry = SqlServerDatabase.Command(connection, transaction,
                "MERGE " + _db.Names.BandEntry + " WITH (HOLDLOCK) AS t " +
                "USING (SELECT @scope AS scope, @kind AS kind, HASHBYTES('SHA2_256', @id) AS id_key) AS s " +
                "ON t.scope = s.scope AND t.kind = s.kind AND t.id_key = s.id_key " +
                "WHEN MATCHED THEN UPDATE SET payload = @payload " +
                "WHEN NOT MATCHED THEN INSERT (scope, kind, id, payload) VALUES (@scope, @kind, @id, @payload);"))
            {
                entry.Parameters.AddWithValue("@scope", scope);
                entry.Parameters.AddWithValue("@kind", (byte)kind);
                entry.Parameters.AddWithValue("@id", id);
                entry.Parameters.AddWithValue("@payload", payload);
                await entry.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            if (keys.Count == 0)
            {
                return true;
            }

            await using var insert = SqlServerDatabase.Command(connection, transaction,
                "BEGIN TRY " +
                "INSERT INTO " + _db.Names.BandKey + " (scope, kind, band, [key], id_key) " +
                "SELECT @scope, @kind, j.band, j.k, HASHBYTES('SHA2_256', @id) FROM OPENJSON(@keys) WITH (band int '$.b', k bigint '$.k') j " +
                "WHERE NOT EXISTS (SELECT 1 FROM " + _db.Names.BandKey + " k WITH (UPDLOCK, HOLDLOCK) " +
                "WHERE k.scope = @scope AND k.kind = @kind AND k.band = j.band AND k.[key] = j.k AND k.id_key = HASHBYTES('SHA2_256', @id)); " +
                "END TRY BEGIN CATCH IF ERROR_NUMBER() NOT IN (2601, 2627) THROW; END CATCH");
            insert.Parameters.AddWithValue("@scope", scope);
            insert.Parameters.AddWithValue("@kind", (byte)kind);
            insert.Parameters.AddWithValue("@id", id);
            insert.Parameters.AddWithValue("@keys", KeysJson(keys));
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return true;
        }, ct));
    }

    public ValueTask<bool> DeleteAsync(string scope, BandKind kind, string id, IReadOnlyList<BandKey> keys, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return new ValueTask<bool>(_db.InTransactionAsync(async (connection, transaction, token) =>
        {
            if (keys.Count > 0)
            {
                await using var key = SqlServerDatabase.Command(connection, transaction,
                    "DELETE k FROM " + _db.Names.BandKey + " k " +
                    "INNER JOIN OPENJSON(@keys) WITH (band int '$.b', k bigint '$.k') j ON k.band = j.band AND k.[key] = j.k " +
                    "WHERE k.scope = @scope AND k.kind = @kind AND k.id_key = HASHBYTES('SHA2_256', @id)");
                key.Parameters.AddWithValue("@scope", scope);
                key.Parameters.AddWithValue("@kind", (byte)kind);
                key.Parameters.AddWithValue("@id", id);
                key.Parameters.AddWithValue("@keys", KeysJson(keys));
                await key.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await using var entry = SqlServerDatabase.Command(connection, transaction,
                "DELETE FROM " + _db.Names.BandEntry + " WHERE scope = @scope AND kind = @kind AND id_key = HASHBYTES('SHA2_256', @id) AND id = @id");
            entry.Parameters.AddWithValue("@scope", scope);
            entry.Parameters.AddWithValue("@kind", (byte)kind);
            entry.Parameters.AddWithValue("@id", id);
            return await entry.ExecuteNonQueryAsync(token).ConfigureAwait(false) > 0;
        }, ct));
    }

    public async ValueTask<byte[]?> GetAsync(string scope, BandKind kind, string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = SqlServerDatabase.Command(connection, null,
            "SELECT payload FROM " + _db.Names.BandEntry + " WHERE scope = @scope AND kind = @kind AND id_key = HASHBYTES('SHA2_256', @id) AND id = @id");
        command.Parameters.AddWithValue("@scope", scope);
        command.Parameters.AddWithValue("@kind", (byte)kind);
        command.Parameters.AddWithValue("@id", id);
        var found = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return found as byte[];
    }

    public async ValueTask<IReadOnlyList<BandEntry>> QueryAsync(string scope, BandKind kind, IReadOnlyList<BandKey> keys, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (keys.Count == 0)
        {
            return Array.Empty<BandEntry>();
        }

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = SqlServerDatabase.Command(connection, null,
            "SELECT DISTINCT e.id, e.payload FROM OPENJSON(@keys) WITH (band int '$.b', k bigint '$.k') j " +
            "INNER JOIN " + _db.Names.BandKey + " k ON k.scope = @scope AND k.kind = @kind AND k.band = j.band AND k.[key] = j.k " +
            "INNER JOIN " + _db.Names.BandEntry + " e ON e.scope = k.scope AND e.kind = k.kind AND e.id_key = k.id_key");
        command.Parameters.AddWithValue("@scope", scope);
        command.Parameters.AddWithValue("@kind", (byte)kind);
        command.Parameters.AddWithValue("@keys", KeysJson(keys));
        var found = new List<BandEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            found.Add(new BandEntry(reader.GetString(0), (byte[])reader[1]));
        }

        return found;
    }

    private async Task<bool> DeleteExactAsync(string scope, ReadOnlyMemory<byte> exactHash, ChunkId? owner, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sql = "DELETE FROM " + _db.Names.Exact + " WHERE scope = @scope AND hash = @hash";
        if (owner is not null)
        {
            sql += " AND committed = 1 AND chunk = @chunk";
        }

        return await _db.InTransactionAsync(async (connection, transaction, token) =>
        {
            await using var command = SqlServerDatabase.Command(connection, transaction, sql);
            command.Parameters.AddWithValue("@scope", scope);
            command.Parameters.AddWithValue("@hash", exactHash.ToArray());
            if (owner is not null)
            {
                command.Parameters.AddWithValue("@chunk", owner.Value.Value);
            }

            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) > 0;
        }, ct).ConfigureAwait(false);
    }

    private async Task EnsureScopeAsync(SqlConnection connection, SqlTransaction transaction, ClaimRequest request, CancellationToken ct)
    {
        await using var read = SqlServerDatabase.Command(connection, transaction,
            "SELECT hash_version, key_version FROM " + _db.Names.ScopeVersion + " WITH (UPDLOCK, HOLDLOCK) WHERE scope = @scope");
        read.Parameters.AddWithValue("@scope", request.Scope);
        await using (var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var hashVersion = reader.GetInt32(0);
                var keyVersion = reader.GetInt32(1);
                if (hashVersion != request.HashVersion || keyVersion != request.KeyVersion)
                {
                    throw new MixedHashVersionException(request.Scope, hashVersion, keyVersion, request.HashVersion, request.KeyVersion);
                }

                return;
            }
        }

        try
        {
            await using var insert = SqlServerDatabase.Command(connection, transaction,
                "INSERT INTO " + _db.Names.ScopeVersion + " (scope, hash_version, key_version) VALUES (@scope, @hash, @key)");
            insert.Parameters.AddWithValue("@scope", request.Scope);
            insert.Parameters.AddWithValue("@hash", request.HashVersion);
            insert.Parameters.AddWithValue("@key", request.KeyVersion);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (SqlServerDatabase.IsDuplicateKey(ex))
        {
            await using var again = SqlServerDatabase.Command(connection, transaction,
                "SELECT hash_version, key_version FROM " + _db.Names.ScopeVersion + " WITH (UPDLOCK, HOLDLOCK) WHERE scope = @scope");
            again.Parameters.AddWithValue("@scope", request.Scope);
            await using var reader = await again.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw;
            }

            var hashVersion = reader.GetInt32(0);
            var keyVersion = reader.GetInt32(1);
            if (hashVersion != request.HashVersion || keyVersion != request.KeyVersion)
            {
                throw new MixedHashVersionException(request.Scope, hashVersion, keyVersion, request.HashVersion, request.KeyVersion);
            }
        }
    }

    private async Task<ExactRow?> ReadExactAsync(SqlConnection connection, SqlTransaction transaction, string scope, byte[] hash, CancellationToken ct)
    {
        await using var command = SqlServerDatabase.Command(connection, transaction,
            "SELECT chunk, committed, holder, lease_until, hash_version, key_version, text_id FROM " + _db.Names.Exact +
            " WITH (UPDLOCK, HOLDLOCK) WHERE scope = @scope AND hash = @hash");
        command.Parameters.AddWithValue("@scope", scope);
        command.Parameters.AddWithValue("@hash", hash);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new ExactRow(
            reader.GetString(0),
            reader.GetBoolean(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? string.Empty : reader.GetString(6));
    }

    private async Task WriteExactAsync(
        SqlConnection connection, SqlTransaction transaction, bool insert, string scope, byte[] hash, string chunk, bool committed,
        string holder, DateTimeOffset until, int hashVersion, int keyVersion, string textId, CancellationToken ct)
    {
        var sql = insert
            ? "INSERT INTO " + _db.Names.Exact + " (scope, hash, chunk, committed, holder, lease_until, hash_version, key_version, text_id) VALUES (@scope, @hash, @chunk, @committed, @holder, @lease, @hashVersion, @keyVersion, @text)"
            : "UPDATE " + _db.Names.Exact + " SET chunk = @chunk, committed = @committed, holder = @holder, lease_until = @lease, text_id = @text WHERE scope = @scope AND hash = @hash";
        await using var command = SqlServerDatabase.Command(connection, transaction, sql);
        command.Parameters.AddWithValue("@scope", scope);
        command.Parameters.AddWithValue("@hash", hash);
        command.Parameters.AddWithValue("@chunk", chunk);
        command.Parameters.AddWithValue("@committed", committed);
        command.Parameters.AddWithValue("@holder", holder);
        command.Parameters.AddWithValue("@lease", until == default ? 0L : until.UtcTicks);
        command.Parameters.AddWithValue("@hashVersion", hashVersion);
        command.Parameters.AddWithValue("@keyVersion", keyVersion);
        command.Parameters.AddWithValue("@text", textId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string KeysJson(IReadOnlyList<BandKey> keys)
    {
        var rows = new List<BandJson>(keys.Count);
        foreach (var key in keys)
        {
            rows.Add(new BandJson(key.Band, unchecked((long)key.Key)));
        }

        return JsonSerializer.Serialize(rows);
    }

    private readonly record struct ExactRow(string Chunk, bool Committed, string Holder, long LeaseUntil, int HashVersion, int KeyVersion, string TextId);

    private readonly record struct BandJson(int b, long k);
}
