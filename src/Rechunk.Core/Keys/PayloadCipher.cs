using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Rechunk;

/// <summary>
/// AES-GCM for a stored signature payload. The key is HMAC-SHA256 of the label
/// <c>rechunk-payload-aes-v1</c> under the scope key. It is not the scope key and not the band key.
/// Each value gets a new 12 byte nonce and a 16 byte tag. The associated data is the scope, the chunk id
/// and the field name, so a ciphertext cannot be moved to another row or field.
/// </summary>
internal static class PayloadCipher
{
    internal const byte Version = 1;

    private static readonly byte[] Label = "rechunk-payload-aes-v1"u8.ToArray();

    public static byte[] Encrypt(ScopeKey key, string scope, string chunkId, string field, ReadOnlySpan<byte> plaintext)
    {
        var dataKey = new byte[32];
        var nonce = new byte[12];
        var tag = new byte[16];
        var cipher = new byte[plaintext.Length];
        try
        {
            key.Hash(Label, dataKey);
            RandomNumberGenerator.Fill(nonce);
            var aad = AssociatedData(scope, chunkId, field);
            using var aes = new AesGcm(dataKey, 16);
            aes.Encrypt(nonce, plaintext, cipher, tag, aad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }

        var packed = new byte[1 + nonce.Length + tag.Length + cipher.Length];
        packed[0] = Version;
        nonce.CopyTo(packed.AsSpan(1));
        tag.CopyTo(packed.AsSpan(13));
        cipher.CopyTo(packed.AsSpan(29));
        return packed;
    }

    public static byte[] Decrypt(ScopeKey key, string scope, string chunkId, string field, ReadOnlySpan<byte> packed)
    {
        if (packed.Length < 29 || packed[0] != Version)
        {
            throw new CryptographicException("The stored signature is not a payload this version can open.");
        }

        var dataKey = new byte[32];
        var plain = new byte[packed.Length - 29];
        try
        {
            key.Hash(Label, dataKey);
            var aad = AssociatedData(scope, chunkId, field);
            using var aes = new AesGcm(dataKey, 16);
            aes.Decrypt(packed.Slice(1, 12), packed[29..], packed.Slice(13, 16), plain, aad);
            return plain;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plain);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    private static byte[] AssociatedData(string scope, string chunkId, string field)
    {
        var parts = new[]
        {
            Encoding.UTF8.GetBytes(scope ?? string.Empty),
            Encoding.UTF8.GetBytes(chunkId ?? string.Empty),
            Encoding.UTF8.GetBytes(field ?? string.Empty),
        };
        var length = 0;
        foreach (var part in parts)
        {
            length += 4 + part.Length;
        }

        var aad = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(offset), part.Length);
            part.CopyTo(aad.AsSpan(offset + 4));
            offset += 4 + part.Length;
        }

        return aad;
    }
}
