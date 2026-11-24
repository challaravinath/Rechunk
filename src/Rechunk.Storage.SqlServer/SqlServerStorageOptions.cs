using Microsoft.Data.SqlClient;

namespace Rechunk.Storage.SqlServer;

/// <summary>
/// How to reach SQL Server or Azure SQL, and where the tables live.
/// Set <see cref="ConnectionString"/> or <see cref="ConnectionFactory"/>. The factory is the one to use
/// when the connection has to carry an Entra token, for example from Managed Identity.
/// </summary>
public sealed class SqlServerStorageOptions
{
    /// <summary>A normal SQL Server or Azure SQL connection string.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Opens a connection for one call. Return a new connection each time. The library disposes it when the call finishes.
    /// Use this when the connection needs an Entra token, for example from Managed Identity.
    /// </summary>
    public Func<CancellationToken, Task<SqlConnection>>? ConnectionFactory { get; set; }

    /// <summary>Schema that holds the tables. The default is rechunk.</summary>
    public string Schema { get; set; } = "rechunk";

    /// <summary>
    /// When true, the first call creates the tables if they are missing. Turn this off when a DBA runs
    /// docs/sqlserver-schema.sql instead.
    /// </summary>
    public bool CreateTables { get; set; } = true;

    /// <summary>How many times a transient Azure SQL or deadlock error is tried. The first try counts.</summary>
    public int MaxAttempts { get; set; } = 3;
}
