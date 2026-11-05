namespace Rechunk;

/// <summary>
/// One word of a chunk. Hash is the stable hash of the lower case word, and the two flags are what the
/// false merge guard looks at.
/// </summary>
internal readonly record struct WordToken(ulong Hash, bool HasDigit, bool IsNegation, bool IsChar = false);

/// <summary>
/// Splits normalized text into words. A word is a run of letters and digits. Everything else
/// (spaces, punctuation) ends a word, except an apostrophe between two letters, which is dropped,
/// so "don't" and "dont" are the same word.
///
/// Chinese, Japanese and Thai have no spaces between words, so a run of letters from those scripts would be one giant
/// word. With characterShingles on (the default) every character of such a run is a token of its own (marked IsChar),
/// and the hashers make their shingles out of those characters. Text in other scripts comes out the same as before.
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

    public static List<WordToken> Tokenize(ReadOnlySpan<char> text, bool characterShingles = true)
    {
        var tokens = new List<WordToken>(Math.Max(4, text.Length / 6));
        var word = new char[32];
        var length = 0;
        var hasDigit = false;

        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : ' ';

            if (characterShingles && i < text.Length && IsUnspacedLetter(text, i, out var width, out var codePoint))
            {
                if (length > 0)
                {
                    tokens.Add(Finish(word, length, hasDigit));
                    length = 0;
                    hasDigit = false;
                }

                var hash = StableHash64.HashLower(text.Slice(i, width));
                var isDigit = Scripts.IsHanNumeral(codePoint) || (width == 1 && char.IsDigit(c));
                tokens.Add(new WordToken(hash, isDigit, Scripts.IsHanNegation(codePoint), true));
                i += width - 1;
                continue;
            }

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
                tokens.Add(Finish(word, length, hasDigit));
                length = 0;
                hasDigit = false;
            }
        }

        return tokens;
    }

    private static WordToken Finish(char[] word, int length, bool hasDigit)
    {
        var hash = StableHash64.HashLower(word.AsSpan(0, length));
        var negation = length <= MaxNegationLength && Negations.Contains(hash);
        return new WordToken(hash, hasDigit, negation);
    }

    // A letter (or mark, or digit) of a script that is written without spaces. Punctuation of those scripts is not.
    private static bool IsUnspacedLetter(ReadOnlySpan<char> text, int index, out int width, out int codePoint)
    {
        var c = text[index];
        width = 1;
        codePoint = c;

        if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            codePoint = char.ConvertToUtf32(c, text[index + 1]);
            width = 2;
        }
        else if (c < 0x0E00)
        {
            // no script that is written without spaces lives below the Thai block
            return false;
        }

        if (!Scripts.IsUnspaced(Scripts.Of(codePoint)))
        {
            return false;
        }

        var category = width == 2
            ? System.Globalization.CharUnicodeInfo.GetUnicodeCategory(codePoint)
            : System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
        return category is not (System.Globalization.UnicodeCategory.SpaceSeparator
            or System.Globalization.UnicodeCategory.Control
            or System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.OtherPunctuation
            or System.Globalization.UnicodeCategory.DashPunctuation
            or System.Globalization.UnicodeCategory.OpenPunctuation
            or System.Globalization.UnicodeCategory.ClosePunctuation
            or System.Globalization.UnicodeCategory.InitialQuotePunctuation
            or System.Globalization.UnicodeCategory.FinalQuotePunctuation
            or System.Globalization.UnicodeCategory.ConnectorPunctuation
            or System.Globalization.UnicodeCategory.MathSymbol
            or System.Globalization.UnicodeCategory.CurrencySymbol
            or System.Globalization.UnicodeCategory.ModifierSymbol
            or System.Globalization.UnicodeCategory.OtherSymbol);
    }
}
