namespace Rechunk;

/// <summary>
/// One word of a chunk. Hash is the stable hash of the lower case word, and the two flags are what the
/// false merge guard looks at.
/// </summary>
internal readonly record struct WordToken(ulong Hash, bool HasDigit, bool IsNegation);

/// <summary>
/// Splits normalized text into words. A word is a run of letters and digits. Everything else
/// (spaces, punctuation) ends a word, except an apostrophe between two letters, which is dropped,
/// so "don't" and "dont" are the same word.
/// </summary>
internal static class WordTokenizer
{
    private const int MaxNegationLength = 10;

    // words that flip the meaning of a sentence, used by the false merge guard
    private static readonly HashSet<ulong> Negations = new[]
    {
        "not", "no", "never", "none", "nor", "neither", "cannot", "without", "nothing", "nobody",
        "dont", "doesnt", "didnt", "isnt", "arent", "wasnt", "werent", "wont", "wouldnt",
        "cant", "couldnt", "shouldnt", "hasnt", "havent", "hadnt", "mustnt", "neednt",
    }.Select(w => StableHash64.HashLower(w)).ToHashSet();

    public static List<WordToken> Tokenize(ReadOnlySpan<char> text)
    {
        var tokens = new List<WordToken>(Math.Max(4, text.Length / 6));
        var word = new char[32];
        var length = 0;
        var hasDigit = false;

        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : ' ';

            var isWordChar = char.IsLetterOrDigit(c);
            var isJoiner = (c == '\'' || c == '\u2019')
                && length > 0
                && i + 1 < text.Length
                && char.IsLetterOrDigit(text[i + 1]);

            if (isWordChar)
            {
                if (length == word.Length)
                {
                    Array.Resize(ref word, word.Length * 2);
                }

                word[length++] = c;
                hasDigit |= char.IsDigit(c);
            }
            else if (!isJoiner && length > 0)
            {
                var span = word.AsSpan(0, length);
                var hash = StableHash64.HashLower(span);
                var negation = length <= MaxNegationLength && Negations.Contains(hash);
                tokens.Add(new WordToken(hash, hasDigit, negation));
                length = 0;
                hasDigit = false;
            }
        }

        return tokens;
    }
}
