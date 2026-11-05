using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rechunk;

/// <summary>Handed back by AddRechunk so the other packages can hang their own setup on it.</summary>
public interface IRechunkBuilder
{
    IServiceCollection Services { get; }
}

public static class ServiceCollectionExtensions
{
    public static IRechunkBuilder AddRechunk(this IServiceCollection services, Action<RechunkOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new RechunkOptions();
        configure?.Invoke(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton<IChunkFingerprinter>(sp => new ChunkFingerprinter(sp.GetRequiredService<RechunkOptions>()));
        services.TryAddSingleton<IExactHashIndex, InMemoryExactHashIndex>();
        services.TryAddSingleton(sp => new SimHashBandIndex(sp.GetRequiredService<RechunkOptions>().SimHash.MaxHammingDistance));
        services.TryAddSingleton(sp => new MinHashLshIndex(sp.GetRequiredService<RechunkOptions>().MinHash));
        services.TryAddSingleton<IDeadLetterStore, InMemoryDeadLetterStore>();
        services.TryAddSingleton(sp =>
        {
            // A fingerprint store that is registered (by a storage package) takes over all three indexes.
            // It is looked up here, not above, so it does not matter which package was added first.
            var store = sp.GetService<IFingerprintStore>();
            return new RechunkPipeline(
                sp.GetRequiredService<RechunkOptions>(),
                sp.GetRequiredService<IChunkFingerprinter>(),
                store?.Exact ?? sp.GetRequiredService<IExactHashIndex>(),
                sp.GetService<ILogger<RechunkPipeline>>() ?? NullLogger<RechunkPipeline>.Instance,
                store?.SimHash ?? sp.GetRequiredService<SimHashBandIndex>(),
                store?.MinHash ?? sp.GetRequiredService<MinHashLshIndex>(),
                store);
        });
        services.TryAddSingleton<IRevisionStore, InMemoryRevisionStore>();
        services.TryAddSingleton<IAuditLog>(sp => new InMemoryAuditLog(sp.GetRequiredService<RechunkOptions>().Audit.InMemoryLimit));
        services.TryAddSingleton<IAuditOutbox, InMemoryAuditOutbox>();
        services.TryAddSingleton(sp => new AuditWriter(
            sp.GetRequiredService<IAuditLog>(),
            sp.GetRequiredService<IAuditOutbox>(),
            sp.GetRequiredService<RechunkOptions>().Audit,
            sp.GetService<ILogger<AuditWriter>>() ?? NullLogger<AuditWriter>.Instance,
            sp.GetService<TimeProvider>()));
        services.TryAddSingleton(sp => new RevisionTracker(
            sp.GetRequiredService<RechunkPipeline>(),
            sp.GetRequiredService<IRevisionStore>(),
            sp.GetRequiredService<IDeadLetterStore>(),
            sp.GetRequiredService<IAuditLog>(),
            sp.GetService<ILogger<RevisionTracker>>() ?? NullLogger<RevisionTracker>.Instance,
            sp.GetService<TimeProvider>(),
            sp.GetRequiredService<AuditWriter>()));
        services.TryAddSingleton(sp => new ChunkIngestor(
            sp.GetRequiredService<RechunkPipeline>(),
            sp.GetRequiredService<IChunkEmbedder>(),
            sp.GetRequiredService<IVectorSink>(),
            sp.GetRequiredService<IDeadLetterStore>(),
            sp.GetService<ILogger<ChunkIngestor>>() ?? NullLogger<ChunkIngestor>.Instance,
            sp.GetService<TimeProvider>(),
            sp.GetRequiredService<AuditWriter>()));

        return new RechunkBuilder(services);
    }

    /// <summary>
    /// Keeps the fingerprints in this process, but with the same claim and commit steps as the shared backends.
    /// Handy for tests and for trying the flow out. It is not shared between processes.
    /// </summary>
    public static IRechunkBuilder UseInMemoryFingerprintStore(this IRechunkBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<IFingerprintStore>(sp =>
            new InMemoryFingerprintStore(sp.GetRequiredService<RechunkOptions>(), sp.GetService<TimeProvider>()));
        return builder;
    }

    private sealed class RechunkBuilder(IServiceCollection services) : IRechunkBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
