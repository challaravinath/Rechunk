namespace Rechunk;

/// <summary>
/// The checksums and tests the built in detector uses to tell a real id from a number that only looks like one. Public so
/// a detector of your own can reuse them.
/// </summary>
public static class PiiChecks
{
    /// <summary>The Luhn check used by payment cards. The digits only, no spaces or dashes.</summary>
    public static bool Luhn(ReadOnlySpan<char> digits)
    {
        if (digits.Length < 2)
        {
            return false;
        }

        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var c = digits[i];
            if (c is < '0' or > '9')
            {
                return false;
            }

            var d = c - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }

    private static readonly byte[,] VerhoeffD =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 },
        { 1, 2, 3, 4, 0, 6, 7, 8, 9, 5 },
        { 2, 3, 4, 0, 1, 7, 8, 9, 5, 6 },
        { 3, 4, 0, 1, 2, 8, 9, 5, 6, 7 },
        { 4, 0, 1, 2, 3, 9, 5, 6, 7, 8 },
        { 5, 9, 8, 7, 6, 0, 4, 3, 2, 1 },
        { 6, 5, 9, 8, 7, 1, 0, 4, 3, 2 },
        { 7, 6, 5, 9, 8, 2, 1, 0, 4, 3 },
        { 8, 7, 6, 5, 9, 3, 2, 1, 0, 4 },
        { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 },
    };

    private static readonly byte[,] VerhoeffP =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 },
        { 1, 5, 7, 6, 2, 8, 3, 0, 9, 4 },
        { 5, 8, 0, 3, 7, 9, 6, 1, 4, 2 },
        { 8, 9, 1, 6, 0, 4, 3, 5, 2, 7 },
        { 9, 4, 5, 3, 1, 2, 6, 8, 7, 0 },
        { 4, 2, 8, 6, 5, 7, 3, 9, 0, 1 },
        { 2, 7, 9, 3, 8, 0, 6, 4, 1, 5 },
        { 7, 0, 4, 6, 9, 1, 3, 2, 5, 8 },
    };

    /// <summary>The Verhoeff check used by Aadhaar numbers. The digits only, the last one is the check digit.</summary>
    public static bool Verhoeff(ReadOnlySpan<char> digits)
    {
        if (digits.Length < 2)
        {
            return false;
        }

        var c = 0;
        var position = 0;
        for (var i = digits.Length - 1; i >= 0; i--, position++)
        {
            var ch = digits[i];
            if (ch is < '0' or > '9')
            {
                return false;
            }

            c = VerhoeffD[c, VerhoeffP[position % 8, ch - '0']];
        }

        return c == 0;
    }

    // IBAN length by country, from the SWIFT registry (the countries that use it).
    private static readonly Dictionary<string, int> IbanLengths = new(StringComparer.Ordinal)
    {
        ["AD"] = 24, ["AE"] = 23, ["AL"] = 28, ["AT"] = 20, ["AZ"] = 28, ["BA"] = 20, ["BE"] = 16, ["BG"] = 22,
        ["BH"] = 22, ["BI"] = 27, ["BR"] = 29, ["BY"] = 28, ["CH"] = 21, ["CR"] = 22, ["CY"] = 28, ["CZ"] = 24,
        ["DE"] = 22, ["DJ"] = 27, ["DK"] = 18, ["DO"] = 28, ["EE"] = 20, ["EG"] = 29, ["ES"] = 24, ["FI"] = 18,
        ["FK"] = 18, ["FO"] = 18, ["FR"] = 27, ["GB"] = 22, ["GE"] = 22, ["GI"] = 23, ["GL"] = 18, ["GR"] = 27,
        ["GT"] = 28, ["HN"] = 28, ["HR"] = 21, ["HU"] = 28, ["IE"] = 22, ["IL"] = 23, ["IQ"] = 23, ["IS"] = 26,
        ["IT"] = 27, ["JO"] = 30, ["KW"] = 30, ["KZ"] = 20, ["LB"] = 28, ["LC"] = 32, ["LI"] = 21, ["LT"] = 20,
        ["LU"] = 20, ["LV"] = 21, ["LY"] = 25, ["MC"] = 27, ["MD"] = 24, ["ME"] = 22, ["MK"] = 19, ["MN"] = 20,
        ["MR"] = 27, ["MT"] = 31, ["MU"] = 30, ["NI"] = 28, ["NL"] = 18, ["NO"] = 15, ["OM"] = 23, ["PK"] = 24,
        ["PL"] = 28, ["PS"] = 29, ["PT"] = 25, ["QA"] = 29, ["RO"] = 24, ["RS"] = 22, ["RU"] = 33, ["SA"] = 24,
        ["SC"] = 31, ["SD"] = 18, ["SE"] = 24, ["SI"] = 19, ["SK"] = 24, ["SM"] = 27, ["SO"] = 23, ["ST"] = 25,
        ["SV"] = 28, ["TL"] = 23, ["TN"] = 24, ["TR"] = 26, ["UA"] = 29, ["VA"] = 22, ["VG"] = 24, ["XK"] = 20,
        ["YE"] = 30,
    };

    /// <summary>True when the text (letters and digits, no spaces) is the right length for its country and the mod 97 check passes.</summary>
    public static bool Iban(ReadOnlySpan<char> compact)
    {
        if (compact.Length < 15 || compact.Length > 34)
        {
            return false;
        }

        var country = compact[..2].ToString();
        if (!IbanLengths.TryGetValue(country, out var length) || length != compact.Length)
        {
            return false;
        }

        // move the first four characters to the end and read letters as 10..35, then take the remainder by 97 as we go
        var remainder = 0;
        for (var i = 0; i < compact.Length; i++)
        {
            var c = compact[(i + 4) % compact.Length];
            if (c is >= '0' and <= '9')
            {
                remainder = (remainder * 10 + (c - '0')) % 97;
            }
            else if (c is >= 'A' and <= 'Z')
            {
                remainder = (remainder * 100 + (c - 'A' + 10)) % 97;
            }
            else
            {
                return false;
            }
        }

        return remainder == 1 && compact[2] is >= '0' and <= '9' && compact[3] is >= '0' and <= '9';
    }

    /// <summary>Shannon entropy in bits per character.</summary>
    public static double Entropy(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return 0;
        }

        var counts = new Dictionary<char, int>();
        foreach (var c in text)
        {
            counts[c] = counts.GetValueOrDefault(c) + 1;
        }

        var entropy = 0.0;
        foreach (var count in counts.Values)
        {
            var p = (double)count / text.Length;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }
}
