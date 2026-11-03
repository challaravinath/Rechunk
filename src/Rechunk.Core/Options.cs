namespace Rechunk;

public sealed class RechunkOptions
{
    /// <summary>Scope used when the caller does not pass one. Dedup never matches across scopes.</summary>
    public string DefaultScope { get; set; } = "default";

    public SimHashOptions SimHash { get; } = new();

    public MinHashOptions MinHash { get; } = new();

    public DuplicatePolicy OnDuplicate { get; set; } = DuplicatePolicy.Alias;

    public RevisionPolicy OnRevision { get; set; } = RevisionPolicy.ReEmbedChanged;

    /// <summary>Do not merge chunks that only differ in numbers, dates or a "not".</summary>
    public bool GuardNumbersAndNegations { get; set; } = true;

    /// <summary>Report what would happen but do not remember anything.</summary>
    public bool DryRun { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DefaultScope))
        {
            throw new ArgumentException("DefaultScope can not be empty.", nameof(DefaultScope));
        }

        if (SimHash.MaxHammingDistance is < 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(SimHash), "MaxHammingDistance has to be between 0 and 64.");
        }

        if (MinHash.Permutations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinHash), "Permutations has to be above zero.");
        }

        if (MinHash.Threshold is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinHash), "Threshold has to be above 0 and at most 1.");
        }

        if (MinHash.Bands is { } bands && MinHash.Rows is { } rows && bands * rows != MinHash.Permutations)
        {
            throw new ArgumentException("Bands times Rows has to equal Permutations.", nameof(MinHash));
        }
    }
}

public sealed class SimHashOptions
{
    /// <summary>Off for now, the SimHash stage is not written yet.</summary>
    public bool Enabled { get; set; }

    /// <summary>Starting value, to be tuned on the benchmark.</summary>
    public int MaxHammingDistance { get; set; } = 3;
}

public sealed class MinHashOptions
{
    /// <summary>Off for now, the MinHash stage is not written yet.</summary>
    public bool Enabled { get; set; }

    public int Permutations { get; set; } = 128;

    /// <summary>Starting value, to be tuned on the benchmark.</summary>
    public double Threshold { get; set; } = 0.85;

    /// <summary>Words per shingle.</summary>
    public int ShingleSize { get; set; } = 3;

    /// <summary>Set both Bands and Rows to pick them by hand, or leave them null to derive them from Threshold.</summary>
    public int? Bands { get; set; }

    public int? Rows { get; set; }
}
