using Microsoft.Data.Sqlite;

namespace Rechunk.Storage.Sqlite;

/// <summary>
/// The fingerprint backend on a SQLite file. Claims, commits and releases each run in one write transaction, so
/// several processes that open the same file get the same exactly-one-winner behavior as the other backends.
/// Use a file path in the connection string. A plain in memory database is private to one connection and can not be shared.
/// SQLite lets one writer in at a time, which is fine for a single machine or a modest worker pool and is not meant
/// for a large fleet (use the Redis backend or your own SQL one for that).
/// </summary>
public sealed class SqliteFingerprintBackend : IFingerprintBackend
{
    private readonly SqliteDatabase _db;

    public SqliteFingerprintBackend(SqliteStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _db = new SqliteDatabase(options.ConnectionString);
    }

    public SqliteFingerprintBackend(string connectionString)
        : this(new SqliteStorageOptions { ConnectionString = connectionString })
    {
    }

    /// <summary>The CREATE TABLE statements, in case you would rather run them with your own migrations.</summary>
    public static string SchemaSql => SqliteDatabase.Schema;

    public ValueTask<ClaimResult> ClaimAsync(ClaimRequest request, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var until = now + request.Lease;

        var result = _db.InTransaction(connection =>
        {
            var row = ReadRow(connection, request.Scope, request.ExactHash.Span);
            if (row is null)
            {
                Write(connection, "INSERT", request.Scope, request.ExactHash.Span, request.Id.Value, false, request.Holder, until);
                return new ClaimResult(ClaimStatus.Claimed, request.Id, until);
            }

            var current = row.Value;
            if (current.Committed)
            {
                return new ClaimResult(ClaimStatus.AlreadyCommitted, new ChunkId(current.Chunk));
            }

            if (current.Holder == request.Holder && current.Chunk == request.Id.Value)
            {
                Write(connection, "UPDATE", request.Scope, request.ExactHash.Span, request.Id.Value, false, request.Holder, until);
                return new ClaimResult(ClaimStatus.Renewed, request.Id, until);
            }

            if (current.LeaseUntil <= now.UtcTicks)
            {
                Write(connection, "UPDATE", request.Scope, request.ExactHash.Span, request.Id.Value, false, request.Holder, until);
                return new ClaimResult(ClaimStatus.TakenOver, request.Id, until);
            }

            return new ClaimResult(ClaimStatus.InFlight, new ChunkId(current.Chunk), new DateTimeOffset(current.LeaseUntil, TimeSpan.Zero));
        });

        return ValueTask.FromResult(result);
    }

    public ValueTask<CommitStatus> CommitAsync(CommitRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var owner = (request.AliasOf ?? request.Id).Value;

        var result = _db.InTransaction(connection =>
        {
            var row = ReadRow(connection, request.Scope, request.ExactHash.Span);
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
                Write(connection, "UPDATE", request.Scope, request.ExactHash.Span, owner, true, string.Empty, default);
                return CommitStatus.Committed;
            }

            return CommitStatus.NotHeld;
        });

        return ValueTask.FromResult(result);
    }

    public ValueTask<bool> ReleaseAsync(ReleaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        // one statement is atomic on its own
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM rechunk_exact WHERE scope = $scope AND hash = $hash AND committed = 0 AND holder = $holder AND chunk = $chunk";
        command.Parameters.AddWithValue("$scope", request.Scope);
        command.Parameters.AddWithValue("$hash", request.ExactHash.ToArray());
        command.Parameters.AddWithValue("$holder", request.Holder);
        command.Parameters.AddWithValue("$chunk", request.Id.Value);
        return ValueTask.FromResult(command.ExecuteNonQuery() > 0);
    }

    public ValueTask<ChunkId?> FindCommittedAsync(string scope, ReadOnlyMemory<byte> exactHash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT chunk FROM rechunk_exact WHERE scope = $scope AND hash = $hash AND committed = 1";
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$hash", exactHash.ToArray());
        ChunkId? found = command.ExecuteScalar() is string chunk ? new ChunkId(chunk) : null;
        return ValueTask.FromResult(found);
    }

    public ValueTask<bool> RemoveAsync(string scope, ReadOnlyMemory<byte> exactHash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM rechunk_exact WHERE scope = $scope AND hash = $hash";
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$hash", exactHash.ToArray());
        return ValueTask.FromResult(command.ExecuteNonQuery() > 0);
    }

    public ValueTask PutAsync(
        string scope, BandKind kind, string id, byte[] payload, IReadOnlyList<BandKey> keys, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _db.InTransaction(connection =>
        {
            using (var entry = connection.CreateCommand())
            {
                entry.CommandText = "INSERT OR REPLACE INTO rechunk_band_entry (scope, kind, id, payload) VALUES ($scope, $kind, $id, $payload)";
                entry.Parameters.AddWithValue("$scope", scope);
                entry.Parameters.AddWithValue("$kind", (int)kind);
                entry.Parameters.AddWithValue("$id", id);
                entry.Parameters.AddWithValue("$payload", payload);
                entry.ExecuteNonQuery();
            }

            using var key = connection.CreateCommand();
            key.CommandText = "INSERT OR IGNORE INTO rechunk_band_key (scope, kind, band, key, id) VALUES ($scope, $kind, $band, $key, $id)";
            var band = key.Parameters.Add("$band", SqliteType.Integer);
            var value = key.Parameters.Add("$key", SqliteType.Integer);
            key.Parameters.AddWithValue("$scope", scope);
            key.Parameters.AddWithValue("$kind", (int)kind);
            key.Parameters.AddWithValue("$id", id);
            foreach (var k in keys)
            {
                band.Value = k.Band;
                value.Value = unchecked((long)k.Key);
                key.ExecuteNonQuery();
            }

            return true;
        });

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> DeleteAsync(
        string scope, BandKind kind, string id, IReadOnlyList<BandKey> keys, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var removed = _db.InTransaction(connection =>
        {
            using var key = connection.CreateCommand();
            key.CommandText = "DELETE FROM rechunk_band_key WHERE scope = $scope AND kind = $kind AND band = $band AND key = $key AND id = $id";
            var band = key.Parameters.Add("$band", SqliteType.Integer);
            var value = key.Parameters.Add("$key", SqliteType.Integer);
            key.Parameters.AddWithValue("$scope", scope);
            key.Parameters.AddWithValue("$kind", (int)kind);
            key.Parameters.AddWithValue("$id", id);
            foreach (var k in keys)
            {
                band.Value = k.Band;
                value.Value = unchecked((long)k.Key);
                key.ExecuteNonQuery();
            }

            using var entry = connection.CreateCommand();
            entry.CommandText = "DELETE FROM rechunk_band_entry WHERE scope = $scope AND kind = $kind AND id = $id";
            entry.Parameters.AddWithValue("$scope", scope);
            entry.Parameters.AddWithValue("$kind", (int)kind);
            entry.Parameters.AddWithValue("$id", id);
            return entry.ExecuteNonQuery() > 0;
        });

        return ValueTask.FromResult(removed);
    }

    public ValueTask<byte[]?> GetAsync(string scope, BandKind kind, string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM rechunk_band_entry WHERE scope = $scope AND kind = $kind AND id = $id";
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$id", id);
        return ValueTask.FromResult(command.ExecuteScalar() as byte[]);
    }

    public ValueTask<IReadOnlyList<BandEntry>> QueryAsync(
        string scope, BandKind kind, IReadOnlyList<BandKey> keys, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var found = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.id, e.payload
            FROM rechunk_band_key k
            JOIN rechunk_band_entry e ON e.scope = k.scope AND e.kind = k.kind AND e.id = k.id
            WHERE k.scope = $scope AND k.kind = $kind AND k.band = $band AND k.key = $key
            """;
        var band = command.Parameters.Add("$band", SqliteType.Integer);
        var value = command.Parameters.Add("$key", SqliteType.Integer);
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$kind", (int)kind);

        foreach (var k in keys)
        {
            band.Value = k.Band;
            value.Value = unchecked((long)k.Key);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (!found.ContainsKey(id))
                {
                    found[id] = (byte[])reader["payload"];
                }
            }
        }

        IReadOnlyList<BandEntry> entries = found.Select(p => new BandEntry(p.Key, p.Value)).ToList();
        return ValueTask.FromResult(entries);
    }

    private readonly record struct Row(string Chunk, bool Committed, string Holder, long LeaseUntil);

    private static Row? ReadRow(SqliteConnection connection, string scope, ReadOnlySpan<byte> hash)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT chunk, committed, holder, lease_until FROM rechunk_exact WHERE scope = $scope AND hash = $hash";
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$hash", hash.ToArray());
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new Row(reader.GetString(0), reader.GetInt64(1) != 0, reader.GetString(2), reader.GetInt64(3))
            : null;
    }

    private static void Write(
        SqliteConnection connection, string verb, string scope, ReadOnlySpan<byte> hash, string chunk, bool committed, string holder, DateTimeOffset until)
    {
        using var command = connection.CreateCommand();
        command.CommandText = verb == "INSERT"
            ? "INSERT INTO rechunk_exact (scope, hash, chunk, committed, holder, lease_until) VALUES ($scope, $hash, $chunk, $committed, $holder, $lease)"
            : "UPDATE rechunk_exact SET chunk = $chunk, committed = $committed, holder = $holder, lease_until = $lease WHERE scope = $scope AND hash = $hash";
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$hash", hash.ToArray());
        command.Parameters.AddWithValue("$chunk", chunk);
        command.Parameters.AddWithValue("$committed", committed ? 1 : 0);
        command.Parameters.AddWithValue("$holder", holder);
        command.Parameters.AddWithValue("$lease", until == default ? 0L : until.UtcTicks);
        command.ExecuteNonQuery();
    }
}
