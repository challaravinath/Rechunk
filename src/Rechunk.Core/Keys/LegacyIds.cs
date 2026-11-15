using System.Buffers.Binary;

namespace Rechunk;

internal static class LegacyIds
{
    public static (string NewChunk, string NewText) Rewrite(string storeScope, ReadOnlySpan<byte> hash, string chunk, string textId, ScopeKey key)
    {
        var language = StoreScopes.Language(storeScope);
        var own = ChunkIds.Create(hash, language, key).Value;
        var pointsAtOwner = TryOwner(chunk, hash, language, key, out var ownerId);
        var newChunk = pointsAtOwner ? ownerId : own;
        var newText = own;
        if (!string.IsNullOrEmpty(textId) && textId != chunk
            && LanguageTags.TryParseId(new ChunkId(textId), out var textHash, out var textLanguage))
        {
            newText = ChunkIds.Create(textHash, textLanguage ?? language, key).Value;
        }

        return (newChunk, newText);
    }

    private static bool TryOwner(string chunk, ReadOnlySpan<byte> hash, string? language, ScopeKey key, out string ownerId)
    {
        ownerId = string.Empty;
        if (!LanguageTags.TryParseId(new ChunkId(chunk), out var parsed, out var parsedLanguage))
        {
            return false;
        }

        if (parsed.AsSpan().SequenceEqual(hash))
        {
            return false;
        }

        ownerId = ChunkIds.Create(parsed, parsedLanguage ?? language, key).Value;
        return true;
    }
}

internal static class LegacyBands
{
    public static BandKey[] SimHash(ReadOnlySpan<byte> payload, SimHashBandLayout layout, ScopeKey key)
    {
        if (payload.Length < 8)
        {
            return Array.Empty<BandKey>();
        }

        var (k0, k1) = BandKeys.Derive(key);
        var sim = BinaryPrimitives.ReadUInt64LittleEndian(payload);
        var keys = new BandKey[layout.BandCount];
        for (var band = 0; band < layout.BandCount; band++)
        {
            var raw = layout.Slice(sim, band);
            keys[band] = new BandKey(band, BandKeys.Protect(k0, k1, BandKind.SimHash, band, raw));
        }

        return keys;
    }

    public static BandKey[] MinHash(ReadOnlySpan<byte> payload, LshParameters lsh, ScopeKey key)
    {
        const int Header = 24;
        if (payload.Length < Header + 4 || (payload.Length - Header) % 4 != 0)
        {
            return Array.Empty<BandKey>();
        }

        var slots = (payload.Length - Header) / 4;
        if ((long)lsh.Bands * lsh.Rows > slots)
        {
            return Array.Empty<BandKey>();
        }

        var signature = new uint[slots];
        for (var i = 0; i < slots; i++)
        {
            signature[i] = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(Header + (4 * i)));
        }

        var (k0, k1) = BandKeys.Derive(key);
        var keys = new BandKey[lsh.Bands];
        for (var band = 0; band < lsh.Bands; band++)
        {
            var raw = lsh.BandKey(signature, band);
            keys[band] = new BandKey(band, BandKeys.Protect(k0, k1, BandKind.MinHash, band, raw));
        }

        return keys;
    }
}
