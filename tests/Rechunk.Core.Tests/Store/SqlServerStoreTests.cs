using Microsoft.Data.SqlClient;
using Rechunk.Storage.SqlServer;

namespace Rechunk.Core.Tests.Store;

/// <summary>SQL Server tests run only when RECHUNK_SQLSERVER holds a connection string. Each class uses its own schema.</summary>
internal sealed class SqlServerTestEnvironment : IDisposable
{
    public const string Variable = "RECHUNK_SQLSERVER";

    public SqlServerTestEnvironment()
    {
        var configuration = Environment.GetEnvironmentVariable(Variable);
        Skip.If(string.IsNullOrWhiteSpace(configuration), $"Set {Variable} to a SQL Server connection string to run these tests.");
        ConnectionString = configuration;
        Schema = "t" + Guid.NewGuid().ToString("N");
    }

    public string ConnectionString { get; }

    public string Schema { get; }

    public SqlServerStorageOptions Options => new()
    {
        ConnectionString = ConnectionString,
        Schema = Schema,
    };

    public void Dispose()
    {
    }
}

public sealed class SqlServerFingerprintStoreTests : FingerprintStoreContract
{
    private readonly Lazy<SqlServerTestEnvironment> _sql = new(() => new SqlServerTestEnvironment());

    protected override IFingerprintStore Create(RechunkOptions options, TimeProvider time)
    {
        var env = _sql.Value;
        return new BackedFingerprintStore(new SqlServerFingerprintBackend(env.Options), options, time);
    }

    public override void Dispose()
    {
        if (_sql.IsValueCreated)
        {
            _sql.Value.Dispose();
        }
    }
}

public sealed class SqlServerRevisionStoreTests : RevisionStoreContract
{
    private readonly Lazy<SqlServerTestEnvironment> _sql = new(() => new SqlServerTestEnvironment());

    protected override IRevisionStore Create()
    {
        var env = _sql.Value;
        return new SqlServerRevisionStore(env.Options);
    }

    public override void Dispose()
    {
        if (_sql.IsValueCreated)
        {
            _sql.Value.Dispose();
        }
    }
}

public sealed class SqlServerStorageTests
{
    [SkippableFact]
    public async Task A_connection_factory_is_used_and_closed_after_the_call()
    {
        var configuration = Environment.GetEnvironmentVariable(SqlServerTestEnvironment.Variable);
        Skip.If(string.IsNullOrWhiteSpace(configuration), "Set RECHUNK_SQLSERVER to run this test.");
        var opened = 0;
        var options = new SqlServerStorageOptions
        {
            Schema = "t" + Guid.NewGuid().ToString("N"),
            ConnectionFactory = async ct =>
            {
                opened++;
                var connection = new SqlConnection(configuration);
                await connection.OpenAsync(ct);
                return connection;
            },
        };

        var store = new SqlServerRevisionStore(options);
        Assert.Null(await store.GetAsync("scope", new DocumentId("doc")));
        Assert.Equal(1, opened);
    }

    [SkippableFact]
    public async Task CreateTables_false_leaves_a_missing_schema_alone()
    {
        var configuration = Environment.GetEnvironmentVariable(SqlServerTestEnvironment.Variable);
        Skip.If(string.IsNullOrWhiteSpace(configuration), "Set RECHUNK_SQLSERVER to run this test.");
        var options = new SqlServerStorageOptions
        {
            ConnectionString = configuration,
            Schema = "t" + Guid.NewGuid().ToString("N"),
            CreateTables = false,
        };

        var store = new SqlServerRevisionStore(options);
        var error = await Assert.ThrowsAsync<SqlException>(async () => await store.GetAsync("scope", new DocumentId("doc")));
        Assert.Equal(208, error.Number);
    }
}
