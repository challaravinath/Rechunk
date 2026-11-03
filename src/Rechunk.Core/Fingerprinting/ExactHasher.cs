using System.Security.Cryptography;
using System.Text;

namespace Rechunk;

/// <summary>SHA-256 of the normalized text.</summary>
public static class ExactHasher
{
    public const string Version = "sha256-v1";

    public static byte[] Hash(ReadOnlySpan<char> text)
    {
        var normalized = TextNormalizer.Normalize(text);
        return SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
    }
}
