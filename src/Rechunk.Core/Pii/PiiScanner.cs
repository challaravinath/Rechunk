using System.Text;

namespace Rechunk;

/// <summary>Runs the detector over a batch of chunks, applies the policies and masks the text. Used by the pipeline.</summary>
internal sealed class PiiScanner
{
    private static readonly PiiType[] Priority =
    {
        PiiType.ApiKey, PiiType.Card, PiiType.Iban, PiiType.Aadhaar, PiiType.Ssn, PiiType.Pan, PiiType.Email, PiiType.Phone,
    };

    private readonly PiiOptions _options;
    private readonly IPiiDetector _detector;

    public PiiScanner(PiiOptions options)
    {
        _options = options;
        _detector = options.Detector ?? new BuiltInPiiDetector(Priority.Where(t => !options.IsIgnored(t)));
    }

    public bool Enabled => _options.Enabled;

    /// <summary>
    /// Returns the chunks with their text masked where the policy says so. Every returned chunk carries the scan result, so a
    /// chunk that goes through the pipeline twice is only scanned once. A chunk with a Block finding comes back with all
    /// its findings masked, so what is left of it is safe to hold in memory, but it must not be stored.
    /// </summary>
    public async ValueTask<IReadOnlyList<ChunkInput>> ScanAsync(
        IReadOnlyList<ChunkInput> chunks, CancellationToken ct)
    {
        var todo = new List<int>();
        for (var i = 0; i < chunks.Count; i++)
        {
            if (chunks[i].Scan is null)
            {
                todo.Add(i);
            }
        }

        if (todo.Count == 0)
        {
            return chunks;
        }

        var requests = todo.Select(i => new PiiRequest(chunks[i].Text, chunks[i].Language)).ToList();
        var found = await _detector.DetectManyAsync(requests, ct).ConfigureAwait(false);
        if (found.Count != requests.Count)
        {
            throw new InvalidOperationException("The PII detector returned " + found.Count + " results for " + requests.Count + " texts.");
        }

        var result = chunks.ToArray();
        for (var k = 0; k < todo.Count; k++)
        {
            var chunk = chunks[todo[k]];
            result[todo[k]] = Apply(chunk, found[k]);
        }

        return result;
    }

    private ChunkInput Apply(ChunkInput chunk, IReadOnlyList<PiiFinding> findings)
    {
        var text = chunk.Text;
        var usable = findings
            .Where(f => f.Start >= 0 && f.Length > 0 && f.End <= text.Length
                && f.Confidence >= _options.MinConfidence && !_options.IsIgnored(f.Type))
            .ToList();
        if (usable.Count == 0)
        {
            return chunk with { Scan = PiiScan.Clean };
        }

        var chosen = Resolve(usable);
        var hits = chosen.Select(f => new PiiHit(f.Type, f.Start, f.Length, _options.PolicyFor(f.Type))).ToList();
        var blocked = hits.Any(h => h.Applied == PiiPolicy.Block);
        var masked = hits.Where(h => blocked || h.Applied == PiiPolicy.Mask).ToList();
        var newText = masked.Count == 0 ? text : Mask(text, masked);
        return chunk with { Text = newText, Scan = new PiiScan(hits, blocked) };
    }

    // overlapping findings: the more specific type wins, then the longer one
    private static List<PiiFinding> Resolve(List<PiiFinding> findings)
    {
        var ordered = findings
            .OrderBy(f => RankOf(f.Type))
            .ThenByDescending(f => f.Length)
            .ThenBy(f => f.Start)
            .ToList();
        var chosen = new List<PiiFinding>();
        foreach (var f in ordered)
        {
            if (!chosen.Any(c => f.Start < c.End && c.Start < f.End))
            {
                chosen.Add(f);
            }
        }

        return chosen.OrderBy(f => f.Start).ToList();
    }

    private static int RankOf(PiiType type)
    {
        var index = Array.IndexOf(Priority, type);
        return index >= 0 ? index : 4;
    }

    private string Mask(string text, List<PiiHit> hits)
    {
        var sb = new StringBuilder(text.Length);
        var at = 0;
        foreach (var hit in hits.OrderBy(h => h.Start))
        {
            sb.Append(text, at, hit.Start - at);
            sb.Append(_options.Placeholder(hit.Type));
            at = hit.Start + hit.Length;
        }

        sb.Append(text, at, text.Length - at);
        return sb.ToString();
    }
}
