using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Rechunk.Storage.Sqlite;

public static class RechunkBuilderExtensions
{
    /// <summary>
    /// Keeps the fingerprints (exact hashes, SimHash bands, MinHash LSH) and the dead letters in a SQLite file, so
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
            new BackedFingerprintStore(
                new SqliteFingerprintBackend(options),
                sp.GetRequiredService<RechunkOptions>(),
                sp.GetService<TimeProvider>())));
        builder.Services.Replace(ServiceDescriptor.Singleton<IDeadLetterStore>(_ => new SqliteDeadLetterStore(options)));
        return builder;
    }
}
