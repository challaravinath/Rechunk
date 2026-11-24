using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Rechunk;

/// <summary>
/// Proof that an erase by subject ran. It names the keyed subject id, the keyed chunk ids, and the counts.
/// It does not contain the subject value, chunk text, or a fingerprint. Signature is HMAC-SHA256 under the scope key.
/// </summary>
public sealed record ErasureReceipt
{
    /// <summary>False when this scope has no key provider, so there is nothing to erase and nothing to sign with.</summary>
    public bool Available { get; init; }

    /// <summary>Id of this receipt. Null when the erase was not available.</summary>
    public string? Id { get; init; }

    public string Scope { get; init; } = string.Empty;

    /// <summary>The keyed subject id, or null when the value was empty or the erase was not available.</summary>
    public string? SubjectId { get; init; }

    public DateTimeOffset Timestamp { get; init; }

    public int ChunksRemoved { get; init; }

    /// <summary>Manifest entries removed, including near copies that pointed at an erased chunk.</summary>
    public int ReferencesRemoved { get; init; }

    public int RevisionsAffected { get; init; }

    public int SubjectLinksRemoved { get; init; }

    /// <summary>Keyed chunk ids that were erased, including near-copy ids that pointed at them. Sorted.</summary>
    public IReadOnlyList<string> ChunkIds { get; init; } = Array.Empty<string>();

    /// <summary>Version of the scope key that signed the receipt. Null when it was not signed.</summary>
    public int? KeyVersion { get; init; }

    /// <summary>Hex HMAC-SHA256 of the canonical payload. Null when the erase was not available.</summary>
    public string? Signature { get; init; }

    public static ErasureReceipt NotAvailable(string scope) => new() { Scope = scope, Available = false };
}

/// <summary>Builds and checks the canonical bytes of an erasure receipt. The label is <c>rechunk-erasure-receipt-v1</c>.</summary>
public static class ErasureReceipts
{
    public const string Label = "rechunk-erasure-receipt-v1";

    public static string CanonicalPayload(ErasureReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var ids = (receipt.ChunkIds ?? Array.Empty<string>()).OrderBy(id => id, StringComparer.Ordinal);
        var text = new StringBuilder();
        text.Append(receipt.Id ?? string.Empty).Append('\n');
        text.Append(receipt.Scope ?? string.Empty).Append('\n');
        text.Append(receipt.SubjectId ?? string.Empty).Append('\n');
        text.Append(receipt.Timestamp.ToUniversalTime().UtcTicks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append(receipt.KeyVersion.HasValue ? receipt.KeyVersion.Value.ToString(CultureInfo.InvariantCulture) : string.Empty).Append('\n');
        text.Append(receipt.ChunksRemoved.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append(receipt.ReferencesRemoved.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append(receipt.RevisionsAffected.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append(receipt.SubjectLinksRemoved.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var id in ids)
        {
            text.Append(id).Append('\n');
        }

        return text.ToString();
    }

    public static string Sign(ScopeKey key, ErasureReceipt receipt)
    {
        var payload = Encoding.UTF8.GetBytes(CanonicalPayload(receipt));
        var label = Encoding.ASCII.GetBytes(Label);
        var message = new byte[label.Length + 1 + payload.Length];
        label.CopyTo(message, 0);
        message[label.Length] = 0;
        payload.CopyTo(message, label.Length + 1);
        try
        {
            using var hmac = new HMACSHA256(key.Bytes);
            return Convert.ToHexString(hmac.ComputeHash(message)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(message);
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public static bool Matches(ScopeKey key, ErasureReceipt receipt)
    {
        if (receipt.Signature is not { Length: > 0 } signature)
        {
            return false;
        }

        byte[] given;
        try
        {
            given = Convert.FromHexString(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        var expectedHex = Sign(key, receipt);
        var expected = Convert.FromHexString(expectedHex);
        return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
    }
}
