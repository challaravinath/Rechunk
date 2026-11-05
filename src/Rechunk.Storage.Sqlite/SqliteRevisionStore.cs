using Microsoft.Data.Sqlite;

namespace Rechunk.Storage.Sqlite;

/// <summary>
/// The revision store on a SQLite file, the same file as the fingerprints if you like. A commit moves the document and
/// updates the counts of every chunk in one write transaction, so workers on one machine that revise the same document
/// at the same time get one winner and one conflict.
/// </summary>
public sealed class SqliteRevisionStore : IRevisionStore
{
    private const int Active = (int)ChunkRefState.Active;
    private const int Orphaned = (int)ChunkRefState.Orphaned;
    private const int Retiring = (int)ChunkRefState.Retiring;

    private readonly SqliteDatabase _db;

    public SqliteRevisionStore(SqliteStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _db = new SqliteDatabase(options.ConnectionString);
    }

    public SqliteRevisionStore(string connectionString)
        : this(new SqliteStorageOptions { ConnectionString = connectionString })
    {
    }

    public ValueTask<DocumentManifest?> GetAsync(string scope, DocumentId document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        using var connection = _db.Open();
        return ValueTask.FromResult(ReadManifest(connection, scope, document));
    }

    public ValueTask<CommitRevisionResult> CommitAsync(CommitRevisionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        RevisionRules.Check(request);

        var result = _db.InTransaction(connection =>
        {
            var current = ReadManifest(connection, request.Scope, request.Document);
            var currentRevision = current?.Revision ?? 0;
            if (currentRevision != request.ExpectedRevision)
            {
                return new CommitRevisionResult(CommitRevisionStatus.Conflict, currentRevision, Array.Empty<ChunkId>(), Array.Empty<ChunkId>());
            }

            var (added, removed) = RevisionRules.Diff(current?.Entries, request.Entries);

            var blocking = new List<ChunkId>();
            foreach (var target in added)
            {
                var state = ReadRef(connection, request.Scope, target)?.State;
                if (state == ChunkRefState.Retiring)
                {
                    blocking.Add(new ChunkId(target));
                }
            }

            if (blocking.Count > 0)
            {
                return new CommitRevisionResult(CommitRevisionStatus.Blocked, currentRevision, Array.Empty<ChunkId>(), blocking);
            }

            foreach (var target in added)
            {
                Run(connection, """
                    INSERT INTO rechunk_ref (scope, chunk, count, state, holder, lease_until) VALUES ($s, $c, 1, $active, '', 0)
                    ON CONFLICT (scope, chunk) DO UPDATE SET count = count + 1, state = $active
                    """, ("$s", request.Scope), ("$c", target), ("$active", Active));
            }

            var orphaned = new List<ChunkId>();
            foreach (var target in removed)
            {
                var existing = ReadRef(connection, request.Scope, target);
                if (existing is null || existing.Value.Count <= 0)
                {
                    continue;
                }

                var left = existing.Value.Count - 1;
                Run(connection, "UPDATE rechunk_ref SET count = $n, state = $state WHERE scope = $s AND chunk = $c",
                    ("$n", left), ("$state", left == 0 ? Orphaned : Active), ("$s", request.Scope), ("$c", target));
                if (left == 0)
                {
                    orphaned.Add(new ChunkId(target));
                }
            }

            foreach (var entry in request.Entries.Where(e => e.Id != e.Target))
            {
                Run(connection, "INSERT OR IGNORE INTO rechunk_alias (scope, target, alias) VALUES ($s, $t, $a)",
                    ("$s", request.Scope), ("$t", entry.Target.Value), ("$a", entry.Id.Value));
            }

            Run(connection, "DELETE FROM rechunk_doc_chunk WHERE scope = $s AND document = $d",
                ("$s", request.Scope), ("$d", request.Document.Value));
            var seq = 0;
            foreach (var e in request.Entries)
            {
                Run(connection, """
                    INSERT INTO rechunk_doc_chunk (scope, document, seq, ordinal, id, target, language, first_revision, previous_ordinal, replaces)
                    VALUES ($s, $d, $seq, $o, $id, $t, $l, $f, $p, $r)
                    """,
                    ("$s", request.Scope), ("$d", request.Document.Value), ("$seq", seq++), ("$o", e.Ordinal), ("$id", e.Id.Value),
                    ("$t", e.Target.Value), ("$l", e.Language), ("$f", e.FirstRevision), ("$p", e.PreviousOrdinal), ("$r", e.Replaces?.Value));
            }

            var revision = currentRevision + 1;
            Run(connection, """
                INSERT INTO rechunk_doc (scope, document, revision, updated_at) VALUES ($s, $d, $rev, $at)
                ON CONFLICT (scope, document) DO UPDATE SET revision = $rev, updated_at = $at
                """,
                ("$s", request.Scope), ("$d", request.Document.Value), ("$rev", revision), ("$at", request.At.UtcTicks));

            return new CommitRevisionResult(CommitRevisionStatus.Committed, revision, orphaned, Array.Empty<ChunkId>());
        });

        return ValueTask.FromResult(result);
    }

    public ValueTask<IReadOnlyList<ChunkId>> ListPendingAsync(string scope, DateTimeOffset now, int take, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT chunk FROM rechunk_ref
            WHERE scope = $s AND count = 0 AND (state = $orphaned OR (state = $retiring AND lease_until <= $now))
            ORDER BY chunk LIMIT $take
            """;
        command.Parameters.AddWithValue("$s", scope);
        command.Parameters.AddWithValue("$orphaned", Orphaned);
        command.Parameters.AddWithValue("$retiring", Retiring);
        command.Parameters.AddWithValue("$now", now.UtcTicks);
        command.Parameters.AddWithValue("$take", Math.Max(0, take));
        var list = new List<ChunkId>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ChunkId(reader.GetString(0)));
        }

        return ValueTask.FromResult<IReadOnlyList<ChunkId>>(list);
    }

    public ValueTask<RetireClaim?> BeginRetireAsync(
        string scope, ChunkId chunk, string holder, TimeSpan lease, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(holder);
        ct.ThrowIfCancellationRequested();

        var claim = _db.InTransaction<RetireClaim?>(connection =>
        {
            var row = ReadRef(connection, scope, chunk.Value);
            if (row is null || row.Value.Count != 0)
            {
                return null;
            }

            var free = row.Value.State == ChunkRefState.Orphaned
                || (row.Value.State == ChunkRefState.Retiring && (row.Value.Holder == holder || row.Value.LeaseUntil <= now.UtcTicks));
            if (!free)
            {
                return null;
            }

            Run(connection, "UPDATE rechunk_ref SET state = $state, holder = $h, lease_until = $until WHERE scope = $s AND chunk = $c",
                ("$state", Retiring), ("$h", holder), ("$until", (now + lease).UtcTicks), ("$s", scope), ("$c", chunk.Value));

            var aliases = new List<ChunkId>();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT alias FROM rechunk_alias WHERE scope = $s AND target = $t ORDER BY alias";
            command.Parameters.AddWithValue("$s", scope);
            command.Parameters.AddWithValue("$t", chunk.Value);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                aliases.Add(new ChunkId(reader.GetString(0)));
            }

            return new RetireClaim(chunk, aliases);
        });

        return ValueTask.FromResult(claim);
    }

    public ValueTask<bool> FinishRetireAsync(string scope, ChunkId chunk, string holder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(holder);
        ct.ThrowIfCancellationRequested();

        var done = _db.InTransaction(connection =>
        {
            var row = ReadRef(connection, scope, chunk.Value);
            if (row is null || row.Value.Count != 0 || row.Value.State != ChunkRefState.Retiring || row.Value.Holder != holder)
            {
                return false;
            }

            Run(connection, "DELETE FROM rechunk_ref WHERE scope = $s AND chunk = $c", ("$s", scope), ("$c", chunk.Value));
            Run(connection, "DELETE FROM rechunk_alias WHERE scope = $s AND target = $c", ("$s", scope), ("$c", chunk.Value));
            return true;
        });

        return ValueTask.FromResult(done);
    }

    public ValueTask<ChunkRef?> GetRefAsync(string scope, ChunkId chunk, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        using var connection = _db.Open();
        var row = ReadRef(connection, scope, chunk.Value);
        ChunkRef? found = row is null ? null : new ChunkRef(chunk, row.Value.Count, row.Value.State);
        return ValueTask.FromResult(found);
    }

    public ValueTask<IReadOnlyList<ChunkRef>> ListRefsAsync(string scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT chunk, count, state FROM rechunk_ref WHERE scope = $s ORDER BY chunk";
        command.Parameters.AddWithValue("$s", scope);
        var list = new List<ChunkRef>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ChunkRef(new ChunkId(reader.GetString(0)), reader.GetInt32(1), (ChunkRefState)reader.GetInt32(2)));
        }

        return ValueTask.FromResult<IReadOnlyList<ChunkRef>>(list);
    }

    private readonly record struct RefRow(int Count, ChunkRefState State, string Holder, long LeaseUntil);

    private static RefRow? ReadRef(SqliteConnection connection, string scope, string chunk)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count, state, holder, lease_until FROM rechunk_ref WHERE scope = $s AND chunk = $c";
        command.Parameters.AddWithValue("$s", scope);
        command.Parameters.AddWithValue("$c", chunk);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new RefRow(reader.GetInt32(0), (ChunkRefState)reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3))
            : null;
    }

    private static DocumentManifest? ReadManifest(SqliteConnection connection, string scope, DocumentId document)
    {
        int revision;
        long updated;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT revision, updated_at FROM rechunk_doc WHERE scope = $s AND document = $d";
            command.Parameters.AddWithValue("$s", scope);
            command.Parameters.AddWithValue("$d", document.Value);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            revision = reader.GetInt32(0);
            updated = reader.GetInt64(1);
        }

        var entries = new List<ManifestEntry>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT ordinal, id, target, language, first_revision, previous_ordinal, replaces
                FROM rechunk_doc_chunk WHERE scope = $s AND document = $d ORDER BY seq
                """;
            command.Parameters.AddWithValue("$s", scope);
            command.Parameters.AddWithValue("$d", document.Value);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new ManifestEntry(
                    reader.GetInt32(0),
                    new ChunkId(reader.GetString(1)),
                    new ChunkId(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : new ChunkId(reader.GetString(6))));
            }
        }

        return new DocumentManifest(scope, document, revision, entries, new DateTimeOffset(updated, TimeSpan.Zero));
    }

    private static void Run(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }
}
