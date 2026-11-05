namespace Rechunk;

/// <summary>One entity a PII service reported. Start and Length are in the unit the service uses, see <see cref="PiiOffsetUnit"/>.</summary>
public sealed record PiiEntity(string EntityType, int Start, int Length, double Score);

/// <summary>
/// The small interface you implement to use an outside PII service. Wrap the client of Microsoft Presidio or of Azure AI
/// Language in it and hand it to <see cref="PiiServiceDetector"/>. Rechunk does not ship a client and makes no network
/// calls on its own. Return one list per request, in the same order.
/// </summary>
public interface IPiiAnalysisClient
{
    ValueTask<IReadOnlyList<IReadOnlyList<PiiEntity>>> AnalyzeAsync(IReadOnlyList<PiiRequest> requests, CancellationToken ct = default);
}

/// <summary>How a service counts the position of an entity in the text.</summary>
public enum PiiOffsetUnit
{
    /// <summary>UTF-16 code units, which is what .NET strings use. The Azure AI Language .NET client gives these.</summary>
    Utf16,

    /// <summary>Unicode code points. Python based services such as Presidio count like this, which differs from UTF-16 after an emoji or another character outside the basic plane.</summary>
    CodePoints,
}

/// <summary>
/// Turns the answer of a PII service into findings. It maps the service's entity names to <see cref="PiiType"/>, drops
/// low scores and fixes the offsets. Names it does not know keep their own name as the type, so you can give them a
/// policy with <c>options.Pii.Set(new PiiType("PERSON"), PiiPolicy.Mask)</c>.
/// </summary>
public sealed class PiiServiceDetector : IPiiDetector
{
    /// <summary>The names Presidio and Azure AI Language use for the types Rechunk knows.</summary>
    public static IReadOnlyDictionary<string, PiiType> DefaultTypeMap { get; } = new Dictionary<string, PiiType>(StringComparer.OrdinalIgnoreCase)
    {
        ["CREDIT_CARD"] = PiiType.Card,
        ["EMAIL_ADDRESS"] = PiiType.Email,
        ["PHONE_NUMBER"] = PiiType.Phone,
        ["IBAN_CODE"] = PiiType.Iban,
        ["US_SSN"] = PiiType.Ssn,
        ["IN_AADHAAR"] = PiiType.Aadhaar,
        ["IN_PAN"] = PiiType.Pan,
        ["CreditCardNumber"] = PiiType.Card,
        ["Email"] = PiiType.Email,
        ["PhoneNumber"] = PiiType.Phone,
        ["InternationalBankingAccountNumber"] = PiiType.Iban,
        ["USSocialSecurityNumber"] = PiiType.Ssn,
    };

    private readonly IPiiAnalysisClient _client;
    private readonly IReadOnlyDictionary<string, PiiType> _map;
    private readonly double _minScore;
    private readonly PiiOffsetUnit _unit;

    public PiiServiceDetector(
        IPiiAnalysisClient client,
        IReadOnlyDictionary<string, PiiType>? typeMap = null,
        double minScore = 0.5,
        PiiOffsetUnit offsets = PiiOffsetUnit.Utf16)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _map = typeMap ?? DefaultTypeMap;
        _minScore = minScore;
        _unit = offsets;
    }

    public async ValueTask<IReadOnlyList<PiiFinding>> DetectAsync(string text, string? language, CancellationToken ct = default)
    {
        var all = await DetectManyAsync(new[] { new PiiRequest(text, language) }, ct).ConfigureAwait(false);
        return all[0];
    }

    public async ValueTask<IReadOnlyList<IReadOnlyList<PiiFinding>>> DetectManyAsync(
        IReadOnlyList<PiiRequest> requests, CancellationToken ct = default)
    {
        var answers = await _client.AnalyzeAsync(requests, ct).ConfigureAwait(false);
        if (answers.Count != requests.Count)
        {
            throw new InvalidOperationException("The PII client returned " + answers.Count + " results for " + requests.Count + " texts.");
        }

        var result = new List<IReadOnlyList<PiiFinding>>(requests.Count);
        for (var i = 0; i < requests.Count; i++)
        {
            var findings = new List<PiiFinding>();
            foreach (var entity in answers[i])
            {
                if (entity.Score < _minScore || entity.Length <= 0 || entity.Start < 0)
                {
                    continue;
                }

                var type = _map.TryGetValue(entity.EntityType, out var mapped) ? mapped : new PiiType(entity.EntityType);
                var (start, length) = _unit == PiiOffsetUnit.CodePoints
                    ? ToUtf16(requests[i].Text, entity.Start, entity.Length)
                    : (entity.Start, entity.Length);
                if (start >= 0 && start + length <= requests[i].Text.Length && length > 0)
                {
                    findings.Add(new PiiFinding(type, start, length, entity.Score));
                }
            }

            result.Add(findings);
        }

        return result;
    }

    // code point positions to UTF-16 positions, -1 when the entity runs past the end of the text
    internal static (int Start, int Length) ToUtf16(string text, int start, int length)
    {
        int Convert(int codePoints)
        {
            var units = 0;
            for (var n = 0; n < codePoints; n++)
            {
                if (units >= text.Length)
                {
                    return -1;
                }

                units += char.IsHighSurrogate(text[units]) && units + 1 < text.Length ? 2 : 1;
            }

            return units;
        }

        var from = Convert(start);
        var to = Convert(start + length);
        return from < 0 || to < 0 ? (-1, 0) : (from, to - from);
    }
}

/// <summary>Runs several detectors and keeps everything they find. Overlaps are sorted out by the scanner. Use it to add a service next to the built in rules.</summary>
public sealed class CompositePiiDetector : IPiiDetector
{
    private readonly IPiiDetector[] _detectors;

    public CompositePiiDetector(params IPiiDetector[] detectors)
    {
        ArgumentNullException.ThrowIfNull(detectors);
        if (detectors.Length == 0)
        {
            throw new ArgumentException("Give at least one detector.", nameof(detectors));
        }

        _detectors = detectors;
    }

    public async ValueTask<IReadOnlyList<PiiFinding>> DetectAsync(string text, string? language, CancellationToken ct = default)
    {
        var all = new List<PiiFinding>();
        foreach (var detector in _detectors)
        {
            all.AddRange(await detector.DetectAsync(text, language, ct).ConfigureAwait(false));
        }

        return all;
    }

    public async ValueTask<IReadOnlyList<IReadOnlyList<PiiFinding>>> DetectManyAsync(
        IReadOnlyList<PiiRequest> requests, CancellationToken ct = default)
    {
        var merged = requests.Select(_ => new List<PiiFinding>()).ToList();
        foreach (var detector in _detectors)
        {
            var part = await detector.DetectManyAsync(requests, ct).ConfigureAwait(false);
            for (var i = 0; i < requests.Count; i++)
            {
                merged[i].AddRange(part[i]);
            }
        }

        return merged;
    }
}
