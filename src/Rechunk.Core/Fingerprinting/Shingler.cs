namespace Rechunk;

/// <summary>
/// Makes the shingle hashes that SimHash and MinHash work on. A shingle is a run of tokens in a row. Words and characters
/// (from scripts without spaces) are shingled apart, each with its own size, so a Chinese sentence in the middle of an
/// English paragraph gives character shingles for the sentence and word shingles for the rest.
/// A text with fewer tokens than the size, in one kind, gets one shingle with all of them. For text that has words only,
/// this is exactly the plain sliding window.
/// </summary>
internal static class Shingler
{
    public static List<ulong> Hashes(IReadOnlyList<WordToken> tokens, int wordSize, int charSize, ulong seed)
    {
        var hashes = new List<ulong>(tokens.Count);
        var start = 0;
        while (start < tokens.Count)
        {
            var kind = tokens[start].IsChar;
            var end = start + 1;
            while (end < tokens.Count && tokens[end].IsChar == kind)
            {
                end++;
            }

            var size = kind ? charSize : wordSize;
            var length = end - start;
            var count = Math.Max(1, length - size + 1);
            var width = Math.Min(size, length);

            for (var i = 0; i < count; i++)
            {
                var h = seed;
                for (var j = 0; j < width; j++)
                {
                    h = StableHash64.Combine(h, tokens[start + i + j].Hash);
                }

                hashes.Add(h);
            }

            start = end;
        }

        return hashes;
    }

    /// <summary>
    /// How many words the tokens are worth when MinWords is checked: a word is one, and a run of characters counts
    /// charsPerWord characters to the word.
    /// </summary>
    public static int Units(IReadOnlyList<WordToken> tokens, int charsPerWord)
    {
        var words = 0;
        var chars = 0;
        foreach (var token in tokens)
        {
            if (token.IsChar)
            {
                chars++;
            }
            else
            {
                words++;
            }
        }

        return words + chars / Math.Max(1, charsPerWord);
    }
}
