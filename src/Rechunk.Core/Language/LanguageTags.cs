namespace Rechunk;

/// <summary>
/// Helpers for the language tag of a chunk. A tag is a BCP 47 style code such as "en", "fr" or "zh".
/// Chunks only ever match chunks of the same language: the tag is part of the scope that all three stages and the
/// claims use, and part of the chunk id.
/// </summary>
public static class LanguageTags
{
    /// <summary>Separates the scope from the language in the scope the stores see. A control character, so no real scope has it.</summary>
    internal const char ScopeSeparator = '\u001F';

    /// <summary>Separates the hash from the language in a chunk id.</summary>
    internal const char IdSeparator = '.';

    /// <summary>
    /// Makes a tag comparable: trimmed, lower case, and cut to the primary language by default, so "en-US", "en_GB" and
    /// "EN" are all "en". Returns null for an empty tag and for "und" (undetermined), which both mean untagged.
    /// </summary>
    public static string? Normalize(string? tag, bool primaryOnly = true)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var value = tag.Trim().Replace('_', '-').ToLowerInvariant();
        if (primaryOnly)
        {
            var dash = value.IndexOf('-');
            if (dash > 0)
            {
                value = value[..dash];
            }
        }

        if (value == "und" || value.Length == 0)
        {
            return null;
        }

        foreach (var c in value)
        {
            if (!(c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '-'))
            {
                throw new ArgumentException($"'{tag}' is not a language tag. Use a code like en, fr or zh-hant.", nameof(tag));
            }
        }

        return value;
    }

    /// <summary>The scope the indexes and the store use for a chunk. Untagged chunks keep the plain scope.</summary>
    public static string ScopeFor(string scope, string? language) =>
        language is null ? scope : scope + ScopeSeparator + language;

    /// <summary>The id of a chunk. Untagged chunks keep the plain hex id, tagged ones get the language added.</summary>
    public static ChunkId IdFor(ReadOnlySpan<byte> exactHash, string? language)
    {
        var hex = Convert.ToHexString(exactHash).ToLowerInvariant();
        return new ChunkId(language is null ? hex : hex + IdSeparator + language);
    }

    /// <summary>Takes an id made by IdFor apart again.</summary>
    public static bool TryParseId(ChunkId id, out byte[] exactHash, out string? language)
    {
        exactHash = [];
        language = null;
        var value = id.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var dot = value.IndexOf(IdSeparator);
        var hex = dot < 0 ? value : value[..dot];
        if (hex.Length == 0 || hex.Length % 2 != 0)
        {
            return false;
        }

        try
        {
            exactHash = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return false;
        }

        language = dot < 0 ? null : value[(dot + 1)..];
        return true;
    }

    /// <summary>The language that is part of a chunk id, or null for an untagged chunk.</summary>
    public static string? FromId(ChunkId id) => TryParseId(id, out _, out var language) ? language : null;
}
