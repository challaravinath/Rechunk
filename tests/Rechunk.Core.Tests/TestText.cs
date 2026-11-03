namespace Rechunk.Core.Tests;

/// <summary>Makes the same pseudo text every time, so tests do not depend on a data file.</summary>
internal static class TestText
{
    private static readonly string[] Syllables =
    {
        "ba", "ko", "ri", "lu", "me", "sa", "to", "ne", "vi", "do", "pa", "re", "ga", "shi", "mo", "ta",
    };

    public static string Word(Random random) =>
        Syllables[random.Next(Syllables.Length)] + Syllables[random.Next(Syllables.Length)] + Syllables[random.Next(Syllables.Length)];

    public static string[] Words(int seed, int count)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count).Select(_ => Word(random)).ToArray();
    }

    /// <summary>A paragraph of the given number of words. Same seed, same text.</summary>
    public static string Paragraph(int seed, int wordCount) => string.Join(' ', Words(seed, wordCount));

    /// <summary>The same paragraph with the word at the given position swapped for another one.</summary>
    public static string WithWordChanged(int seed, int wordCount, int position, string replacement)
    {
        var words = Words(seed, wordCount);
        words[position] = replacement;
        return string.Join(' ', words);
    }
}
