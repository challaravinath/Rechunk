# Rechunk.Storage.SqlServer

Fingerprints and document revisions in SQL Server or Azure SQL. The first call creates the tables in the `rechunk` schema. Set `CreateTables` to false and run [docs/sqlserver-schema.sql](../../docs/sqlserver-schema.sql) yourself when a DBA owns the database.

```csharp
using Rechunk.Storage.SqlServer;

services.AddRechunk(options =>
    {
        options.SimHash.Enabled = true;
        options.MinHash.Enabled = true;
    })
    .AddRechunkSqlServer(connectionString);
```

A connection factory is there for Entra token auth. Pass the open connection back. The library disposes it when the call finishes.

```csharp
builder.AddRechunkSqlServer(options =>
{
    options.ConnectionFactory = async ct =>
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    };
});
```

Fingerprints and revisions can live in different places. `AddSqlServerFingerprints` and `AddSqlServerRevisions` each replace only their own store.

Depends on the Rechunk package.

https://github.com/challaravinath/Rechunk
