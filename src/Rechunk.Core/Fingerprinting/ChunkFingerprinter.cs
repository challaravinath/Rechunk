namespace Rechunk;

/// <summary>
/// Default fingerprinter. The exact hash always works. SimHash and MinHash only run when they are
/// switched on in the options, and for now they throw because they are not written yet.
/// </summary>
public sealed class ChunkFingerprinter : IChunkFingerprinter
{
    private readonly RechunkOptions _options;
    private readonly SimHasher _simHasher = new();
    private readonly MinHasher _minHasher;

    public ChunkFingerprinter(RechunkOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _minHasher = new MinHasher(options.MinHash);
    }

    public string AlgorithmVersion => $"rechunk-fp-v1+{TextNormalizer.Version}+{ExactHasher.Version}";

    public ChunkFingerprint Compute(ReadOnlySpan<char> text)
    {
        var exact = ExactHasher.Hash(text);

        ulong simHash = 0;
        ReadOnlyMemory<uint> minHash = default;

        if (_options.SimHash.Enabled || _options.MinHash.Enabled)
        {
            var normalized = TextNormalizer.Normalize(text);

            if (_options.SimHash.Enabled)
            {
                simHash = _simHasher.Compute(normalized);
            }

            if (_options.MinHash.Enabled)
            {
                minHash = _minHasher.Compute(normalized);
            }
        }

        return new ChunkFingerprint(exact, simHash, minHash);
    }
}
