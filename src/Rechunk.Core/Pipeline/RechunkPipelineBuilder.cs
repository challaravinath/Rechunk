using Microsoft.Extensions.Logging;

namespace Rechunk;

public sealed class RechunkPipelineBuilder
{
    private readonly RechunkOptions _options = new();
    private IExactHashIndex? _exactIndex;
    private INearDuplicateIndex? _simHashIndex;
    private IMinHashIndex? _minHashIndex;
    private IFingerprintStore? _store;
    private ILogger? _logger;

    /// <summary>Stage 1 is always on. This is here so the setup reads the same as the other stages.</summary>
    public RechunkPipelineBuilder UseExactHash() => this;

    /// <summary>Stage 2: near duplicates by SimHash. Off unless you call this.</summary>
    public RechunkPipelineBuilder UseSimHash(Action<SimHashOptions>? configure = null)
    {
        _options.SimHash.Enabled = true;
        configure?.Invoke(_options.SimHash);
        return this;
    }

    /// <summary>Stage 3: near duplicates by MinHash and LSH. Off unless you call this.</summary>
    public RechunkPipelineBuilder UseMinHash(Action<MinHashOptions>? configure = null)
    {
        _options.MinHash.Enabled = true;
        configure?.Invoke(_options.MinHash);
        return this;
    }

    public RechunkPipelineBuilder OnDuplicate(DuplicatePolicy policy)
    {
        _options.OnDuplicate = policy;
        return this;
    }

    public RechunkPipelineBuilder OnRevision(RevisionPolicy policy)
    {
        _options.OnRevision = policy;
        return this;
    }

    public RechunkPipelineBuilder GuardNumbersAndNegations(bool enabled = true)
    {
        _options.GuardNumbersAndNegations = enabled;
        return this;
    }

    public RechunkPipelineBuilder UseDefaultScope(string scope)
    {
        _options.DefaultScope = scope;
        return this;
    }

    public RechunkPipelineBuilder DryRun(bool enabled = true)
    {
        _options.DryRun = enabled;
        return this;
    }

    public RechunkPipelineBuilder UseExactHashIndex(IExactHashIndex index)
    {
        _exactIndex = index ?? throw new ArgumentNullException(nameof(index));
        return this;
    }

    /// <summary>Use your own index for the SimHash stage. By default a SimHashBandIndex in memory is used.</summary>
    public RechunkPipelineBuilder UseSimHashIndex(INearDuplicateIndex index)
    {
        _simHashIndex = index ?? throw new ArgumentNullException(nameof(index));
        return this;
    }

    /// <summary>Use your own index for the MinHash stage. By default a MinHashLshIndex in memory is used.</summary>
    public RechunkPipelineBuilder UseMinHashIndex(IMinHashIndex index)
    {
        _minHashIndex = index ?? throw new ArgumentNullException(nameof(index));
        return this;
    }

    /// <summary>
    /// Use a shared store for all three stages, so workers in different processes check against the same
    /// fingerprints. Indexes you set yourself with the other Use methods still win for their stage.
    /// </summary>
    public RechunkPipelineBuilder UseFingerprintStore(IFingerprintStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        return this;
    }

    /// <summary>For the settings that have no method of their own, like the lease time and the retry settings.</summary>
    public RechunkPipelineBuilder Configure(Action<RechunkOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_options);
        return this;
    }

    public RechunkPipelineBuilder UseLogger(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        return this;
    }

    public RechunkPipeline Build() =>
        new(
            _options,
            new ChunkFingerprinter(_options),
            _exactIndex ?? _store?.Exact ?? new InMemoryExactHashIndex(),
            _logger,
            _simHashIndex,
            _minHashIndex,
            _store);
}
