namespace Rechunk;

/// <summary>
/// Decides that a chunk is mostly code, an include directive, or markup of the same kind
/// (a fenced sample, a docfx include, a config breadcrumb). Near duplicate hashes treat the
/// long shared prefix as the whole chunk and then merge two snippets that differ by an id.
/// A sentence that mentions one name in backticks is not this.
/// </summary>
internal static class CodeLikeText
{
    public const string Version = "codelike-v1";

    public static bool IsMostlyCodeOrMarkup(ReadOnlySpan<char> text)
    {
        if (text.IsWhiteSpace())
        {
            return false;
        }

        if (OpensWithFence(text) || EveryLineIsDirective(text) || IsMarkupHeavy(text) || HasDenseCodeSymbols(text))
        {
            return true;
        }

        return false;
    }

    private static bool OpensWithFence(ReadOnlySpan<char> text)
    {
        var i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        if (i >= text.Length)
        {
            return false;
        }

        var marker = text[i];
        if (marker != '`' && marker != '~')
        {
            return false;
        }

        var n = 0;
        while (i < text.Length && text[i] == marker)
        {
            n++;
            i++;
        }

        return n >= 3;
    }

    private static bool EveryLineIsDirective(ReadOnlySpan<char> text)
    {
        var any = false;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i != text.Length && text[i] != '\n')
            {
                continue;
            }

            if (!IsDirectiveLine(text[start..i]))
            {
                var line = text[start..i];
                if (!line.IsWhiteSpace())
                {
                    return false;
                }
            }
            else if (!text[start..i].IsWhiteSpace())
            {
                any = true;
            }

            start = i + 1;
        }

        return any;
    }

    private static bool IsDirectiveLine(ReadOnlySpan<char> line)
    {
        var t = line.Trim();
        while (t.Length > 0 && t[0] == '>')
        {
            t = t[1..].TrimStart();
        }

        if (t.StartsWith(":::"))
        {
            return true;
        }

        return t.StartsWith("[!code", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("[!include", StringComparison.OrdinalIgnoreCase);
    }

    // Config breadcrumbs are mostly brackets, backticks and &nbsp; indents. Prose with one link is not.
    private static bool IsMarkupHeavy(ReadOnlySpan<char> text)
    {
        var nonSpace = 0;
        var markup = 0;
        var nbsp = 0;
        var backtickTags = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (i + 6 <= text.Length && text[i] == '&' && text.Slice(i, 6).Equals("&nbsp;", StringComparison.Ordinal))
            {
                nbsp++;
                nonSpace += 6;
                markup += 6;
                i += 5;
                continue;
            }

            if (i + 1 < text.Length && text[i] == '`' && text[i + 1] == '<')
            {
                backtickTags++;
            }

            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            nonSpace++;
            if (c is '`' or '[' or ']' or '(' or ')' or '{' or '}' or '<' or '>' or '*' or '&' or '=' or '\\' or '#')
            {
                markup++;
            }
        }

        if (nonSpace == 0 || markup * 100 < nonSpace * 22)
        {
            return false;
        }

        return nbsp >= 2 || backtickTags >= 2;
    }

    // An unfenced sample. A sentence with one semicolon does not have enough of these.
    private static bool HasDenseCodeSymbols(ReadOnlySpan<char> text)
    {
        var symbols = 0;
        var letters = 0;
        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                letters++;
            }
            else if (c is ';' or '{' or '}')
            {
                symbols++;
            }
        }

        return symbols >= 3 && symbols * 12 >= letters;
    }
}
