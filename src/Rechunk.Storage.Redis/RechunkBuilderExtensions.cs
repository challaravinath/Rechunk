using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Rechunk.Storage.Redis;

public static class RechunkBuilderExtensions
{
    /// <summary>
    /// Keeps the fingerprints and the dead letters in Redis, so every worker checks against the same indexes.
    /// A connection is made from the configuration string, unless the app already registered an IConnectionMultiplexer.
    /// </summary>
    public static IRechunkBuilder AddRedisStorage(this IRechunkBuilder builder, Action<RedisStorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new RedisStorageOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        builder.Services.TryAddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(options.Configuration));
        builder.Services.Replace(ServiceDescriptor.Singleton<IFingerprintStore>(sp =>
            new BackedFingerprintStore(
                new RedisFingerprintBackend(sp.GetRequiredService<IConnectionMultiplexer>(), options),
                sp.GetRequiredService<RechunkOptions>(),
                sp.GetService<TimeProvider>())));
        builder.Services.Replace(ServiceDescriptor.Singleton<IDeadLetterStore>(sp =>
            new RedisDeadLetterStore(sp.GetRequiredService<IConnectionMultiplexer>(), options)));
        return builder;
    }

    public static IRechunkBuilder AddRedisStorage(this IRechunkBuilder builder, string configuration) =>
        builder.AddRedisStorage(o => o.Configuration = configuration);
}
