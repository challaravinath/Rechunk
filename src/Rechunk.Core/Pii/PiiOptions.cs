namespace Rechunk;

/// <summary>
/// Scanning for personal data and secrets, before a chunk is fingerprinted and embedded. Off by default. When it is on,
/// the built in detector is used unless Detector is set.
/// </summary>
public sealed class PiiOptions
{
    private readonly Dictionary<PiiType, PiiPolicy> _policies = new();
    private readonly HashSet<PiiType> _ignored = new();

    public bool Enabled { get; set; }

    /// <summary>The detector to use. Null means the built in one.</summary>
    public IPiiDetector? Detector { get; set; }

    /// <summary>The policy for a type that has none of its own.</summary>
    public PiiPolicy DefaultPolicy { get; set; } = PiiPolicy.Flag;

    /// <summary>Findings below this confidence are dropped. The built in detector reports 1, a service reports its own score.</summary>
    public double MinConfidence { get; set; }

    /// <summary>What replaces masked text. The default is the upper case type name in square brackets, for example [EMAIL].</summary>
    public Func<PiiType, string> Placeholder { get; set; } = type => "[" + (type.Name ?? "pii").ToUpperInvariant() + "]";

    /// <summary>Sets the policy for one type.</summary>
    /// <summary>The policies set per type with <see cref="Set"/>. Types that are not here use <see cref="DefaultPolicy"/>.</summary>
    public IReadOnlyDictionary<PiiType, PiiPolicy> Policies => _policies;

    public PiiOptions Set(PiiType type, PiiPolicy policy)
    {
        _policies[type] = policy;
        _ignored.Remove(type);
        return this;
    }

    /// <summary>Does not report these types at all.</summary>
    public PiiOptions Ignore(params PiiType[] types)
    {
        foreach (var type in types)
        {
            _ignored.Add(type);
            _policies.Remove(type);
        }

        return this;
    }

    internal bool IsIgnored(PiiType type) => _ignored.Contains(type);

    internal PiiPolicy PolicyFor(PiiType type) => _policies.TryGetValue(type, out var p) ? p : DefaultPolicy;

    internal void Validate()
    {
        if (MinConfidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinConfidence), "MinConfidence has to be between 0 and 1.");
        }

        if (!Enum.IsDefined(DefaultPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultPolicy), "Unknown policy.");
        }
    }
}

/// <summary>Retries and the pending queue for audit writes.</summary>
public sealed class AuditOptions
{
    /// <summary>How often a failed audit write is tried before it goes to the outbox.</summary>
    public int MaxAttempts { get; set; } = 3;

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(50);

    public double RetryBackoff { get; set; } = 2;

    /// <summary>How many events one drain of the outbox sends.</summary>
    public int DrainBatchSize { get; set; } = 200;

    /// <summary>The most events the default in memory audit log keeps. The oldest go first. Use a SQLite or Redis log for real use.</summary>
    public int InMemoryLimit { get; set; } = 50_000;

    internal void Validate()
    {
        if (MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), "MaxAttempts has to be at least 1.");
        }

        if (RetryDelay < TimeSpan.Zero || RetryBackoff < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(RetryDelay), "RetryDelay can not be negative and RetryBackoff has to be at least 1.");
        }

        if (DrainBatchSize < 1 || InMemoryLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(DrainBatchSize), "DrainBatchSize and InMemoryLimit have to be at least 1.");
        }
    }
}
