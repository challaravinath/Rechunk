using System.Text.RegularExpressions;

namespace Rechunk;

/// <summary>
/// The detector that ships with Rechunk. It looks for card numbers (Luhn and a known issuer prefix), common API keys and
/// tokens (known prefixes, or a key name followed by a random looking value), emails, phone numbers, Aadhaar (Verhoeff),
/// PAN, US SSN and IBAN (mod 97). It only reports a number when more than its shape matches, so order ids, version
/// strings, dates and random digit runs are left alone. It is a set of rules, not a model. Names, addresses and free
/// text are not found, use a service for those (see <see cref="PiiServiceDetector"/>).
/// </summary>
public sealed partial class BuiltInPiiDetector : IPiiDetector
{
    private const int TimeoutMs = 250;

    private readonly HashSet<PiiType>? _only;

    public BuiltInPiiDetector()
    {
    }

    /// <summary>Only looks for these types, which saves the time of the other rules.</summary>
    public BuiltInPiiDetector(IEnumerable<PiiType> only)
    {
        ArgumentNullException.ThrowIfNull(only);
        _only = new HashSet<PiiType>(only);
    }

    public ValueTask<IReadOnlyList<PiiFinding>> DetectAsync(string text, string? language, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<PiiFinding>>(Scan(text));
    }

    /// <summary>The same as DetectAsync, for code that does not want a task.</summary>
    public IReadOnlyList<PiiFinding> Scan(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var found = new List<PiiFinding>();
        if (text.Length < 5)
        {
            return found;
        }

        if (Wants(PiiType.ApiKey))
        {
            Guarded(() => FindKeys(text, found));
        }

        if (Wants(PiiType.Card) || Wants(PiiType.Aadhaar))
        {
            FindDigitIds(text, found);
        }

        if (Wants(PiiType.Iban))
        {
            Guarded(() => FindIbans(text, found));
        }

        if (Wants(PiiType.Ssn))
        {
            Guarded(() => FindSsn(text, found));
        }

        if (Wants(PiiType.Pan))
        {
            Guarded(() => FindPan(text, found));
        }

        if (Wants(PiiType.Email))
        {
            Guarded(() => FindEmails(text, found));
        }

        if (Wants(PiiType.Phone))
        {
            Guarded(() => FindPhones(text, found));
        }

        return found;
    }

    private bool Wants(PiiType type) => _only is null || _only.Contains(type);

    // a rule that runs out of time on strange text finds nothing, the other rules still run
    private static void Guarded(Action rule)
    {
        try
        {
            rule();
        }
        catch (RegexMatchTimeoutException)
        {
        }
    }

    // ---- API keys and tokens ----

    [GeneratedRegex(@"\b(?:AKIA|ASIA|AGPA|AIDA|AROA|ANPA)(?<b>[A-Z2-7]{16})\b", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex AwsKey();

    [GeneratedRegex(@"\b(?:gh[pousr]_(?<b>[A-Za-z0-9]{36,255})|github_pat_(?<b>[A-Za-z0-9_]{22,255}))\b", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"(?<![A-Za-z0-9_\-])sk-(?:proj-|ant-[a-z0-9]{2,8}-)?(?<b>[A-Za-z0-9_\-]{20,200})(?![A-Za-z0-9_\-])", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SkKey();

    [GeneratedRegex(@"\b[sr]k_(?:live|test)_(?<b>[A-Za-z0-9]{16,99})\b", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex StripeKey();

    [GeneratedRegex(@"\bxox[abprs]-(?<b>[A-Za-z0-9\-]{10,250})", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"\bhooks\.slack\.com/services/T[A-Z0-9]{6,}/B[A-Z0-9]{6,}/(?<b>[A-Za-z0-9]{16,})", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SlackWebhook();

    [GeneratedRegex(@"\bAIza(?<b>[0-9A-Za-z_\-]{35})(?![0-9A-Za-z_\-])", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex GoogleKey();

    [GeneratedRegex(@"\bSG\.(?<b>[A-Za-z0-9_\-]{22}\.[A-Za-z0-9_\-]{43})(?![A-Za-z0-9_\-])", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SendGridKey();

    [GeneratedRegex(@"\bnpm_(?<b>[A-Za-z0-9]{36})\b", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex NpmToken();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.(?<b>[A-Za-z0-9_\-]{8,})", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"-----BEGIN (?:[A-Z]+ )*PRIVATE KEY-----(?:[\s\S]*?-----END (?:[A-Z]+ )*PRIVATE KEY-----)?", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\bAccountKey=(?<b>[A-Za-z0-9+/]{60,}={0,2})", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex AzureStorageKey();

    // a key name, a colon or equals sign, then the value. The value has to look random.
    [GeneratedRegex(
        @"\b(?:api[_\-]?key|apikey|secret(?:[_\-]?key)?|client[_\-]?secret|access[_\-]?token|auth[_\-]?token|token|passwd|password|pwd)\b[""']?\s*(?::|=|=>)\s*[""']?(?<v>[A-Za-z0-9_\-+/=.~]{12,})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex KeyAssignment();

    private static readonly string[] Placeholders =
    {
        "example", "placeholder", "changeme", "change_me", "yourkey", "your_", "your-", "dummy", "sample", "redacted",
        "xxxx", "****", "0000", "abcdef", "insert", "replace", "none", "null", "undefined", "<", ">",
    };

    private static void FindKeys(string text, List<PiiFinding> found)
    {
        // prefix rules: the body after the prefix has to be random enough, so AKIAXXXXXXXXXXXXXXXX is not a key
        Prefixed(AwsKey(), text, found, 2.5, mixed: false);
        Prefixed(GitHubToken(), text, found, 3.0, mixed: false);
        Prefixed(SkKey(), text, found, 3.3, mixed: true);
        Prefixed(StripeKey(), text, found, 3.0, mixed: false);
        Prefixed(SlackToken(), text, found, 2.5, mixed: false);
        Prefixed(SlackWebhook(), text, found, 3.0, mixed: false);
        Prefixed(GoogleKey(), text, found, 3.3, mixed: false);
        Prefixed(SendGridKey(), text, found, 3.3, mixed: false);
        Prefixed(NpmToken(), text, found, 3.0, mixed: false);
        Prefixed(Jwt(), text, found, 3.0, mixed: false);
        Prefixed(AzureStorageKey(), text, found, 4.0, mixed: false);

        foreach (Match m in PrivateKey().Matches(text))
        {
            found.Add(new PiiFinding(PiiType.ApiKey, m.Index, m.Length));
        }

        foreach (Match m in KeyAssignment().Matches(text))
        {
            var value = m.Groups["v"];
            var v = value.Value.TrimEnd('.', ',', ';');
            if (v.Length < 12 || LooksLikePlaceholder(v) || !IsMixed(v))
            {
                continue;
            }

            // hex is allowed a lower bar than base64 because it can not get above 4 bits per character
            var hex = v.All(Uri.IsHexDigit);
            if (PiiChecks.Entropy(v) < (hex ? 3.0 : 3.5))
            {
                continue;
            }

            found.Add(new PiiFinding(PiiType.ApiKey, value.Index, v.Length, 0.9));
        }
    }

    private static void Prefixed(Regex rule, string text, List<PiiFinding> found, double minEntropy, bool mixed)
    {
        foreach (Match m in rule.Matches(text))
        {
            var body = m.Groups["b"];
            var value = body.Success ? body.Value : m.Value;
            if (LooksLikePlaceholder(value) || PiiChecks.Entropy(value) < minEntropy || (mixed && !IsMixed(value)))
            {
                continue;
            }

            found.Add(new PiiFinding(PiiType.ApiKey, m.Index, m.Length));
        }
    }

    private static bool LooksLikePlaceholder(string value)
    {
        foreach (var p in Placeholders)
        {
            if (value.Contains(p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // letters and digits both, a long word is not a key
    private static bool IsMixed(string value) =>
        value.Any(char.IsLetter) && value.Any(char.IsDigit);

    // ---- cards and Aadhaar, found in runs of digits ----

    private readonly record struct Group(int Start, int Length);

    private sealed record Run(List<Group> Groups, List<char> Separators);

    private static readonly string[] CardWords = { "card", "visa", "mastercard", "master card", "amex", "credit", "debit", "cvv", "expiry", "rupay" };

    private static readonly string[] NotCardWords =
    {
        "order", "invoice", "tracking", "shipment", "ticket", "serial", "sku", "reference", "ref ", "ref:", "txn",
        "transaction", "awb", "consignment", "booking", "pnr", "receipt", "account no", "batch",
    };

    private static readonly string[] AadhaarWords = { "aadhaar", "aadhar", "uidai", "uid:", "uid ", "aadhaar no" };

    private void FindDigitIds(string text, List<PiiFinding> found)
    {
        foreach (var run in Runs(text))
        {
            var groups = run.Groups;
            var i = 0;
            while (i < groups.Count)
            {
                if (!(i == 0 || run.Separators[i - 1] == ' ') || !StartBoundary(text, groups[i].Start))
                {
                    i++;
                    continue;
                }

                var matched = false;
                for (var j = groups.Count - 1; j >= i && !matched; j--)
                {
                    if (!(j == groups.Count - 1 || run.Separators[j] == ' ') || !EndBoundary(text, groups[j].Start + groups[j].Length))
                    {
                        continue;
                    }

                    matched = TryRun(text, run, i, j, found);
                    if (matched)
                    {
                        i = j + 1;
                    }
                }

                if (!matched)
                {
                    i++;
                }
            }
        }
    }

    private bool TryRun(string text, Run run, int first, int last, List<PiiFinding> found)
    {
        var lengths = new List<int>();
        var digits = new System.Text.StringBuilder();
        var sep = '\0';
        for (var g = first; g <= last; g++)
        {
            var group = run.Groups[g];
            lengths.Add(group.Length);
            digits.Append(text, group.Start, group.Length);
            if (g < last)
            {
                var s = run.Separators[g];
                if (sep != '\0' && s != sep)
                {
                    return false;
                }

                sep = s;
            }
        }

        var start = run.Groups[first].Start;
        var end = run.Groups[last].Start + run.Groups[last].Length;
        var number = digits.ToString();

        if (Wants(PiiType.Card) && number.Length is >= 13 and <= 19 && IssuerOk(number) && GroupsOk(lengths)
            && PiiChecks.Luhn(number) && !AllSame(number) && !(Near(text, start, NotCardWords) && !Near(text, start, CardWords)))
        {
            found.Add(new PiiFinding(PiiType.Card, start, end - start));
            return true;
        }

        if (Wants(PiiType.Aadhaar) && number.Length == 12 && number[0] >= '2' && PiiChecks.Verhoeff(number) && !AllSame(number))
        {
            var grouped = lengths.Count == 3 && lengths.All(l => l == 4);
            var single = lengths.Count == 1;
            if ((grouped && !(Near(text, start, NotCardWords) && !Near(text, start, AadhaarWords)))
                || (single && Near(text, start, AadhaarWords)))
            {
                found.Add(new PiiFinding(PiiType.Aadhaar, start, end - start));
                return true;
            }
        }

        return false;
    }

    // digit groups joined by one space or dash
    private static IEnumerable<Run> Runs(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (!IsDigit(text[i]) || (i > 0 && IsDigit(text[i - 1])))
            {
                i++;
                continue;
            }

            var groups = new List<Group>();
            var separators = new List<char>();
            var p = i;
            while (true)
            {
                var s = p;
                while (p < text.Length && IsDigit(text[p]))
                {
                    p++;
                }

                groups.Add(new Group(s, p - s));
                if (groups.Count < 8 && p + 1 < text.Length && (text[p] == ' ' || text[p] == '-') && IsDigit(text[p + 1]))
                {
                    separators.Add(text[p]);
                    p++;
                    continue;
                }

                break;
            }

            yield return new Run(groups, separators);
            i = p;
        }
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private static bool StartBoundary(string text, int start) =>
        start == 0 || char.IsWhiteSpace(text[start - 1]) || "([{\"'<:;,=".Contains(text[start - 1]);

    private static bool EndBoundary(string text, int end)
    {
        if (end >= text.Length)
        {
            return true;
        }

        var c = text[end];
        if (c == '.')
        {
            return end + 1 >= text.Length || !IsDigit(text[end + 1]);
        }

        return char.IsWhiteSpace(c) || ")]}\"'>:;,!?".Contains(c);
    }

    private static bool AllSame(string digits) => digits.All(c => c == digits[0]);

    private static bool Near(string text, int start, string[] words)
    {
        var from = Math.Max(0, start - 40);
        var before = text.Substring(from, start - from).ToLowerInvariant();
        foreach (var w in words)
        {
            if (before.Contains(w, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool GroupsOk(List<int> lengths)
    {
        if (lengths.Count == 1)
        {
            return true;
        }

        if (lengths.Count == 3 && lengths[0] == 4 && ((lengths[1] == 6 && lengths[2] is 4 or 5)))
        {
            return true;
        }

        for (var i = 0; i < lengths.Count - 1; i++)
        {
            if (lengths[i] != 4)
            {
                return false;
            }
        }

        return lengths[^1] is >= 1 and <= 4;
    }

    // Does the number start like a card of a known network, and is it the length that network uses?
    private static bool IssuerOk(string n)
    {
        var len = n.Length;
        int Prefix(int digits) => int.Parse(n.AsSpan(0, digits), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);

        if (n[0] == '4')
        {
            return len is 13 or 16 or 19;
        }

        var p2 = Prefix(2);
        var p3 = Prefix(3);
        var p4 = Prefix(4);

        if ((p2 is >= 51 and <= 55) || (p4 is >= 2221 and <= 2720))
        {
            return len == 16;
        }

        if (p2 is 34 or 37)
        {
            return len == 15;
        }

        if (p4 == 6011 || p2 == 65 || p3 is >= 644 and <= 649 || (Prefix(6) is >= 622126 and <= 622925))
        {
            return len is >= 16 and <= 19;
        }

        if (p4 is >= 3528 and <= 3589)
        {
            return len is >= 16 and <= 19;
        }

        if (p3 is >= 300 and <= 305 || p4 == 3095 || p2 is 36 or 38 or 39)
        {
            return len is >= 14 and <= 19;
        }

        if (p2 == 62)
        {
            return len is >= 16 and <= 19;
        }

        if (p2 is 60 or 81 or 82 || p3 is 508 or 353 or 356)
        {
            return len == 16;
        }

        return p4 is 5018 or 5020 or 5038 or 6304 or 6759 or 6761 or 6762 or 6763 && len is >= 12 and <= 19;
    }

    // ---- IBAN ----

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){2,7}(?: ?[A-Z0-9]{1,3})?(?![A-Za-z0-9])", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex IbanShape();

    private static void FindIbans(string text, List<PiiFinding> found)
    {
        foreach (Match m in IbanShape().Matches(text))
        {
            var compact = m.Value.Replace(" ", string.Empty, StringComparison.Ordinal);
            if (PiiChecks.Iban(compact))
            {
                found.Add(new PiiFinding(PiiType.Iban, m.Index, m.Length));
            }
        }
    }

    // ---- SSN and PAN ----

    [GeneratedRegex(@"(?<![A-Za-z0-9_\-.])(?<a>\d{3})-(?<g>\d{2})-(?<s>\d{4})(?![A-Za-z0-9_\-]|\.\d)", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SsnShape();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])[A-Z]{3}[ABCFGHJKLPT][A-Z]\d{4}[A-Z](?![A-Za-z0-9_])", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex PanShape();

    // nine digits in a row only count when the text around them talks about a social security number
    [GeneratedRegex(@"(?<![A-Za-z0-9_\-.])\d{9}(?![A-Za-z0-9_\-]|\.\d)", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex BareNine();

    private static readonly string[] SsnWords = { "ssn", "social security" };

    private static bool SsnRules(ReadOnlySpan<char> area, ReadOnlySpan<char> group, ReadOnlySpan<char> serial)
    {
        var a = int.Parse(area, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
        return a is not (0 or 666 or >= 900) && !group.SequenceEqual("00") && !serial.SequenceEqual("0000");
    }

    private static void FindSsn(string text, List<PiiFinding> found)
    {
        foreach (Match m in SsnShape().Matches(text))
        {
            if (SsnRules(m.Groups["a"].ValueSpan, m.Groups["g"].ValueSpan, m.Groups["s"].ValueSpan))
            {
                found.Add(new PiiFinding(PiiType.Ssn, m.Index, m.Length));
            }
        }

        foreach (Match m in BareNine().Matches(text))
        {
            var v = m.ValueSpan;
            if (Near(text, m.Index, SsnWords) && SsnRules(v[..3], v.Slice(3, 2), v[5..]))
            {
                found.Add(new PiiFinding(PiiType.Ssn, m.Index, m.Length, 0.8));
            }
        }
    }

    private static void FindPan(string text, List<PiiFinding> found)
    {
        foreach (Match m in PanShape().Matches(text))
        {
            found.Add(new PiiFinding(PiiType.Pan, m.Index, m.Length));
        }
    }

    // ---- email ----

    [GeneratedRegex(
        @"(?<![A-Za-z0-9._%+\-@])(?<l>[A-Za-z0-9][A-Za-z0-9._%+\-]{0,63})@(?<d>(?:[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?\.){1,5}(?<t>[A-Za-z]{2,24}))(?![A-Za-z0-9@\-]|:[\w.\-]+/)",
        RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex EmailShape();

    // a name like icon@2x.png is a file, not a mailbox
    private static readonly HashSet<string> FileEndings = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "gif", "svg", "webp", "bmp", "ico", "css", "js", "ts", "json", "xml", "html", "htm",
        "md", "txt", "pdf", "zip", "gz", "tar", "exe", "dll", "cs", "py", "java", "yml", "yaml",
    };

    private static void FindEmails(string text, List<PiiFinding> found)
    {
        foreach (Match m in EmailShape().Matches(text))
        {
            var local = m.Groups["l"].Value;
            if (local.Contains("..", StringComparison.Ordinal) || local.EndsWith('.') || FileEndings.Contains(m.Groups["t"].Value))
            {
                continue;
            }

            found.Add(new PiiFinding(PiiType.Email, m.Index, m.Length));
        }
    }

    // ---- phone numbers ----

    // with a plus sign and a country code, 8 to 15 digits in all
    [GeneratedRegex(@"(?<![A-Za-z0-9_+])\+\d{1,3}(?:[ .\-]?\(?\d{2,5}\)?){2,5}(?![A-Za-z0-9_]|[\-.]\d)", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex InternationalPhone();

    // 555-123-4567 and (555) 123-4567, area code and exchange start with 2 to 9
    [GeneratedRegex(@"(?<![A-Za-z0-9_\-.+])(?:\([2-9]\d{2}\)[ .\-]?|[2-9]\d{2}[ .\-])[2-9]\d{2}[ .\-]\d{4}(?![A-Za-z0-9_\-]|\.\d)", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex NanpPhone();

    // Indian mobile numbers start with 6 to 9. Written 98765 43210, or as 10 digits when the text says it is a phone.
    [GeneratedRegex(@"(?<![A-Za-z0-9_\-.+])(?<p>(?:91[ \-]|0)?)(?<n>[6-9]\d{4}(?<s>[ \-]?)\d{5})(?![A-Za-z0-9_\-]|\.\d)", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex IndianMobile();

    private static readonly string[] PhoneWords = { "phone", "mobile", "mob", "tel", "call", "contact", "whatsapp", "cell", "ph:", "ph." };

    private static void FindPhones(string text, List<PiiFinding> found)
    {
        var phones = new List<PiiFinding>();
        FindPhoneShapes(text, phones);

        // one number can fit two shapes, +1 415 555 2671 is also a NANP number. Keep the widest.
        foreach (var p in phones)
        {
            if (!phones.Any(o => !ReferenceEquals(o, p) && o.Start <= p.Start && o.End >= p.End && (o.Length > p.Length || o.Start < p.Start || phones.IndexOf(o) < phones.IndexOf(p))))
            {
                found.Add(p);
            }
        }
    }

    private static void FindPhoneShapes(string text, List<PiiFinding> found)
    {
        foreach (Match m in InternationalPhone().Matches(text))
        {
            var digits = m.Value.Count(IsDigit);
            if (digits is >= 8 and <= 15)
            {
                found.Add(new PiiFinding(PiiType.Phone, m.Index, m.Length, 0.9));
            }
        }

        foreach (Match m in NanpPhone().Matches(text))
        {
            found.Add(new PiiFinding(PiiType.Phone, m.Index, m.Length, 0.9));
        }

        foreach (Match m in IndianMobile().Matches(text))
        {
            var spaced = m.Groups["s"].Length > 0;
            var prefixed = m.Groups["p"].Length > 0;
            if (spaced || (Near(text, m.Index, PhoneWords) && (prefixed || m.Groups["n"].Length == 10)))
            {
                found.Add(new PiiFinding(PiiType.Phone, m.Index, m.Length, 0.9));
            }
        }
    }
}
