namespace Rechunk;

/// <summary>
/// MinHash signature of a chunk. Two signatures agree in about as many slots as the two chunks
/// agree in shingles, so the share of equal slots is an estimate of the Jaccard similarity.
///
/// How it works:
///  1. Split the text into words (same tokenizer as SimHash) and make shingles of ShingleSize words.
///     A text with fewer words than that gets one shingle with all its words.
///  2. Hash every shingle to 64 bits with StableHash64, starting from a fixed seed.
///  3. Slot i of the signature is the smallest value of mix(shingleHash xor seed[i]) over all shingles,
///     keeping the top 32 bits. The seeds are not random: seed[i] = mix(PermutationSeed + i * golden ratio),
///     so every machine gets the same ones.
///
/// With N slots the estimate is off by about sqrt(J * (1 - J) / N), so around 0.04 for N = 128.
/// The golden tests pin the signatures, so any change to the steps above has to bump Version.
/// </summary>
public sealed class MinHasher
{
    /// <summary>Bump this if the steps above ever change.</summary>
    public const string Version = "minhash-v1";

    /// <summary>"MINHASH1" in ASCII. The start value for every shingle hash.</summary>
    public const ulong ShingleSeed = 0x4D494E4841534831UL;

    /// <summary>"PERMUTE1" in ASCII. The per slot seeds are derived from it.</summary>
    public const ulong PermutationSeed = 0x5045524D55544531UL;

    private readonly int _shingleSize;
    private readonly ulong[] _seeds;

    public MinHasher(MinHashOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ShingleSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Permutations, 1);

        Options = options;
        _shingleSize = options.ShingleSize;
        _seeds = new ulong[options.Permutations];
        for (var i = 0; i < _seeds.Length; i++)
        {
            _seeds[i] = StableHash64.Mix(PermutationSeed + (ulong)i * 0x9e3779b97f4a7c15UL);
        }
    }

    public MinHashOptions Options { get; }

    public int Permutations => _seeds.Length;

    /// <summary>Share of equal slots, which estimates the Jaccard similarity of the two chunks.</summary>
    public static double EstimateJaccard(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("Signatures made with a different number of permutations can not be compared.");
        }

        if (a.Length == 0)
        {
            return 0;
        }

        var equal = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i])
            {
                equal++;
            }
        }

        return (double)equal / a.Length;
    }

    /// <summary>Signature of already normalized text. Text without any words gives an empty signature.</summary>
    public ReadOnlyMemory<uint> Compute(ReadOnlySpan<char> normalizedText) => Compute(WordTokenizer.Tokenize(normalizedText));

    internal ReadOnlyMemory<uint> Compute(IReadOnlyList<WordToken> tokens)
    {
        if (tokens.Count == 0)
        {
            return default;
        }

        var signature = new uint[_seeds.Length];
        Array.Fill(signature, uint.MaxValue);

        var shingleCount = Math.Max(1, tokens.Count - _shingleSize + 1);
        var width = Math.Min(_shingleSize, tokens.Count);

        for (var start = 0; start < shingleCount; start++)
        {
            var h = ShingleSeed;
            for (var i = 0; i < width; i++)
            {
                h = StableHash64.Combine(h, tokens[start + i].Hash);
            }

            for (var slot = 0; slot < signature.Length; slot++)
            {
                var value = (uint)(StableHash64.Mix(h ^ _seeds[slot]) >> 32);
                if (value < signature[slot])
                {
                    signature[slot] = value;
                }
            }
        }

        return signature;
    }
}
