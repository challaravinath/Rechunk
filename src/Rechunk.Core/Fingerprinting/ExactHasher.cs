using System.Security.Cryptography;
using System.Text;

namespace Rechunk;

/// <summary>SHA-256 of the normalized text.</summary>
public static class ExactHasher
{
    public const string Version = "sha256-v1";

    public static byte[] Hash(ReadOnlySpan<char> text) => HashNormalized(TextNormalizer.Normalize(text));

    /// <summary>For callers that already normalized the text and do not want to do it twice.</summary>
    public static byte[] HashNormalized(string normalizedText) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(normalizedText));
}
