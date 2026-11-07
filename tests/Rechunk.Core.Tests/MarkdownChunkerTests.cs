using Rechunk.RealData;

namespace Rechunk.Core.Tests;

public class MarkdownChunkerTests
{
    [Fact]
    public void Drops_front_matter_and_splits_on_blank_lines()
    {
        var text = "---\ntitle: T\n---\n# Title\n\nOne paragraph.\n\nTwo paragraph.\n";

        Assert.Equal(new[] { "# Title", "One paragraph.", "Two paragraph." }, MarkdownChunker.Chunk(text));
    }

    [Fact]
    public void Heading_stays_with_the_text_under_it()
    {
        var text = "# Title\nThe body is here.\n\nNext.\n";

        Assert.Equal(new[] { "# Title\nThe body is here.", "Next." }, MarkdownChunker.Chunk(text));
    }

    [Fact]
    public void Keeps_a_fenced_block_as_one_chunk()
    {
        var text = "Before.\n\n```\nline\n\nline2\n```\n\nAfter.\n";

        Assert.Equal(new[] { "Before.", "```\nline\n\nline2\n```", "After." }, MarkdownChunker.Chunk(text));
    }

    [Fact]
    public void Drops_html_comments()
    {
        var text = "Hello.\n\n<!-- note -->\n\nWorld.\n";

        Assert.Equal(new[] { "Hello.", "World." }, MarkdownChunker.Chunk(text));
    }
}
