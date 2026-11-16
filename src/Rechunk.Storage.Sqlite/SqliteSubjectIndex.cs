using Microsoft.Data.Sqlite;

namespace Rechunk.Storage.Sqlite;

/// <summary>
/// Subject links in the Rechunk SQLite file. The table holds the scope, the chunk id, the kind, and the keyed subject id.
/// The value the id was made from is not stored.
/// </summary>
public sealed class SqliteSubjectIndex : ISubjectIndex
{
    private readonly SqliteDatabase _db;

    public SqliteSubjectIndex(string connectionString)
    {
        _db = new SqliteDatabase(connectionString);
    }

    public SqliteSubjectIndex(SqliteStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _db = new SqliteDatabase(options.ConnectionString);
    }

    public ValueTask LinkAsync(string scope, ChunkId chunk, IReadOnlyList<SubjectKey> subjects, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Require(scope, chunk);
        _db.InTransaction(connection =>
        {
            Delete(connection, scope, chunk.Value);
            Insert(connection, scope, chunk.Value, subjects);
            return true;
        });
        return ValueTask.CompletedTask;
    }

    public ValueTask AddAsync(string scope, ChunkId chunk, IReadOnlyList<SubjectKey> subjects, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Require(scope, chunk);
        if (subjects is null || subjects.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        _db.InTransaction(connection =>
        {
            Insert(connection, scope, chunk.Value, subjects);
            return true;
        });
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string scope, ChunkId chunk, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Require(scope, chunk);
        _db.InTransaction(connection =>
        {
            Delete(connection, scope, chunk.Value);
            return true;
        });
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<ChunkId>> ListChunksAsync(string scope, SubjectKey subject, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RequireScope(scope);
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT chunk FROM rechunk_subject
            WHERE scope = $scope AND subject = $subject AND kind = $kind
            ORDER BY chunk
            """;
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$subject", subject.Id);
        command.Parameters.AddWithValue("$kind", subject.Kind);
        using var reader = command.ExecuteReader();
        var list = new List<ChunkId>();
        while (reader.Read())
        {
            list.Add(new ChunkId(reader.GetString(0)));
        }

        return ValueTask.FromResult<IReadOnlyList<ChunkId>>(list);
    }

    public ValueTask<int> CountAsync(string scope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RequireScope(scope);
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM rechunk_subject WHERE scope = $scope";
        command.Parameters.AddWithValue("$scope", scope);
        return ValueTask.FromResult(Convert.ToInt32(command.ExecuteScalar()));
    }

    public ValueTask<int> CountAsync(string scope, SubjectKey subject, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RequireScope(scope);
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM rechunk_subject WHERE scope = $scope AND subject = $subject AND kind = $kind";
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$subject", subject.Id);
        command.Parameters.AddWithValue("$kind", subject.Kind);
        return ValueTask.FromResult(Convert.ToInt32(command.ExecuteScalar()));
    }

    private static void Delete(SqliteConnection connection, string scope, string chunk)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM rechunk_subject WHERE scope = $scope AND chunk = $chunk";
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$chunk", chunk);
        command.ExecuteNonQuery();
    }

    private static void Insert(SqliteConnection connection, string scope, string chunk, IReadOnlyList<SubjectKey> subjects)
    {
        if (subjects is null)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO rechunk_subject (scope, chunk, kind, subject)
            VALUES ($scope, $chunk, $kind, $subject)
            """;
        var scopeParam = command.Parameters.Add("$scope", SqliteType.Text);
        var chunkParam = command.Parameters.Add("$chunk", SqliteType.Text);
        var kindParam = command.Parameters.Add("$kind", SqliteType.Text);
        var subjectParam = command.Parameters.Add("$subject", SqliteType.Text);
        scopeParam.Value = scope;
        chunkParam.Value = chunk;
        foreach (var subject in subjects)
        {
            kindParam.Value = subject.Kind;
            subjectParam.Value = subject.Id;
            command.ExecuteNonQuery();
        }
    }

    private static void Require(string scope, ChunkId chunk)
    {
        RequireScope(scope);
        if (string.IsNullOrEmpty(chunk.Value))
        {
            throw new ArgumentException("Chunk id cannot be empty.", nameof(chunk));
        }
    }

    private static void RequireScope(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException("Scope cannot be empty.", nameof(scope));
        }
    }
}
