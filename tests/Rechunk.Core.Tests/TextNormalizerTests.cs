namespace Rechunk.Core.Tests;

public class TextNormalizerTests
{
    [Fact]
    public void Collapses_whitespace_and_trims()
    {
        Assert.Equal("hello world", TextNormalizer.Normalize("  hello \t\r\n  world  "));
    }

    [Fact]
    public void Keeps_case()
    {
        Assert.Equal("Hello World", TextNormalizer.Normalize("Hello World"));
    }

    [Fact]
    public void Composes_unicode_to_nfc()
    {
        // "e" plus a combining accent, and the single character version
        Assert.Equal(TextNormalizer.Normalize("caf\u00e9"), TextNormalizer.Normalize("cafe\u0301"));
    }

    [Fact]
    public void Empty_and_whitespace_only_text_gives_empty_string()
    {
        Assert.Equal(string.Empty, TextNormalizer.Normalize(""));
        Assert.Equal(string.Empty, TextNormalizer.Normalize("   \n\t "));
    }
}
