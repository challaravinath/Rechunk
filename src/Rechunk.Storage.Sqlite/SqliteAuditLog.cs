using System.Text;
using Microsoft.Data.Sqlite;

namespace Rechunk.Storage.Sqlite;

/// <summary>
/// The audit log in a SQLite file. An append is one transaction and ignores an event id it has seen, so writing the same
/// event twice stores it once. The table holds the id, time, scope, document, chunk, source and kind as columns for
/// queries, and the event as JSON. There is no column for chunk text.
/// </summary>
public sealed class SqliteAuditLog : IAuditLog
{
    private readonly SqliteDatabase _db;

    public SqliteAuditLog(SqliteStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _db = new SqliteDatabase(options.ConnectionString);
    }

    public SqliteAuditLog(string connectionString)
        : this(new SqliteStorageOptions { ConnectionString = connectionString })
    {
    }

    public ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        return AppendManyAsync(new[] { auditEvent }, ct);
    }

    public ValueTask AppendManyAsync(IReadOnlyList<AuditEvent> events, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ct.ThrowIfCancellationRequested();
        if (events.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        _db.InTransaction(connection =>
        {
            foreach (var e in events)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT OR IGNORE INTO rechunk_audit (id, at_ms, scope, document, chunk, source, kind, body)
                    VALUES ($id, $at, $scope, $doc, $chunk, $source, $kind, $body)
                    """;
                insert.Parameters.AddWithValue("$id", e.Id);
                insert.Parameters.AddWithValue("$at", AuditQuery.Ms(e.At));
                insert.Parameters.AddWithValue("$scope", e.Scope);
                insert.Parameters.AddWithValue("$doc", e.Document.Value ?? string.Empty);
                insert.Parameters.AddWithValue("$chunk", e.Chunk.Value ?? string.Empty);
                insert.Parameters.AddWithValue("$source", e.Source);
                insert.Parameters.AddWithValue("$kind", (int)e.Kind);
                insert.Parameters.AddWithValue("$body", AuditEventJson.Serialize(e));
                if (insert.ExecuteNonQuery() == 0)
                {
                    continue;
                }

                foreach (var type in e.Findings.Select(f => f.Type.Name).Distinct(StringComparer.Ordinal))
                {
                    using var finding = connection.CreateCommand();
                    finding.CommandText = "INSERT OR IGNORE INTO rechunk_audit_finding (type, event_id) VALUES ($t, $id)";
                    finding.Parameters.AddWithValue("$t", type);
                    finding.Parameters.AddWithValue("$id", e.Id);
                    finding.ExecuteNonQuery();
                }
            }

            return 0;
        });

        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<AuditEvent> QueryAsync(
        AuditQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();

        // read everything for this page first, so no connection stays open while the caller consumes the sequence
        var found = new List<AuditEvent>();
        using (var connection = _db.Open())
        using (var command = connection.CreateCommand())
        {
            var sql = new StringBuilder("SELECT a.body FROM rechunk_audit a WHERE 1 = 1");
            void Filter(string clause, string name, object? value)
            {
                sql.Append(" AND ").Append(clause);
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            if (query.Document is { } document)
            {
                Filter("a.document = $document", "$document", document.Value ?? string.Empty);
            }

            if (query.Chunk is { } chunk)
            {
                Filter("a.chunk = $chunk", "$chunk", chunk.Value ?? string.Empty);
            }

            if (query.From is { } from)
            {
                Filter("a.at_ms >= $from", "$from", AuditQuery.Ms(from));
            }

            if (query.To is { } to)
            {
                Filter("a.at_ms <= $to", "$to", AuditQuery.Ms(to));
            }

            if (query.Source is not null)
            {
                Filter("a.source = $source", "$source", query.Source);
            }

            if (query.Scope is not null)
            {
                Filter("a.scope = $scope", "$scope", query.Scope);
            }

            if (query.Kind is { } kind)
            {
                Filter("a.kind = $kind", "$kind", (int)kind);
            }

            if (query.FindingType is { } type)
            {
                Filter("EXISTS (SELECT 1 FROM rechunk_audit_finding f WHERE f.event_id = a.id AND f.type = $type)", "$type", type.Name);
            }

            sql.Append(" ORDER BY a.at_ms, a.id COLLATE BINARY LIMIT $take OFFSET $skip");
            command.Parameters.AddWithValue("$take", query.Take ?? -1);
            command.Parameters.AddWithValue("$skip", Math.Max(0, query.Skip));
            command.CommandText = sql.ToString();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                found.Add(AuditEventJson.Deserialize(reader.GetString(0)));
            }
        }

        foreach (var e in found)
        {
            ct.ThrowIfCancellationRequested();
            yield return e;
        }
    }
}

/// <summary>The audit outbox in the same SQLite file, so events that could not be written survive a restart.</summary>
public sealed class SqliteAuditOutbox : IAuditOutbox
{
    private readonly SqliteDatabase _db;

    public SqliteAuditOutbox(SqliteStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _db = new SqliteDatabase(options.ConnectionString);
    }

    public SqliteAuditOutbox(string connectionString)
        : this(new SqliteStorageOptions { ConnectionString = connectionString })
    {
    }

    public ValueTask EnqueueAsync(IReadOnlyList<AuditEvent> events, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ct.ThrowIfCancellationRequested();
        if (events.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        _db.InTransaction(connection =>
        {
            foreach (var e in events)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT OR IGNORE INTO rechunk_audit_outbox (id, body) VALUES ($id, $body)";
                command.Parameters.AddWithValue("$id", e.Id);
                command.Parameters.AddWithValue("$body", AuditEventJson.Serialize(e));
                command.ExecuteNonQuery();
            }

            return 0;
        });
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<AuditEvent>> PeekAsync(int max, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM rechunk_audit_outbox ORDER BY seq LIMIT $max";
        command.Parameters.AddWithValue("$max", Math.Max(0, max));
        var list = new List<AuditEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(AuditEventJson.Deserialize(reader.GetString(0)));
        }

        return ValueTask.FromResult<IReadOnlyList<AuditEvent>>(list);
    }

    public ValueTask RemoveAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ct.ThrowIfCancellationRequested();
        if (ids.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        _db.InTransaction(connection =>
        {
            foreach (var id in ids)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM rechunk_audit_outbox WHERE id = $id";
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }

            return 0;
        });
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> CountAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM rechunk_audit_outbox";
        return ValueTask.FromResult(Convert.ToInt32(command.ExecuteScalar()));
    }
}
