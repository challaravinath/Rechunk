namespace Rechunk.RealData;

/// <summary>
/// Splits one markdown file the way the real data run feeds the pipeline.
/// A chunk is a paragraph, a heading, or a fenced code block. A heading with
/// text on the following lines and no blank line between them stays as one chunk.
/// YAML front matter at the top of the file, and HTML comments, are left out.
/// </summary>
public static class MarkdownChunker
{
    public static List<string> Chunk(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        markdown = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        markdown = StripFrontMatter(markdown);
        markdown = StripComments(markdown);

        var chunks = new List<string>();
        var buffer = new List<string>();
        var inFence = false;
        var fenceChar = '\0';
        var fenceLen = 0;

        void Flush()
        {
            if (buffer.Count == 0)
            {
                return;
            }

            var text = string.Join('\n', buffer).Trim();
            buffer.Clear();
            if (text.Length > 0)
            {
                chunks.Add(text);
            }
        }

        foreach (var line in markdown.Split('\n'))
        {
            if (inFence)
            {
                buffer.Add(line);
                if (IsClosingFence(line, fenceChar, fenceLen))
                {
                    inFence = false;
                    Flush();
                }

                continue;
            }

            if (TryOpenFence(line, out fenceChar, out fenceLen))
            {
                Flush();
                inFence = true;
                buffer.Add(line);
                continue;
            }

            if (line.Trim().Length == 0)
            {
                Flush();
                continue;
            }

            if (IsHeading(line) && buffer.Count > 0)
            {
                Flush();
            }

            buffer.Add(line);
        }

        Flush();
        return chunks;
    }

    // Only a leading --- block. A horizontal rule later in the file is real text.
    private static string StripFrontMatter(string text)
    {
        var i = 0;
        while (i < text.Length && text[i] == '\n')
        {
            i++;
        }

        if (i + 3 >= text.Length || text[i] != '-' || text[i + 1] != '-' || text[i + 2] != '-')
        {
            return text;
        }

        if (text[i + 3] != '\n')
        {
            return text;
        }

        var close = text.IndexOf("\n---", i + 3, StringComparison.Ordinal);
        if (close < 0)
        {
            return text;
        }

        var after = close + 4;
        if (after < text.Length && text[after] == '\n')
        {
            after++;
        }

        return text[after..];
    }

    private static string StripComments(string text)
    {
        var start = text.IndexOf("<!--", StringComparison.Ordinal);
        if (start < 0)
        {
            return text;
        }

        var sb = new System.Text.StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (i + 3 < text.Length && text[i] == '<' && text[i + 1] == '!' && text[i + 2] == '-' && text[i + 3] == '-')
            {
                var end = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 3;
                continue;
            }

            sb.Append(text[i]);
            i++;
        }

        return sb.ToString();
    }

    private static bool IsHeading(string line)
    {
        var t = line.TrimStart();
        var i = 0;
        while (i < t.Length && t[i] == '#')
        {
            i++;
        }

        return i is >= 1 and <= 6 && i < t.Length && t[i] == ' ';
    }

    private static bool TryOpenFence(string line, out char marker, out int length)
    {
        marker = '\0';
        length = 0;
        var t = line.Trim();
        if (t.Length < 3 || (t[0] != '`' && t[0] != '~'))
        {
            return false;
        }

        var n = 0;
        while (n < t.Length && t[n] == t[0])
        {
            n++;
        }

        if (n < 3)
        {
            return false;
        }

        marker = t[0];
        length = n;
        return true;
    }

    private static bool IsClosingFence(string line, char marker, int length)
    {
        var t = line.Trim();
        if (t.Length < length)
        {
            return false;
        }

        for (var i = 0; i < t.Length; i++)
        {
            if (t[i] != marker)
            {
                return false;
            }
        }

        return true;
    }
}
