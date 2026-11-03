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
        services.TryAddSingleton(sp => new RechunkPipeline(
            sp.GetRequiredService<RechunkOptions>(),
            sp.GetRequiredService<IChunkFingerprinter>(),
            sp.GetRequiredService<IExactHashIndex>(),
            sp.GetService<ILogger<RechunkPipeline>>() ?? NullLogger<RechunkPipeline>.Instance,
            sp.GetRequiredService<SimHashBandIndex>()));

        return new RechunkBuilder(services);
    }

    private sealed class RechunkBuilder(IServiceCollection services) : IRechunkBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
