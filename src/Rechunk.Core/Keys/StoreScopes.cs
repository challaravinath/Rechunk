namespace Rechunk;

/// <summary>
/// The scope string a fingerprint row uses. A language tag is added after a separator.
/// The revision store keeps the caller's scope and puts the language on the chunk id instead.
/// </summary>
public static class StoreScopes
{
    /// <summary>Separates the caller's scope from the language. A control character, so a normal scope does not contain it.</summary>
    public const char LanguageSeparator = '\u001F';

    public static bool IsUserOrLanguage(string storeScope, string userScope)
    {
        if (string.Equals(storeScope, userScope, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = userScope + LanguageSeparator;
        return storeScope.StartsWith(prefix, StringComparison.Ordinal);
    }

    public static string? Language(string storeScope)
    {
        var split = storeScope.IndexOf(LanguageSeparator);
        return split < 0 ? null : storeScope[(split + 1)..];
    }
}
