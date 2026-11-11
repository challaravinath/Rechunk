namespace Rechunk;

/// <summary>
/// HMAC chunk ids. Hash version 2 is HMAC-SHA256 of the SHA-256 of the cleaned text, under the scope key.
/// The text form starts with <see cref="Prefix"/> so it is not the digest itself.
/// Nothing in the pipeline calls this until a later change wires it in. Stored ids stay the hex digest until then.
/// </summary>
public static class ChunkIds
{
    public const int LegacyHashVersion = 1;

    public const int HmacHashVersion = 2;

    /// <summary>Marks a keyed id. The bytes after it are the HMAC, not the SHA-256 digest.</summary>
    public const string Prefix = "k2.";

    public static bool IsKeyed(ChunkId id) =>
        id.Value is { Length: > 0 } value && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Id for a digest that is already SHA-256 of the cleaned text.
    /// Cleaned text is what <see cref="ExactHasher"/> hashes. A language tag is appended after the HMAC.
    /// </summary>
    public static ChunkId Create(ReadOnlySpan<byte> digest, string? language, ScopeKey key)
    {
        if (digest.Length != 32)
        {
            throw new ArgumentException("The digest has to be SHA-256, 32 bytes.", nameof(digest));
        }

        Span<byte> mac = stackalloc byte[32];
        key.Hash(digest, mac);
        var value = Prefix + Convert.ToHexString(mac).ToLowerInvariant();
        if (language is not null)
        {
            value = value + LanguageTags.IdSeparator + language;
        }

        return new ChunkId(value);
    }

    /// <summary>Cleans the text with the existing exact hasher, then builds the HMAC id.</summary>
    public static ChunkId FromText(ReadOnlySpan<char> text, string? language, ScopeKey key) =>
        Create(ExactHasher.Hash(text), language, key);
}
