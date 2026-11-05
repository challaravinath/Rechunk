using Microsoft.Data.Sqlite;

namespace Rechunk.Storage.Sqlite;

/// <summary>Opens connections and makes sure the tables are there. Shared by the backend and the dead letter store.</summary>
internal sealed class SqliteDatabase
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Ready = new(StringComparer.Ordinal);

    private readonly string _connectionString;

    public SqliteDatabase(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public const string Schema = """
        CREATE TABLE IF NOT EXISTS rechunk_exact (
            scope        TEXT    NOT NULL,
            hash         BLOB    NOT NULL,
            chunk        TEXT    NOT NULL,
            committed    INTEGER NOT NULL,
            holder       TEXT    NOT NULL,
            lease_until  INTEGER NOT NULL,
            PRIMARY KEY (scope, hash)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS rechunk_band_entry (
            scope    TEXT    NOT NULL,
            kind     INTEGER NOT NULL,
            id       TEXT    NOT NULL,
            payload  BLOB    NOT NULL,
            PRIMARY KEY (scope, kind, id)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS rechunk_band_key (
            scope  TEXT    NOT NULL,
            kind   INTEGER NOT NULL,
            band   INTEGER NOT NULL,
            key    INTEGER NOT NULL,
            id     TEXT    NOT NULL,
            PRIMARY KEY (scope, kind, band, key, id)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS rechunk_dead_letter (
            id        TEXT    NOT NULL PRIMARY KEY,
            scope     TEXT    NOT NULL,
            chunk     TEXT    NOT NULL,
            document  TEXT,
            ordinal   INTEGER NOT NULL,
            stage     INTEGER NOT NULL,
            reason    TEXT    NOT NULL,
            attempts  INTEGER NOT NULL,
            failed_at INTEGER NOT NULL,
            body      TEXT,
            language  TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_rechunk_dead_letter_scope ON rechunk_dead_letter (scope, failed_at);

        CREATE TABLE IF NOT EXISTS rechunk_doc (
            scope       TEXT    NOT NULL,
            document    TEXT    NOT NULL,
            revision    INTEGER NOT NULL,
            updated_at  INTEGER NOT NULL,
            source      TEXT,
            PRIMARY KEY (scope, document)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS rechunk_doc_chunk (
            scope           TEXT    NOT NULL,
            document        TEXT    NOT NULL,
            seq             INTEGER NOT NULL,
            ordinal         INTEGER NOT NULL,
            id              TEXT    NOT NULL,
            target          TEXT    NOT NULL,
            language        TEXT,
            first_revision  INTEGER NOT NULL,
            previous_ordinal INTEGER,
            replaces        TEXT,
            PRIMARY KEY (scope, document, seq)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS rechunk_ref (
            scope        TEXT    NOT NULL,
            chunk        TEXT    NOT NULL,
            count        INTEGER NOT NULL,
            state        INTEGER NOT NULL,
            holder       TEXT    NOT NULL,
            lease_until  INTEGER NOT NULL,
            PRIMARY KEY (scope, chunk)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS rechunk_alias (
            scope   TEXT NOT NULL,
            target  TEXT NOT NULL,
            alias   TEXT NOT NULL,
            PRIMARY KEY (scope, target, alias)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS rechunk_audit (
            id        TEXT    NOT NULL PRIMARY KEY,
            at_ms     INTEGER NOT NULL,
            scope     TEXT    NOT NULL,
            document  TEXT    NOT NULL,
            chunk     TEXT    NOT NULL,
            source    TEXT    NOT NULL,
            kind      INTEGER NOT NULL,
            body      TEXT    NOT NULL
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS ix_rechunk_audit_time ON rechunk_audit (at_ms, id);
        CREATE INDEX IF NOT EXISTS ix_rechunk_audit_document ON rechunk_audit (document, at_ms);
        CREATE INDEX IF NOT EXISTS ix_rechunk_audit_chunk ON rechunk_audit (chunk, at_ms);
        CREATE INDEX IF NOT EXISTS ix_rechunk_audit_source ON rechunk_audit (source, at_ms);

        CREATE TABLE IF NOT EXISTS rechunk_audit_finding (
            type      TEXT NOT NULL,
            event_id  TEXT NOT NULL,
            PRIMARY KEY (type, event_id)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS rechunk_audit_outbox (
            seq   INTEGER PRIMARY KEY AUTOINCREMENT,
            id    TEXT    NOT NULL UNIQUE,
            body  TEXT    NOT NULL
        );
        """;

    // Changes for databases that an older version created. CREATE TABLE IF NOT EXISTS does not touch an existing table.
    private static void Migrate(SqliteConnection connection)
    {
        if (!HasColumn(connection, "rechunk_doc", "source"))
        {
            try
            {
                Execute(connection, "ALTER TABLE rechunk_doc ADD COLUMN source TEXT;");
            }
            catch (SqliteException) when (HasColumn(connection, "rechunk_doc", "source"))
            {
                // another process added it between our check and our change
            }
        }

        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_rechunk_doc_source ON rechunk_doc (scope, source, document);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_rechunk_doc_chunk_target ON rechunk_doc_chunk (scope, target);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS ix_rechunk_doc_chunk_id ON rechunk_doc_chunk (scope, id);");

        if (!HasColumn(connection, "rechunk_dead_letter", "language"))
        {
            try
            {
                Execute(connection, "ALTER TABLE rechunk_dead_letter ADD COLUMN language TEXT;");
            }
            catch (SqliteException) when (HasColumn(connection, "rechunk_dead_letter", "language"))
            {
                // another process added it between our check and our change
            }
        }
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // the first caller creates the tables and everybody else waits for that before they use the database
        lock (Gate)
        {
            if (!Ready.Contains(_connectionString))
            {
                try
                {
                    Execute(connection, "PRAGMA journal_mode = WAL;");
                    Execute(connection, Schema);
                    Migrate(connection);
                    Ready.Add(_connectionString);
                }
                catch
                {
                    connection.Dispose();
                    throw;
                }
            }
        }

        return connection;
    }

    /// <summary>Runs the work in one write transaction. BEGIN IMMEDIATE takes the write lock up front, so two workers never read the same row and both write it.</summary>
    public T InTransaction<T>(Func<SqliteConnection, T> work)
    {
        using var connection = Open();
        Execute(connection, "BEGIN IMMEDIATE;");
        try
        {
            var result = work(connection);
            Execute(connection, "COMMIT;");
            return result;
        }
        catch
        {
            try
            {
                Execute(connection, "ROLLBACK;");
            }
            catch (SqliteException)
            {
                // the original error is the one worth seeing
            }

            throw;
        }
    }

    public static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
