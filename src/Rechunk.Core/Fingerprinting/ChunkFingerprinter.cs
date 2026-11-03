namespace Rechunk;

/// <summary>
/// Default fingerprinter. The exact hash is always made. SimHash is made when it is switched on in
/// the options. MinHash is not written yet and throws if you switch it on.
/// </summary>
public sealed class ChunkFingerprinter : IChunkFingerprinter
{
    private readonly RechunkOptions _options;
    private readonly SimHasher _simHasher;
    private readonly MinHasher _minHasher;

    public ChunkFingerprinter(RechunkOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _simHasher = new SimHasher(options.SimHash.ShingleSize);
        _minHasher = new MinHasher(options.MinHash);
    }

    /// <summary>
    /// Names every step that can change a stored value. Store it next to the fingerprints, and
    /// treat fingerprints with a different version as not comparable.
    /// </summary>
    public string AlgorithmVersion
    {
        get
        {
            var version = $"rechunk-fp-v1+{TextNormalizer.Version}+{ExactHasher.Version}";

            if (_options.SimHash.Enabled)
            {
                version += $"+{SimHasher.Version}-s{_options.SimHash.ShingleSize}-m{_options.SimHash.MinWords}";

                if (_options.GuardNumbersAndNegations)
                {
                    version += $"+{FalseMergeGuard.Version}";
                }
            }

            return version;
        }
    }

    public ChunkFingerprint Compute(ReadOnlySpan<char> text)
    {
        var normalized = TextNormalizer.Normalize(text);
        var exact = ExactHasher.HashNormalized(normalized);

        ulong simHash = 0;
        ulong guard = 0;
        ReadOnlyMemory<uint> minHash = default;

        if (_options.SimHash.Enabled)
        {
            var tokens = WordTokenizer.Tokenize(normalized);

            if (tokens.Count >= _options.SimHash.MinWords)
            {
                simHash = _simHasher.Compute(tokens);

                if (_options.GuardNumbersAndNegations)
                {
                    guard = FalseMergeGuard.Signature(tokens);
                }
            }
        }

        if (_options.MinHash.Enabled)
        {
            minHash = _minHasher.Compute(normalized);
        }

        return new ChunkFingerprint(exact, simHash, minHash, guard);
    }
}
