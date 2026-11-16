using System.Security.Cryptography;
using System.Text;

namespace Rechunk;

/// <summary>Kind names the index understands. Any other non-empty kind is accepted and kept as you spelled the letters, after trim and lower case.</summary>
public static class SubjectKinds
{
    public const string Email = "email";

    public const string Phone = "phone";

    public const string Customer = "customer";
}

/// <summary>
/// A person this chunk is about, before it is keyed. The value is not written to a store. Printing a subject shows the kind only.
/// </summary>
public readonly record struct SubjectRef(string Kind, string Value)
{
    public override string ToString() => Kind ?? string.Empty;
}

/// <summary>
/// A subject after keying. Id is the keyed id, not the value it came from.
/// </summary>
public readonly record struct SubjectKey
{
    public SubjectKey(string kind, string id)
    {
        if (string.IsNullOrWhiteSpace(kind) || kind.IndexOfAny(['\t', '\r', '\n']) >= 0)
        {
            throw new ArgumentException("Subject kind has to be a short name.", nameof(kind));
        }

        if (!SubjectIds.IsKeyed(id))
        {
            throw new ArgumentException("Subject id has to be a keyed id.", nameof(id));
        }

        Kind = kind.Trim().ToLowerInvariant();
        Id = id;
    }

    public string Kind { get; }

    public string Id { get; }

    public override string ToString() => Kind;
}

/// <summary>
/// Turns a subject into a keyed id. The id is HMAC-SHA256 under the scope key.
/// The message is the label <c>rechunk-subject-v1</c>, a zero byte, the kind, a zero byte, and the normalized value.
/// The text form starts with <c>s1.</c> so it is not a chunk id and not the value.
/// </summary>
public static class SubjectIds
{
    public const string Label = "rechunk-subject-v1";

    public const string Prefix = "s1.";

    public static bool IsKeyed(string? id) =>
        id is { Length: 67 } && id.StartsWith(Prefix, StringComparison.Ordinal) && IsHex(id.AsSpan(Prefix.Length));

    /// <summary>
    /// Email is trimmed and lowercased. Phone keeps a leading + when the trimmed value has one, then digits only.
    /// Any other kind is trimmed and keeps its case. Empty after that is dropped.
    /// </summary>
    public static string? Normalize(string kind, string value)
    {
        if (string.IsNullOrWhiteSpace(kind) || value is null)
        {
            return null;
        }

        var name = kind.Trim();
        if (name.Equals(SubjectKinds.Email, StringComparison.OrdinalIgnoreCase))
        {
            var email = value.Trim().ToLowerInvariant();
            return email.Length == 0 ? null : email;
        }

        if (name.Equals(SubjectKinds.Phone, StringComparison.OrdinalIgnoreCase))
        {
            return NormalizePhone(value);
        }

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    public static SubjectKey? TryKey(string kind, string value, ScopeKey key)
    {
        var normalized = Normalize(kind, value);
        if (normalized is null)
        {
            return null;
        }

        var kindName = kind.Trim().ToLowerInvariant();
        Span<byte> mac = stackalloc byte[32];
        Hash(key, kindName, normalized, mac);
        return new SubjectKey(kindName, Prefix + Convert.ToHexString(mac).ToLowerInvariant());
    }

    public static List<SubjectKey> KeyAll(IReadOnlyList<SubjectRef>? subjects, ScopeKey key)
    {
        var list = new List<SubjectKey>();
        if (subjects is null)
        {
            return list;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subject in subjects)
        {
            var keyed = TryKey(subject.Kind, subject.Value, key);
            if (keyed is { } item && seen.Add(item.Id))
            {
                list.Add(item);
            }
        }

        return list;
    }

    public static Dictionary<string, int> CountByKind(IReadOnlyList<SubjectKey> subjects)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var subject in subjects)
        {
            counts.TryGetValue(subject.Kind, out var n);
            counts[subject.Kind] = n + 1;
        }

        return counts;
    }

    private static string? NormalizePhone(string value)
    {
        var trimmed = value.Trim();
        var plus = trimmed.StartsWith('+');
        var digits = new StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            if (c is >= '0' and <= '9')
            {
                digits.Append(c);
            }
        }

        if (digits.Length == 0)
        {
            return null;
        }

        return plus ? "+" + digits : digits.ToString();
    }

    private static void Hash(ScopeKey key, string kind, string normalized, Span<byte> destination)
    {
        var kindBytes = Encoding.UTF8.GetBytes(kind);
        var valueBytes = Encoding.UTF8.GetBytes(normalized);
        var labelBytes = Encoding.ASCII.GetBytes(Label);
        var message = new byte[labelBytes.Length + 2 + kindBytes.Length + valueBytes.Length];
        labelBytes.CopyTo(message, 0);
        var at = labelBytes.Length;
        message[at++] = 0;
        kindBytes.CopyTo(message, at);
        at += kindBytes.Length;
        message[at++] = 0;
        valueBytes.CopyTo(message, at);

        try
        {
            using var hmac = new HMACSHA256(key.Bytes);
            if (!hmac.TryComputeHash(message, destination, out var written) || written != 32)
            {
                throw new CryptographicException("Subject id could not be keyed.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(message);
            CryptographicOperations.ZeroMemory(valueBytes);
        }
    }

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
