using Microsoft.Extensions.DependencyInjection;

namespace Rechunk.Storage.Sqlite;

public static class RechunkBuilderExtensions
{
    public static IRechunkBuilder AddSqliteStorage(this IRechunkBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        builder.Services.AddSingleton(new SqliteStorageOptions { ConnectionString = connectionString });

        // TODO:
        //  - tables for chunk state, the delete ledger, reference counts and the audit log
        //  - an IExactHashIndex that survives restarts
        //  - run the shared store contract tests against it
        return builder;
    }
}
