using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Rechunk.Storage.SqlServer;

/// <summary>
/// Document revisions in SQL Server. A commit takes one exclusive lock on the document, then locks the chunk
/// rows it will change with UPDLOCK and HOLDLOCK, in chunk name order. Two workers cannot both commit the same
/// revision, and two documents that share a chunk do not deadlock.
/// </summary>
public sealed class SqlServerRevisionStore : IRevisionStore
{
    private const byte Active = (byte)ChunkRefState.Active;
    private const byte Orphaned = (byte)ChunkRefState.Orphaned;
    private const byte Retiring = (byte)ChunkRefState.Retiring;

    private readonly SqlServerDatabase _db;

    public SqlServerRevisionStore(SqlServerStorageOptions options)
    {
        _db = new SqlServerDatabase(options);
    }

    public SqlServerRevisionStore(string connectionString)
        : this(new SqlServerStorageOptions { ConnectionString = connectionString })
    {
    }

    public async ValueTask<DocumentManifest?> GetAsync(string scope, DocumentId document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        return await ReadManifestAsync(connection, null, scope, document, ct).ConfigureAwait(false);
    }

    public ValueTask<CommitRevisionResult> CommitAsync(CommitRevisionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        RevisionRules.Check(request);

        return new ValueTask<CommitRevisionResult>(_db.InTransactionAsync(async (connection, transaction, token) =>
        {
            await LockDocumentAsync(connection, transaction, request.Scope, request.Document.Value, token).ConfigureAwait(false);
            var current = await ReadManifestAsync(connection, transaction, request.Scope, request.Document, token).ConfigureAwait(false);
            var currentRevision = current?.Revision ?? 0;
            if (currentRevision != request.ExpectedRevision)
            {
                return new CommitRevisionResult(CommitRevisionStatus.Conflict, currentRevision, Array.Empty<ChunkId>(), Array.Empty<ChunkId>());
            }

            var (added, removed) = RevisionRules.Diff(current?.Entries, request.Entries);
            var locked = new Dictionary<string, RefRow?>(StringComparer.Ordinal);
            foreach (var chunk in added.Concat(removed).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal))
            {
                locked[chunk] = await ReadRefAsync(connection, transaction, request.Scope, chunk, lockRow: true, token).ConfigureAwait(false);
            }

            var blocking = new List<ChunkId>();
            foreach (var target in added)
            {
                if (locked[target]?.State == ChunkRefState.Retiring)
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
                await using var upsert = SqlServerDatabase.Command(connection, transaction,
                    "MERGE " + _db.Names.Ref + " WITH (HOLDLOCK) AS t " +
                    "USING (SELECT @s AS scope, HASHBYTES('SHA2_256', @c) AS chunk_key, @c AS chunk) AS s " +
                    "ON t.scope = s.scope AND t.chunk_key = s.chunk_key AND t.chunk = s.chunk " +
                    "WHEN MATCHED THEN UPDATE SET count = t.count + 1, state = @state " +
                    "WHEN NOT MATCHED THEN INSERT (scope, chunk, count, state, holder, lease_until) VALUES (@s, @c, 1, @state, N'', 0);");
                upsert.Parameters.AddWithValue("@s", request.Scope);
                upsert.Parameters.AddWithValue("@c", target);
                upsert.Parameters.AddWithValue("@state", Active);
                await upsert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            var orphaned = new List<ChunkId>();
            foreach (var target in removed)
            {
                var existing = locked[target];
                if (existing is null || existing.Value.Count <= 0)
                {
                    continue;
                }

                var left = existing.Value.Count - 1;
                await using var update = SqlServerDatabase.Command(connection, transaction,
                    "UPDATE " + _db.Names.Ref + " SET count = @n, state = @state WHERE scope = @s AND chunk_key = HASHBYTES('SHA2_256', @c) AND chunk = @c");
                update.Parameters.AddWithValue("@n", left);
                update.Parameters.AddWithValue("@state", left == 0 ? Orphaned : Active);
                update.Parameters.AddWithValue("@s", request.Scope);
                update.Parameters.AddWithValue("@c", target);
                await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                if (left == 0)
                {
                    orphaned.Add(new ChunkId(target));
                }
            }

            foreach (var entry in request.Entries.Where(e => e.Id != e.Target).OrderBy(e => e.Id.Value, StringComparer.Ordinal))
            {
                await InsertAliasAsync(connection, transaction, request.Scope, entry.Target.Value, entry.Id.Value, token).ConfigureAwait(false);
            }

            await using (var delete = SqlServerDatabase.Command(connection, transaction,
                "DELETE FROM " + _db.Names.DocChunk + " WHERE scope = @s AND document_key = HASHBYTES('SHA2_256', @d) AND document = @d"))
            {
                delete.Parameters.AddWithValue("@s", request.Scope);
                delete.Parameters.AddWithValue("@d", request.Document.Value);
                await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            var seq = 0;
            foreach (var entry in request.Entries)
            {
                await using var insert = SqlServerDatabase.Command(connection, transaction,
                    "INSERT INTO " + _db.Names.DocChunk +
                    " (scope, document, seq, ordinal, id, target, language, first_revision, previous_ordinal, replaces) " +
                    "VALUES (@s, @d, @seq, @o, @id, @t, @l, @f, @p, @r)");
                insert.Parameters.AddWithValue("@s", request.Scope);
                insert.Parameters.AddWithValue("@d", request.Document.Value);
                insert.Parameters.AddWithValue("@seq", seq++);
                insert.Parameters.AddWithValue("@o", entry.Ordinal);
                insert.Parameters.AddWithValue("@id", entry.Id.Value);
                insert.Parameters.AddWithValue("@t", entry.Target.Value);
                insert.Parameters.AddWithValue("@l", (object?)entry.Language ?? DBNull.Value);
                insert.Parameters.AddWithValue("@f", entry.FirstRevision);
                insert.Parameters.AddWithValue("@p", (object?)entry.PreviousOrdinal ?? DBNull.Value);
                insert.Parameters.AddWithValue("@r", (object?)entry.Replaces?.Value ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            var revision = currentRevision + 1;
            await using (var doc = SqlServerDatabase.Command(connection, transaction,
                "UPDATE " + _db.Names.Doc + " SET revision = @rev, updated_at = @at, source = COALESCE(@src, source) WHERE scope = @s AND document_key = HASHBYTES('SHA2_256', @d) AND document = @d; " +
                "IF @@ROWCOUNT = 0 INSERT INTO " + _db.Names.Doc + " (scope, document, revision, updated_at, source) VALUES (@s, @d, @rev, @at, @src);"))
            {
                doc.Parameters.AddWithValue("@s", request.Scope);
                doc.Parameters.AddWithValue("@d", request.Document.Value);
                doc.Parameters.AddWithValue("@rev", revision);
                doc.Parameters.AddWithValue("@at", request.At.UtcTicks);
                doc.Parameters.AddWithValue("@src", (object?)request.Source ?? DBNull.Value);
                await doc.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            return new CommitRevisionResult(CommitRevisionStatus.Committed, revision, orphaned, Array.Empty<ChunkId>());
        }, ct));
    }

    public async ValueTask<IReadOnlyList<ChunkId>> ListPendingAsync(string scope, DateTimeOffset now, int take, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = SqlServerDatabase.Command(connection, null,
            "SELECT TOP (@take) chunk FROM " + _db.Names.Ref +
            " WHERE scope = @s AND count = 0 AND (state = @orphaned OR (state = @retiring AND lease_until <= @now)) ORDER BY chunk");
        command.Parameters.AddWithValue("@s", scope);
        command.Parameters.AddWithValue("@orphaned", Orphaned);
        command.Parameters.AddWithValue("@retiring", Retiring);
        command.Parameters.AddWithValue("@now", now.UtcTicks);
        command.Parameters.AddWithValue("@take", Math.Max(0, take));
        return await ReadChunksAsync(command, ct).ConfigureAwait(false);
    }

    public ValueTask<RetireClaim?> BeginRetireAsync(string scope, ChunkId chunk, string holder, TimeSpan lease, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(holder);
        ct.ThrowIfCancellationRequested();

        return new ValueTask<RetireClaim?>(_db.InTransactionAsync(async (connection, transaction, token) =>
        {
            var row = await ReadRefAsync(connection, transaction, scope, chunk.Value, lockRow: true, token).ConfigureAwait(false);
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

            await using (var update = SqlServerDatabase.Command(connection, transaction,
                "UPDATE " + _db.Names.Ref + " SET state = @state, holder = @h, lease_until = @until WHERE scope = @s AND chunk_key = HASHBYTES('SHA2_256', @c) AND chunk = @c"))
            {
                update.Parameters.AddWithValue("@state", Retiring);
                update.Parameters.AddWithValue("@h", holder);
                update.Parameters.AddWithValue("@until", (now + lease).UtcTicks);
                update.Parameters.AddWithValue("@s", scope);
                update.Parameters.AddWithValue("@c", chunk.Value);
                await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await using var command = SqlServerDatabase.Command(connection, transaction,
                "SELECT alias FROM " + _db.Names.Alias + " WHERE scope = @s AND target_key = HASHBYTES('SHA2_256', @t) AND target = @t ORDER BY alias");
            command.Parameters.AddWithValue("@s", scope);
            command.Parameters.AddWithValue("@t", chunk.Value);
            var aliases = await ReadChunksAsync(command, token).ConfigureAwait(false);
            return (RetireClaim?)new RetireClaim(chunk, aliases);
        }, ct));
    }

    public ValueTask<bool> FinishRetireAsync(string scope, ChunkId chunk, string holder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(holder);
        ct.ThrowIfCancellationRequested();

        return new ValueTask<bool>(_db.InTransactionAsync(async (connection, transaction, token) =>
        {
            var row = await ReadRefAsync(connection, transaction, scope, chunk.Value, lockRow: true, token).ConfigureAwait(false);
            if (row is null || row.Value.Count != 0 || row.Value.State != ChunkRefState.Retiring || row.Value.Holder != holder)
            {
                return false;
            }

            await using (var delete = SqlServerDatabase.Command(connection, transaction,
                "DELETE FROM " + _db.Names.Ref + " WHERE scope = @s AND chunk_key = HASHBYTES('SHA2_256', @c) AND chunk = @c"))
            {
                delete.Parameters.AddWithValue("@s", scope);
                delete.Parameters.AddWithValue("@c", chunk.Value);
                await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await using var aliases = SqlServerDatabase.Command(connection, transaction,
                "DELETE FROM " + _db.Names.Alias + " WHERE scope = @s AND target_key = HASHBYTES('SHA2_256', @c) AND target = @c");
            aliases.Parameters.AddWithValue("@s", scope);
            aliases.Parameters.AddWithValue("@c", chunk.Value);
            await aliases.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return true;
        }, ct));
    }

    public async ValueTask<ChunkRef?> GetRefAsync(string scope, ChunkId chunk, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var row = await ReadRefAsync(connection, null, scope, chunk.Value, lockRow: false, ct).ConfigureAwait(false);
        return row is null ? null : new ChunkRef(chunk, row.Value.Count, row.Value.State);
    }

    public async ValueTask<IReadOnlyList<ChunkRef>> ListRefsAsync(string scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = SqlServerDatabase.Command(connection, null,
            "SELECT chunk, count, state FROM " + _db.Names.Ref + " WHERE scope = @s ORDER BY chunk");
        command.Parameters.AddWithValue("@s", scope);
        var list = new List<ChunkRef>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ChunkRef(new ChunkId(reader.GetString(0)), reader.GetInt32(1), (ChunkRefState)reader.GetByte(2)));
        }

        return list;
    }

    public async ValueTask<IReadOnlyList<DocumentRef>> ListDocumentsAsync(string scope, string? source, string? afterDocument, int take, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = SqlServerDatabase.Command(connection, null,
            "SELECT TOP (@take) document, revision, source FROM " + _db.Names.Doc +
            " WHERE scope = @s AND (@src IS NULL OR source = @src) AND (@after IS NULL OR document > @after) ORDER BY document");
        command.Parameters.AddWithValue("@s", scope);
        command.Parameters.AddWithValue("@src", (object?)source ?? DBNull.Value);
        command.Parameters.AddWithValue("@after", (object?)afterDocument ?? DBNull.Value);
        command.Parameters.AddWithValue("@take", Math.Max(0, take));
        return await ReadDocumentsAsync(command, ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<DocumentRef>> ListDocumentsUsingAsync(string scope, IReadOnlyCollection<ChunkId> chunks, string? afterDocument, int take, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(chunks);
        ct.ThrowIfCancellationRequested();
        if (chunks.Count == 0)
        {
            return Array.Empty<DocumentRef>();
        }

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = SqlServerDatabase.Command(connection, null,
            "SELECT TOP (@take) d.document, d.revision, d.source FROM " + _db.Names.Doc + " d " +
            "WHERE d.scope = @s AND (@after IS NULL OR d.document > @after) AND EXISTS (" +
            "SELECT 1 FROM " + _db.Names.DocChunk + " c WHERE c.scope = d.scope AND c.document = d.document " +
            "AND c.document_key = d.document_key AND (" +
            "(c.id_key IN (SELECT HASHBYTES('SHA2_256', value) FROM OPENJSON(@ids)) AND c.id IN (SELECT value FROM OPENJSON(@ids))) OR " +
            "(c.target_key IN (SELECT HASHBYTES('SHA2_256', value) FROM OPENJSON(@ids)) AND c.target IN (SELECT value FROM OPENJSON(@ids)))) ORDER BY d.document");
        command.Parameters.AddWithValue("@s", scope);
        command.Parameters.AddWithValue("@after", (object?)afterDocument ?? DBNull.Value);
        command.Parameters.AddWithValue("@take", Math.Max(0, take));
        command.Parameters.AddWithValue("@ids", JsonSerializer.Serialize(chunks.Select(c => c.Value)));
        return await ReadDocumentsAsync(command, ct).ConfigureAwait(false);
    }

    private async Task LockDocumentAsync(SqlConnection connection, SqlTransaction transaction, string scope, string document, CancellationToken ct)
    {
        var resource = "rechunk/" + _db.Names.Doc + "/" + scope + "/" + document;
        if (resource.Length > 255)
        {
            resource = "rechunk/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resource)));
        }

        await using var command = SqlServerDatabase.Command(connection, transaction, """
            DECLARE @result int;
            EXEC @result = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
            IF @result < 0 THROW 50001, N'Could not lock the document.', 1;
            """);
        command.Parameters.AddWithValue("@resource", resource);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<DocumentManifest?> ReadManifestAsync(SqlConnection connection, SqlTransaction? transaction, string scope, DocumentId document, CancellationToken ct)
    {
        int revision;
        long updated;
        string? source;
        await using (var command = SqlServerDatabase.Command(connection, transaction,
            "SELECT revision, updated_at, source FROM " + _db.Names.Doc + " WITH (UPDLOCK, HOLDLOCK) WHERE scope = @s AND document_key = HASHBYTES('SHA2_256', @d) AND document = @d"))
        {
            if (transaction is null)
            {
                command.CommandText = "SELECT revision, updated_at, source FROM " + _db.Names.Doc + " WHERE scope = @s AND document_key = HASHBYTES('SHA2_256', @d) AND document = @d";
            }

            command.Parameters.AddWithValue("@s", scope);
            command.Parameters.AddWithValue("@d", document.Value);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            revision = reader.GetInt32(0);
            updated = reader.GetInt64(1);
            source = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        var entries = new List<ManifestEntry>();
        await using (var command = SqlServerDatabase.Command(connection, transaction,
            "SELECT ordinal, id, target, language, first_revision, previous_ordinal, replaces FROM " + _db.Names.DocChunk +
            " WHERE scope = @s AND document_key = HASHBYTES('SHA2_256', @d) AND document = @d ORDER BY seq"))
        {
            command.Parameters.AddWithValue("@s", scope);
            command.Parameters.AddWithValue("@d", document.Value);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
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

        return new DocumentManifest(scope, document, revision, entries, new DateTimeOffset(updated, TimeSpan.Zero), source);
    }

    private async Task<RefRow?> ReadRefAsync(SqlConnection connection, SqlTransaction? transaction, string scope, string chunk, bool lockRow, CancellationToken ct)
    {
        var sql = "SELECT count, state, holder, lease_until FROM " + _db.Names.Ref
            + (lockRow ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty)
            + " WHERE scope = @s AND chunk_key = HASHBYTES('SHA2_256', @c) AND chunk = @c";
        await using var command = SqlServerDatabase.Command(connection, transaction, sql);
        command.Parameters.AddWithValue("@s", scope);
        command.Parameters.AddWithValue("@c", chunk);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new RefRow(reader.GetInt32(0), (ChunkRefState)reader.GetByte(1), reader.GetString(2), reader.GetInt64(3));
    }

    private async Task InsertAliasAsync(SqlConnection connection, SqlTransaction transaction, string scope, string target, string alias, CancellationToken ct)
    {
        try
        {
            await using var command = SqlServerDatabase.Command(connection, transaction,
                "INSERT INTO " + _db.Names.Alias + " (scope, target, alias) VALUES (@s, @t, @a)");
            command.Parameters.AddWithValue("@s", scope);
            command.Parameters.AddWithValue("@t", target);
            command.Parameters.AddWithValue("@a", alias);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (SqlServerDatabase.IsDuplicateKey(ex))
        {
            // the same alias was already recorded for this chunk
        }
    }

    private static async Task<IReadOnlyList<ChunkId>> ReadChunksAsync(SqlCommand command, CancellationToken ct)
    {
        var list = new List<ChunkId>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ChunkId(reader.GetString(0)));
        }

        return list;
    }

    private static async Task<IReadOnlyList<DocumentRef>> ReadDocumentsAsync(SqlCommand command, CancellationToken ct)
    {
        var list = new List<DocumentRef>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new DocumentRef(new DocumentId(reader.GetString(0)), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return list;
    }

    private readonly record struct RefRow(int Count, ChunkRefState State, string Holder, long LeaseUntil);
}
