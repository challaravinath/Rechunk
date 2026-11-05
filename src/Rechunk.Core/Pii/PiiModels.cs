namespace Rechunk;

/// <summary>
/// The kind of personal data or secret a detector found. It is a name, so a detector of your own (or an adapter for a
/// service) can bring types that are not listed here. The built in detector uses the ones below.
/// </summary>
public readonly record struct PiiType(string Name)
{
    public static readonly PiiType Card = new("card");

    public static readonly PiiType ApiKey = new("api_key");

    public static readonly PiiType Email = new("email");

    public static readonly PiiType Phone = new("phone");

    public static readonly PiiType Aadhaar = new("aadhaar");

    public static readonly PiiType Pan = new("pan");

    public static readonly PiiType Ssn = new("ssn");

    public static readonly PiiType Iban = new("iban");

    public override string ToString() => Name ?? string.Empty;
}

/// <summary>What to do with a chunk that has a finding of a type. When a chunk has findings with different policies the strictest one wins.</summary>
public enum PiiPolicy
{
    /// <summary>Report it and carry on. The text is stored as it is. This is the default.</summary>
    Flag = 0,

    /// <summary>Replace the found text with a placeholder before the chunk is fingerprinted, embedded or stored.</summary>
    Mask = 1,

    /// <summary>The chunk is not fingerprinted, embedded or stored at all.</summary>
    Block = 2,
}

/// <summary>
/// What a detector found. Start and Length count characters (UTF-16 code units) in the text it was given. The value
/// itself is never part of a finding, on purpose, so a finding can be logged and audited without leaking what it points at.
/// </summary>
public sealed record PiiFinding(PiiType Type, int Start, int Length, double Confidence = 1.0)
{
    public int End => Start + Length;
}

/// <summary>A finding after the policy was applied. This is what ends up in decisions and in the audit log.</summary>
public sealed record PiiHit(PiiType Type, int Start, int Length, PiiPolicy Applied);

/// <summary>One text for a detector, with the language of its chunk when it is known.</summary>
public sealed record PiiRequest(string Text, string? Language);

/// <summary>
/// Looks for personal data and secrets in text. The built in detector covers cards, keys, emails, phone numbers and a few
/// national ids. To use a service such as Microsoft Presidio or Azure AI Language, wrap its client in a
/// <see cref="PiiServiceDetector"/>. A detector has to return spans only, never the text it found.
/// </summary>
public interface IPiiDetector
{
    ValueTask<IReadOnlyList<PiiFinding>> DetectAsync(string text, string? language, CancellationToken ct = default);

    /// <summary>
    /// Looks at many texts at once. The scanner calls this with the whole batch, so a detector that talks to a service can
    /// send one request. The result has one list per request, in the same order. The default asks one by one.
    /// </summary>
    async ValueTask<IReadOnlyList<IReadOnlyList<PiiFinding>>> DetectManyAsync(
        IReadOnlyList<PiiRequest> requests, CancellationToken ct = default)
    {
        var all = new List<IReadOnlyList<PiiFinding>>(requests.Count);
        foreach (var request in requests)
        {
            all.Add(await DetectAsync(request.Text, request.Language, ct).ConfigureAwait(false));
        }

        return all;
    }
}

/// <summary>What the scanner decided for one chunk. Kept on the chunk so it is not scanned twice.</summary>
internal sealed record PiiScan(IReadOnlyList<PiiHit> Hits, bool Blocked)
{
    public static readonly PiiScan Clean = new(Array.Empty<PiiHit>(), false);
}
