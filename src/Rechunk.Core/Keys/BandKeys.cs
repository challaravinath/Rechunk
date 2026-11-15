using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Rechunk;

/// <summary>
/// Turns a raw band slice into the value that is stored and looked up.
///
/// SimHash and MinHash are unchanged. This only hides the slice used as a bucket key, so a stored
/// index cannot be probed for a guessed text without the scope key. The signature payload stays as it is.
///
/// SipHash-2-4 is a keyed pseudorandom function for short messages (Aumasson and Bernstein). The output
/// is 64 bits. Two different slices can land on the same value. That only adds a candidate, and the real
/// SimHash distance or MinHash estimate still decides. It is not used for chunk ids. Those stay HMAC-SHA256.
///
/// The 128 bit SipHash key is HMAC-SHA256 of a fixed label under the scope key, not a copy of the scope
/// key. The label is <c>rechunk-band-siphash-v1</c>. The message is the band kind, the band number, and
/// the raw slice, so the same number in two bands does not share a bucket.
/// </summary>
internal static class BandKeys
{
    private static readonly ConditionalWeakTable<IKeyProvider, BandKeyDirectory> Directories = new();

    public static BandKeyDirectory For(IKeyProvider provider) =>
        Directories.GetValue(provider, static p => new BandKeyDirectory(p));

    internal static (ulong K0, ulong K1) Derive(ScopeKey key)
    {
        Span<byte> mac = stackalloc byte[32];
        key.Hash("rechunk-band-siphash-v1"u8, mac);
        var k0 = BinaryPrimitives.ReadUInt64LittleEndian(mac);
        var k1 = BinaryPrimitives.ReadUInt64LittleEndian(mac[8..]);
        CryptographicOperations.ZeroMemory(mac);
        return (k0, k1);
    }

    internal static ulong Protect(ulong k0, ulong k1, BandKind kind, int band, ulong raw)
    {
        Span<byte> message = stackalloc byte[11];
        message[0] = (byte)kind;
        BinaryPrimitives.WriteUInt16LittleEndian(message[1..], checked((ushort)band));
        BinaryPrimitives.WriteUInt64LittleEndian(message[3..], raw);
        return SipHash24.Compute(k0, k1, message);
    }

    internal static string UserScope(string storeScope)
    {
        var split = storeScope.IndexOf(LanguageTags.ScopeSeparator);
        return split < 0 ? storeScope : storeScope[..split];
    }
}

internal sealed class BandKeyDirectory
{
    private readonly IKeyProvider _keys;
    private readonly ConcurrentDictionary<string, (ulong K0, ulong K1)> _cache = new(StringComparer.Ordinal);

    public BandKeyDirectory(IKeyProvider keys)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    }

    public void Warm(string scope, ScopeKey key) => _cache[scope] = BandKeys.Derive(key);

    public byte[] Seal(string storeScope, string chunkId, string field, ReadOnlySpan<byte> plaintext) =>
        PayloadCipher.Encrypt(Require(storeScope), storeScope, chunkId, field, plaintext);

    public byte[] Open(string storeScope, string chunkId, string field, ReadOnlySpan<byte> packed) =>
        PayloadCipher.Decrypt(Require(storeScope), storeScope, chunkId, field, packed);

    private ScopeKey Require(string storeScope)
    {
        var user = BandKeys.UserScope(storeScope);
        if (!_keys.TryGetCached(user, out var key))
        {
            throw new KeyUnavailableException(user);
        }

        return key;
    }

    public ulong Protect(string storeScope, BandKind kind, int band, ulong raw)
    {
        var user = BandKeys.UserScope(storeScope);
        if (!_cache.TryGetValue(user, out var key))
        {
            if (!_keys.TryGetCached(user, out var scopeKey))
            {
                throw new KeyUnavailableException(user);
            }

            key = BandKeys.Derive(scopeKey);
            _cache[user] = key;
        }

        return BandKeys.Protect(key.K0, key.K1, kind, band, raw);
    }
}

/// <summary>SipHash-2-4. The key is two 64 bit words. The output is one 64 bit word.</summary>
internal static class SipHash24
{
    public static ulong Compute(ulong k0, ulong k1, ReadOnlySpan<byte> message)
    {
        var v0 = 0x736f6d6570736575UL ^ k0;
        var v1 = 0x646f72616e646f6dUL ^ k1;
        var v2 = 0x6c7967656e657261UL ^ k0;
        var v3 = 0x7465646279746573UL ^ k1;

        var offset = 0;
        while (offset + 8 <= message.Length)
        {
            var block = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(offset, 8));
            v3 ^= block;
            Round(ref v0, ref v1, ref v2, ref v3);
            Round(ref v0, ref v1, ref v2, ref v3);
            v0 ^= block;
            offset += 8;
        }

        var last = (ulong)message.Length << 56;
        for (var i = 0; i < message.Length - offset; i++)
        {
            last |= (ulong)message[offset + i] << (8 * i);
        }

        v3 ^= last;
        Round(ref v0, ref v1, ref v2, ref v3);
        Round(ref v0, ref v1, ref v2, ref v3);
        v0 ^= last;

        v2 ^= 0xff;
        Round(ref v0, ref v1, ref v2, ref v3);
        Round(ref v0, ref v1, ref v2, ref v3);
        Round(ref v0, ref v1, ref v2, ref v3);
        Round(ref v0, ref v1, ref v2, ref v3);
        return v0 ^ v1 ^ v2 ^ v3;
    }

    private static void Round(ref ulong v0, ref ulong v1, ref ulong v2, ref ulong v3)
    {
        v0 += v1;
        v1 = BitOperations.RotateLeft(v1, 13);
        v1 ^= v0;
        v0 = BitOperations.RotateLeft(v0, 32);

        v2 += v3;
        v3 = BitOperations.RotateLeft(v3, 16);
        v3 ^= v2;

        v0 += v3;
        v3 = BitOperations.RotateLeft(v3, 21);
        v3 ^= v0;

        v2 += v1;
        v1 = BitOperations.RotateLeft(v1, 17);
        v1 ^= v2;
        v2 = BitOperations.RotateLeft(v2, 32);
    }
}
