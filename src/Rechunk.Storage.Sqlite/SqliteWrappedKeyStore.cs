using Microsoft.Data.Sqlite;

namespace Rechunk.Storage.Sqlite;

/// <summary>
/// Wrapped scope keys in the Rechunk SQLite file. The table holds the nonce, tag, and ciphertext.
/// The wrapping key is not stored, and it is not written to a log by this class.
///
/// A new process that opens the same file with the same wrapping key gets the same scope key.
/// A wrong wrapping key, or a record whose scope or version was changed, fails to unwrap.
/// That failure does not delete or replace the row. Create does not invent a new key over an
/// existing one that cannot be read.
///
/// The file is not a secure erase. A backup or a copy of the file still has the wrapped keys.
/// The caller has to keep the wrapping key. Losing it means the scope keys in this file cannot be opened.
/// Existing fingerprint rows are not changed when this table is added.
/// </summary>
public sealed class SqliteWrappedKeyStore : IWrappedKeyStore
{
    private readonly SqliteDatabase _db;

    public SqliteWrappedKeyStore(string connectionString)
    {
        _db = new SqliteDatabase(connectionString);
    }

    public SqliteWrappedKeyStore(SqliteStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _db = new SqliteDatabase(options.ConnectionString);
    }

    public ValueTask<WrappedKeyRecord?> ReadAsync(string scope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT version, nonce, tag, ciphertext
            FROM rechunk_wrapped_key
            WHERE scope = $scope
            """;
        command.Parameters.AddWithValue("$scope", scope);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return ValueTask.FromResult<WrappedKeyRecord?>(null);
        }

        return ValueTask.FromResult<WrappedKeyRecord?>(new WrappedKeyRecord(
            scope,
            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            Blob(reader, 1),
            Blob(reader, 2),
            Blob(reader, 3)));
    }

    public ValueTask<bool> TryAddAsync(WrappedKeyRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(record.Scope))
        {
            throw new ArgumentException("Scope cannot be empty.", nameof(record));
        }

        if (record.Version < 1
            || record.Nonce is null || record.Nonce.Length != 12
            || record.Tag is null || record.Tag.Length != 16
            || record.Ciphertext is null || record.Ciphertext.Length != ScopeKey.MaterialLength)
        {
            throw new ArgumentException("The wrapped record is not a scope key this version can store.", nameof(record));
        }

        try
        {
            _db.InTransaction(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO rechunk_wrapped_key (scope, version, nonce, tag, ciphertext)
                    VALUES ($scope, $version, $nonce, $tag, $ciphertext)
                    """;
                command.Parameters.AddWithValue("$scope", record.Scope);
                command.Parameters.AddWithValue("$version", record.Version);
                command.Parameters.AddWithValue("$nonce", record.Nonce);
                command.Parameters.AddWithValue("$tag", record.Tag);
                command.Parameters.AddWithValue("$ciphertext", record.Ciphertext);
                command.ExecuteNonQuery();
                return true;
            });
            return ValueTask.FromResult(true);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && ex.SqliteExtendedErrorCode == 1555)
        {
            // another caller inserted this scope first
            return ValueTask.FromResult(false);
        }
    }

    private static byte[] Blob(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? Array.Empty<byte>() : (byte[])reader.GetValue(ordinal);
}
