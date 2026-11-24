using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Rechunk.Storage.SqlServer;

public static class RechunkBuilderExtensions
{
    /// <summary>
    /// Keeps fingerprints and document revisions in SQL Server or Azure SQL. Dead letters, the audit log and the
    /// subject index stay on whatever store was registered before this, or on the in memory defaults.
    /// </summary>
    public static IRechunkBuilder AddRechunkSqlServer(this IRechunkBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return builder.AddRechunkSqlServer(options => options.ConnectionString = connectionString);
    }

    /// <summary>Same as the connection string overload, when you need a schema name, a connection factory, or both.</summary>
    public static IRechunkBuilder AddRechunkSqlServer(this IRechunkBuilder builder, Action<SqlServerStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new SqlServerStorageOptions();
        configure(options);
        builder.Services.AddSingleton(options);
        builder.AddSqlServerFingerprints(options);
        builder.AddSqlServerRevisions(options);
        return builder;
    }

    /// <summary>Fingerprints only. Revisions can stay on another store.</summary>
    public static IRechunkBuilder AddSqlServerFingerprints(this IRechunkBuilder builder, SqlServerStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        builder.Services.Replace(ServiceDescriptor.Singleton<IFingerprintStore>(sp =>
        {
            var rechunk = sp.GetRequiredService<RechunkOptions>();
            rechunk.KeyProvider ??= sp.GetService<IKeyProvider>();
            return new BackedFingerprintStore(new SqlServerFingerprintBackend(options), rechunk, sp.GetService<TimeProvider>());
        }));
        return builder;
    }

    /// <summary>Document revisions only. Fingerprints can stay on another store.</summary>
    public static IRechunkBuilder AddSqlServerRevisions(this IRechunkBuilder builder, SqlServerStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        builder.Services.Replace(ServiceDescriptor.Singleton<IRevisionStore>(_ => new SqlServerRevisionStore(options)));
        return builder;
    }
}
