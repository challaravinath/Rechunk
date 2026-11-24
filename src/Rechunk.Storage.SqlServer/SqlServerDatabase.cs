using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Rechunk.Storage.SqlServer;

/// <summary>
/// Opens connections, creates the tables once, and retries the small set of errors Azure SQL uses when it is busy.
/// </summary>
internal sealed class SqlServerDatabase
{
    private static readonly int[] Transient = { 20, 64, 121, 233, 1205, 1222, 4060, 4221, 10053, 10054, 10060, 10928, 10929, 10936, 40143, 40197, 40501, 40540, 40613, 49918, 49919, 49920 };
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, byte> Ready = new(StringComparer.Ordinal);
    private static readonly string Script = ReadScript();

    /// <summary>The CREATE TABLE script, with {{SCHEMA}} where the schema name goes.</summary>
    public static string SchemaSql => Script;

    private readonly SqlServerStorageOptions _options;
    private readonly string _schema;

    public SqlServerDatabase(SqlServerStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ConnectionFactory is null && string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentException("Set ConnectionString or ConnectionFactory.", nameof(options));
        }

        if (options.MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxAttempts has to be at least 1.");
        }

        _options = options;
        _schema = Quote(options.Schema);
        Names = new SqlNames(_schema);
    }

    public SqlNames Names { get; }

    public Task<T> InTransactionAsync<T>(Func<SqlConnection, SqlTransaction, CancellationToken, Task<T>> work, CancellationToken ct) =>
        RetryAsync(async () =>
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);
            try
            {
                var result = await work(connection, transaction, ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return result;
            }
            catch
            {
                try
                {
                    await transaction.RollbackAsync(ct).ConfigureAwait(false);
                }
                catch (SqlException)
                {
                    // the original error is the one worth seeing
                }

                throw;
            }
        }, ct);

    public async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        SqlConnection connection;
        if (_options.ConnectionFactory is { } factory)
        {
            connection = await factory(ct).ConfigureAwait(false) ?? throw new InvalidOperationException("ConnectionFactory returned null.");
        }
        else
        {
            connection = new SqlConnection(_options.ConnectionString);
        }

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        if (_options.CreateTables)
        {
            await EnsureSchemaAsync(connection, ct).ConfigureAwait(false);
        }

        return connection;
    }

    public static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 30;
        return command;
    }

    public static bool IsDuplicateKey(SqlException exception) => exception.Number is 2601 or 2627;

    private async Task EnsureSchemaAsync(SqlConnection connection, CancellationToken ct)
    {
        var key = connection.DataSource + "|" + connection.Database + "|" + _options.Schema;
        if (Ready.ContainsKey(key))
        {
            return;
        }

        await SchemaGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Ready.ContainsKey(key))
            {
                return;
            }

            var sql = Script.Replace("{{SCHEMA}}", _options.Schema, StringComparison.Ordinal);
            await using var command = Command(connection, null, sql);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            Ready[key] = 1;
        }
        finally
        {
            SchemaGate.Release();
        }
    }

    private async Task<T> RetryAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await work().ConfigureAwait(false);
            }
            catch (SqlException ex) when (attempt < _options.MaxAttempts && IsTransient(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(SqlException exception)
    {
        foreach (SqlError error in exception.Errors)
        {
            if (Array.IndexOf(Transient, error.Number) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    internal static string Quote(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema) || schema.Length > 128 || !Regex.IsMatch(schema, "^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            throw new ArgumentException("Schema name has to be a simple SQL identifier.", nameof(schema));
        }

        return "[" + schema + "]";
    }

    private static string ReadScript()
    {
        var name = typeof(SqlServerDatabase).Assembly.GetManifestResourceNames().Single(n => n.EndsWith("Schema.sql", StringComparison.Ordinal));
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException("Schema.sql is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

internal sealed class SqlNames
{
    public SqlNames(string schema)
    {
        Exact = schema + ".[exact]";
        ScopeVersion = schema + ".[scope_version]";
        BandEntry = schema + ".[band_entry]";
        BandKey = schema + ".[band_key]";
        Doc = schema + ".[doc]";
        DocChunk = schema + ".[doc_chunk]";
        Ref = schema + ".[ref]";
        Alias = schema + ".[alias]";
    }

    public string Exact { get; }

    public string ScopeVersion { get; }

    public string BandEntry { get; }

    public string BandKey { get; }

    public string Doc { get; }

    public string DocChunk { get; }

    public string Ref { get; }

    public string Alias { get; }
}
