using System.Numerics;

namespace Rechunk;

/// <summary>
/// 64 bit SimHash (Charikar) over word shingles.
///
/// How it works:
///  1. Split the text into words, lower cased, punctuation dropped.
///  2. Make shingles: every run of ShingleSize words in a row. A text with fewer words than that
///     gets one shingle with all its words.
///  3. Hash each shingle to 64 bits (see StableHash64, mixed with the seed).
///  4. For every bit position, add 1 for each shingle that has the bit set and subtract 1 for each
///     that does not. A shingle that shows up twice counts twice, which is the simple form of weighting.
///  5. The result bit is 1 where the total is above zero.
///
/// Similar texts share most of their shingles, so most columns come out the same and the Hamming
/// distance between the results stays small. The hash is stable across runs and machines, and
/// the golden tests make sure it stays that way. Change anything above and Version has to change too.
/// </summary>
public sealed class SimHasher
{
    /// <summary>Bump this if the steps above ever change.</summary>
    public const string Version = "simhash-v1";

    /// <summary>"RECHUNK1" in ASCII. Mixed into every shingle hash.</summary>
    public const ulong DefaultSeed = 0x52454348554E4B31UL;

    private readonly int _shingleSize;
    private readonly ulong _seed;

    public SimHasher(int shingleSize = 3, ulong seed = DefaultSeed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(shingleSize, 1);
        _shingleSize = shingleSize;
        _seed = seed;
    }

    public int ShingleSize => _shingleSize;

    /// <summary>Number of bits that differ between two SimHashes.</summary>
    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>SimHash of already normalized text. Text without any words gives 0.</summary>
    public ulong Compute(ReadOnlySpan<char> normalizedText) => Compute(WordTokenizer.Tokenize(normalizedText));

    internal ulong Compute(IReadOnlyList<WordToken> tokens)
    {
        if (tokens.Count == 0)
        {
            return 0;
        }

        Span<int> columns = stackalloc int[64];
        columns.Clear();

        var shingleCount = Math.Max(1, tokens.Count - _shingleSize + 1);
        var width = Math.Min(_shingleSize, tokens.Count);

        for (var start = 0; start < shingleCount; start++)
        {
            var h = _seed;
            for (var i = 0; i < width; i++)
            {
                h = StableHash64.Combine(h, tokens[start + i].Hash);
            }

            for (var bit = 0; bit < 64; bit++)
            {
                columns[bit] += ((h >> bit) & 1UL) == 1UL ? 1 : -1;
            }
        }

        ulong result = 0;
        for (var bit = 0; bit < 64; bit++)
        {
            if (columns[bit] > 0)
            {
                result |= 1UL << bit;
            }
        }

        return result;
    }
}
