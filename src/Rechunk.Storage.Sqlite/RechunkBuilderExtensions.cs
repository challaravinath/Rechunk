using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Rechunk.Storage.Sqlite;

public static class RechunkBuilderExtensions
{
    /// <summary>
    /// Keeps the fingerprints (exact hashes, SimHash bands, MinHash LSH), the dead letters, the document revisions and the audit trail in a SQLite file, so
    /// they survive restarts and can be shared by workers on one machine. The file is created on first use.
    /// </summary>
    public static IRechunkBuilder AddSqliteStorage(this IRechunkBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new SqliteStorageOptions { ConnectionString = connectionString };
        builder.Services.AddSingleton(options);

        // Replace, not TryAdd: asking for SQLite storage should win over the in memory defaults.
        builder.Services.Replace(ServiceDescriptor.Singleton<IFingerprintStore>(sp =>
        {
            var rechunk = sp.GetRequiredService<RechunkOptions>();
            rechunk.KeyProvider ??= sp.GetService<IKeyProvider>();
            return new BackedFingerprintStore(
                new SqliteFingerprintBackend(options),
                rechunk,
                sp.GetService<TimeProvider>());
        }));
        builder.Services.Replace(ServiceDescriptor.Singleton<IDeadLetterStore>(_ => new SqliteDeadLetterStore(options)));
        builder.Services.Replace(ServiceDescriptor.Singleton<IRevisionStore>(_ => new SqliteRevisionStore(options)));
        builder.Services.Replace(ServiceDescriptor.Singleton<IAuditLog>(_ => new SqliteAuditLog(options)));
        builder.Services.Replace(ServiceDescriptor.Singleton<IAuditOutbox>(_ => new SqliteAuditOutbox(options)));
        builder.Services.Replace(ServiceDescriptor.Singleton<ISubjectIndex>(_ => new SqliteSubjectIndex(options)));
        return builder;
    }
}
