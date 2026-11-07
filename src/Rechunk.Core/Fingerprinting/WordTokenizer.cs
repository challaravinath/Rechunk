namespace Rechunk;

/// <summary>
/// One word of a chunk. Hash is the stable hash of the lower case word, and the two flags are what the
/// false merge guard looks at.
/// </summary>
internal readonly record struct WordToken(
    ulong Hash,
    bool HasDigit,
    bool IsNegation,
    bool IsChar = false,
    bool IsIdentifier = false,
    ulong IdentifierHash = 0);

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
        var wordStart = 0;

        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : ' ';

            if (characterShingles && i < text.Length && IsUnspacedLetter(text, i, out var width, out var codePoint))
            {
                if (length > 0)
                {
                    tokens.Add(Finish(word, length, hasDigit, text, wordStart, i));
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
                if (length == 0)
                {
                    wordStart = i;
                }

                if (length == word.Length)
                {
                    Array.Resize(ref word, word.Length * 2);
                }

                word[length++] = c;
                hasDigit |= char.IsDigit(c);
            }
            else if (!isJoiner && length > 0)
            {
                tokens.Add(Finish(word, length, hasDigit, text, wordStart, i));
                length = 0;
                hasDigit = false;
            }
        }

        return tokens;
    }

    private static WordToken Finish(char[] word, int length, bool hasDigit, ReadOnlySpan<char> text, int start, int end)
    {
        var hash = StableHash64.HashLower(word.AsSpan(0, length));
        var negation = length <= MaxNegationLength && Negations.Contains(hash);
        var identifier = IsIdentifier(word, length, hasDigit, text, start, end);
        var identifierHash = identifier ? StableHash64.Hash(word.AsSpan(0, length)) : 0UL;
        return new WordToken(hash, hasDigit, negation, false, identifier, identifierHash);
    }

    // A name, a snippet id, or part of a dotted name. Ordinary words, including a capital at the start of a sentence, are not.
    private static bool IsIdentifier(char[] word, int length, bool hasDigit, ReadOnlySpan<char> text, int start, int end)
    {
        if (HasInternalCapital(word, length) || IsAllCaps(word, length) || (hasDigit && HasLetter(word, length)))
        {
            return true;
        }

        if (IsDottedPart(text, start, end))
        {
            return true;
        }

        return IsMidSentenceName(word, length, text, start);
    }

    private static bool HasInternalCapital(char[] word, int length)
    {
        for (var i = 1; i < length; i++)
        {
            if (char.IsUpper(word[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAllCaps(char[] word, int length)
    {
        if (length < 2)
        {
            return false;
        }

        var letter = false;
        for (var i = 0; i < length; i++)
        {
            if (!char.IsLetter(word[i]))
            {
                return false;
            }

            letter = true;
            if (char.IsLower(word[i]))
            {
                return false;
            }
        }

        return letter;
    }

    private static bool HasLetter(char[] word, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (char.IsLetter(word[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDottedPart(ReadOnlySpan<char> text, int start, int end)
    {
        if (start > 1 && text[start - 1] == '.' && char.IsLetterOrDigit(text[start - 2]))
        {
            return true;
        }

        return end + 1 < text.Length && text[end] == '.' && char.IsLetterOrDigit(text[end + 1]);
    }

    // "Indexes" in the middle of a sentence. "Beginning" at the start is just a word.
    private static bool IsMidSentenceName(char[] word, int length, ReadOnlySpan<char> text, int start)
    {
        if (length < 2 || !char.IsUpper(word[0]) || IsSentenceStart(text, start))
        {
            return false;
        }

        for (var i = 1; i < length; i++)
        {
            if (!char.IsLower(word[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSentenceStart(ReadOnlySpan<char> text, int start)
    {
        var i = start - 1;
        while (i >= 0)
        {
            var c = text[i];
            if (c is '.' or '!' or '?' or ':' or ';')
            {
                return true;
            }

            if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || c is '#' or '>')
            {
                i--;
                continue;
            }

            return false;
        }

        return true;
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
