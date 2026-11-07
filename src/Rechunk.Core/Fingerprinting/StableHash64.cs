namespace Rechunk;

/// <summary>
/// The 64 bit hash that SimHash and the false merge guard are built on.
/// It is FNV-1a over the characters (each char is fed in as two bytes, low byte first, so the result does
/// not depend on the machine), followed by the murmur3 finalizer to spread the bits. It never uses
/// string.GetHashCode, which changes from run to run.
/// </summary>
internal static class StableHash64
{
    private const ulong FnvOffset = 0xcbf29ce484222325UL;
    private const ulong FnvPrime = 0x00000100000001b3UL;

    /// <summary>Hash of the characters as they are written, so "Style" and "style" stay apart.</summary>
    public static ulong Hash(ReadOnlySpan<char> chars)
    {
        var h = FnvOffset;
        foreach (var c in chars)
        {
            h = (h ^ (byte)c) * FnvPrime;
            h = (h ^ (byte)(c >> 8)) * FnvPrime;
        }

        return Mix(h);
    }

    /// <summary>Hash of the lower case form of the characters. Lower casing is char.ToLowerInvariant.</summary>
    public static ulong HashLower(ReadOnlySpan<char> chars)
    {
        var h = FnvOffset;
        foreach (var raw in chars)
        {
            var c = char.ToLowerInvariant(raw);
            h = (h ^ (byte)c) * FnvPrime;
            h = (h ^ (byte)(c >> 8)) * FnvPrime;
        }

        return Mix(h);
    }

    /// <summary>murmur3 fmix64. Every input bit changes about half of the output bits.</summary>
    public static ulong Mix(ulong k)
    {
        k ^= k >> 33;
        k *= 0xff51afd7ed558ccdUL;
        k ^= k >> 33;
        k *= 0xc4ceb9fe1a85ec53UL;
        k ^= k >> 33;
        return k;
    }

    /// <summary>Folds one more value into a running hash. The order of the values matters.</summary>
    public static ulong Combine(ulong running, ulong value) =>
        Mix(running ^ (value + 0x9e3779b97f4a7c15UL + (running << 6) + (running >> 2)));
}
