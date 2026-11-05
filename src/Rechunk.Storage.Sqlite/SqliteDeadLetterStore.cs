using Microsoft.Data.Sqlite;

namespace Rechunk.Storage.Sqlite;

/// <summary>Dead letters in the same SQLite file as the fingerprints, or in a file of their own.</summary>
public sealed class SqliteDeadLetterStore : IDeadLetterStore
{
    private readonly SqliteDatabase _db;

    public SqliteDeadLetterStore(SqliteStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _db = new SqliteDatabase(options.ConnectionString);
    }

    public SqliteDeadLetterStore(string connectionString)
        : this(new SqliteStorageOptions { ConnectionString = connectionString })
    {
    }

    public ValueTask AddAsync(DeadLetter deadLetter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deadLetter);
        ct.ThrowIfCancellationRequested();

        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO rechunk_dead_letter (id, scope, chunk, document, ordinal, stage, reason, attempts, failed_at, body, language)
            VALUES ($id, $scope, $chunk, $document, $ordinal, $stage, $reason, $attempts, $failedAt, $body, $language)
            """;
        command.Parameters.AddWithValue("$id", deadLetter.Id);
        command.Parameters.AddWithValue("$scope", deadLetter.Scope);
        command.Parameters.AddWithValue("$chunk", deadLetter.Chunk.Value);
        command.Parameters.AddWithValue("$document", (object?)deadLetter.Document?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$ordinal", deadLetter.Ordinal);
        command.Parameters.AddWithValue("$stage", (int)deadLetter.Stage);
        command.Parameters.AddWithValue("$reason", deadLetter.Reason);
        command.Parameters.AddWithValue("$attempts", deadLetter.Attempts);
        command.Parameters.AddWithValue("$failedAt", deadLetter.FailedAt.UtcTicks);
        command.Parameters.AddWithValue("$body", (object?)deadLetter.Text ?? DBNull.Value);
        command.Parameters.AddWithValue("$language", (object?)deadLetter.Language ?? DBNull.Value);
        command.ExecuteNonQuery();
        return ValueTask.CompletedTask;
    }

    public ValueTask<DeadLetter?> GetAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return ValueTask.FromResult(reader.Read() ? Map(reader) : null);
    }

    public ValueTask<IReadOnlyList<DeadLetter>> ListAsync(DeadLetterQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + Where(command, query) + " ORDER BY failed_at, id LIMIT $take OFFSET $skip";
        command.Parameters.AddWithValue("$take", Math.Max(0, query.Take));
        command.Parameters.AddWithValue("$skip", Math.Max(0, query.Skip));

        var list = new List<DeadLetter>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }

        return ValueTask.FromResult<IReadOnlyList<DeadLetter>>(list);
    }

    public ValueTask<long> CountAsync(DeadLetterQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM rechunk_dead_letter" + Where(command, query);
        return ValueTask.FromResult(Convert.ToInt64(command.ExecuteScalar()));
    }

    public ValueTask<bool> RemoveAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM rechunk_dead_letter WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return ValueTask.FromResult(command.ExecuteNonQuery() > 0);
    }

    private const string Select =
        "SELECT id, scope, chunk, document, ordinal, stage, reason, attempts, failed_at, body, language FROM rechunk_dead_letter";

    private static string Where(SqliteCommand command, DeadLetterQuery q)
    {
        var parts = new List<string>();
        if (q.Scope is not null)
        {
            parts.Add("scope = $fScope");
            command.Parameters.AddWithValue("$fScope", q.Scope);
        }

        if (q.Stage is not null)
        {
            parts.Add("stage = $fStage");
            command.Parameters.AddWithValue("$fStage", (int)q.Stage.Value);
        }

        if (q.Chunk is not null)
        {
            parts.Add("chunk = $fChunk");
            command.Parameters.AddWithValue("$fChunk", q.Chunk.Value.Value);
        }

        if (q.Document is not null)
        {
            parts.Add("document = $fDocument");
            command.Parameters.AddWithValue("$fDocument", q.Document.Value.Value);
        }

        return parts.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", parts);
    }

    private static DeadLetter Map(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        new ChunkId(r.GetString(2)),
        r.IsDBNull(3) ? null : new DocumentId(r.GetString(3)),
        r.GetInt32(4),
        (DeadLetterStage)r.GetInt32(5),
        r.GetString(6),
        r.GetInt32(7),
        new DateTimeOffset(r.GetInt64(8), TimeSpan.Zero),
        r.IsDBNull(9) ? null : r.GetString(9),
        r.IsDBNull(10) ? null : r.GetString(10));
}
