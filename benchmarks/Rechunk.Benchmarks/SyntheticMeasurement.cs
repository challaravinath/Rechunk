using System.Diagnostics;

namespace Rechunk.Benchmarks;

/// <summary>
/// A small measurement on made up text, not a claim about real documents. It builds a corpus of pseudo word chunks,
/// plants duplicates of known kinds, runs the pipeline in several settings and counts what each stage saved and what
/// it got right. The corpus is the same on every run (fixed seed), so the numbers can be reproduced.
///
/// Planted kinds, one variant per chosen base chunk, always placed after its original:
///   exact:    the same text with extra whitespace
///   tiny:     one word replaced
///   moderate: about 2 percent of the words replaced
///   loose:    about 5 percent of the words replaced (Jaccard around 0.75, below the default threshold)
///   guarded:  a number changed, or "not" put in. Close in wording, different in meaning. Must stay unique.
///
/// A planted variant counts as one that should be merged when its exact Jaccard similarity to the original
/// (word triples) is at least the MinHash threshold, and the guard does not apply.
/// </summary>
public static class SyntheticMeasurement
{
    private const int BaseChunks = 4000;
    private const int PerKind = 300;
    private const double Threshold = 0.85;

    private enum Kind { Original, Exact, Tiny, Moderate, Loose, Guarded }

    private sealed record Item(string Text, int Family, Kind Kind, bool ShouldMerge, double Jaccard);

    private sealed record Setting(string Name, Action<RechunkPipelineBuilder> Configure);

    public static async Task RunAsync(TextWriter output)
    {
        var items = BuildCorpus(out var vocabularySize);
        var variants = items.Where(i => i.Kind != Kind.Original).ToList();

        output.WriteLine("Synthetic corpus (made up words, not real documents)");
        output.WriteLine($"  chunks: {items.Count} ({BaseChunks} originals, {variants.Count} planted variants), vocabulary {vocabularySize} words");
        foreach (var kind in new[] { Kind.Exact, Kind.Tiny, Kind.Moderate, Kind.Loose, Kind.Guarded })
        {
            var ofKind = variants.Where(v => v.Kind == kind).ToList();
            var meanJaccard = ofKind.Average(v => v.Jaccard);
            output.WriteLine($"  {kind,-9} {ofKind.Count,4} variants, mean Jaccard to original {meanJaccard:F2}, should merge: {ofKind.Count(v => v.ShouldMerge)}");
        }

        var shouldMerge = variants.Count(v => v.ShouldMerge);
        output.WriteLine($"  planted duplicates that should be merged: {shouldMerge}");
        output.WriteLine();

        var settings = new[]
        {
            new Setting("exact only", b => { }),
            new Setting("exact + SimHash (k=3)", b => b.UseSimHash()),
            new Setting("exact + SimHash (k=6)", b => b.UseSimHash(o => o.MaxHammingDistance = 6)),
            new Setting("exact + MinHash", b => b.UseMinHash()),
            new Setting("all three, SimHash trusted", b => b.UseSimHash(o => o.ConfirmWithMinHash = false).UseMinHash()),
            new Setting("all three, MinHash confirms SimHash", b => b.UseSimHash().UseMinHash()),
        };

        output.WriteLine("| setting | embedding calls | saved | exact / SimHash / MinHash | precision | recall | wrong merges | ms | us per chunk |");
        output.WriteLine("|---|---|---|---|---|---|---|---|---|");

        var details = new List<(string Name, Dictionary<Kind, int> Caught)>();
        foreach (var setting in settings)
        {
            var builder = RechunkPipeline.CreateBuilder();
            setting.Configure(builder);
            var pipeline = builder.Build();

            var stopwatch = Stopwatch.StartNew();
            var decisions = new List<DedupDecision>(items.Count);
            for (var start = 0; start < items.Count; start += 500)
            {
                var batch = items.Skip(start).Take(500).Select((item, i) => new ChunkInput(start + i, item.Text)).ToList();
                decisions.AddRange((await pipeline.DeduplicateAsync("synthetic", batch)).Decisions);
            }

            stopwatch.Stop();

            var familyOf = new Dictionary<string, int>();
            for (var i = 0; i < items.Count; i++)
            {
                familyOf.TryAdd(decisions[i].Id.Value, items[i].Family);
            }

            int correct = 0, wrong = 0, byExact = 0, bySimHash = 0, byMinHash = 0;
            var caught = Enum.GetValues<Kind>().ToDictionary(k => k, _ => 0);
            for (var i = 0; i < items.Count; i++)
            {
                var d = decisions[i];
                if (d.Outcome != DedupOutcome.Duplicate)
                {
                    continue;
                }

                switch (d.Stage)
                {
                    case MatchStage.Exact: byExact++; break;
                    case MatchStage.SimHash: bySimHash++; break;
                    default: byMinHash++; break;
                }

                var sameFamily = familyOf[d.DuplicateOf!.Value.Value] == items[i].Family;
                if (items[i].Kind != Kind.Original && items[i].ShouldMerge && sameFamily)
                {
                    correct++;
                }
                else
                {
                    wrong++;
                }

                caught[items[i].Kind]++;
            }

            var skipped = correct + wrong;
            var unique = items.Count - skipped;
            var precision = skipped == 0 ? double.NaN : (double)correct / skipped;
            var recall = (double)correct / shouldMerge;
            var micros = stopwatch.Elapsed.TotalMilliseconds * 1000 / items.Count;
            output.WriteLine(
                $"| {setting.Name} | {unique} | {skipped} | {byExact} / {bySimHash} / {byMinHash} | " +
                $"{(double.IsNaN(precision) ? "n/a" : precision.ToString("P1"))} | {recall:P1} | {wrong} | {stopwatch.ElapsedMilliseconds} | {micros:F0} |");
            details.Add((setting.Name, caught));
        }

        output.WriteLine();
        output.WriteLine("Skipped chunks by planted kind (how many of the variants of that kind each setting skipped)");
        output.WriteLine();
        output.WriteLine("| setting | exact | tiny | moderate | loose | guarded | originals skipped |");
        output.WriteLine("|---|---|---|---|---|---|---|");
        foreach (var (name, caught) in details)
        {
            output.WriteLine(
                $"| {name} | {caught[Kind.Exact]} | {caught[Kind.Tiny]} | {caught[Kind.Moderate]} | {caught[Kind.Loose]} | {caught[Kind.Guarded]} | {caught[Kind.Original]} |");
        }

        output.WriteLine();
        output.WriteLine($"Each kind has {PerKind} variants. Precision counts a skip as right only when the chunk is a planted duplicate that should merge");
        output.WriteLine("and it was matched to its own original. Loose variants are below the threshold, so skipping them counts as a wrong merge.");
    }

    private static List<Item> BuildCorpus(out int vocabularySize)
    {
        var random = new Random(2026);
        var syllables = new[]
        {
            "ba", "ko", "ri", "lu", "me", "sa", "to", "ne", "vi", "do", "pa", "re", "ga", "shi", "mo", "ta",
            "ke", "ru", "ni", "so", "fa", "li", "mu", "de", "ha", "yo", "ze", "bu", "ci", "wa", "xe", "jo",
        };

        var vocabulary = new HashSet<string>();
        while (vocabulary.Count < 6000)
        {
            vocabulary.Add(string.Concat(Enumerable.Range(0, random.Next(2, 5)).Select(_ => syllables[random.Next(syllables.Length)])));
        }

        var words = vocabulary.ToArray();
        vocabularySize = words.Length;

        // common words are used more than rare ones, a little like real text
        string Pick() => words[(int)(words.Length * Math.Pow(random.NextDouble(), 2))];

        var originals = new List<string[]>();
        for (var i = 0; i < BaseChunks; i++)
        {
            originals.Add(Enumerable.Range(0, random.Next(60, 151)).Select(_ => Pick()).ToArray());
        }

        // which originals get which kind of variant, 300 each, no original gets two
        var order = Enumerable.Range(0, BaseChunks).OrderBy(_ => random.Next()).ToArray();
        var kinds = new[] { Kind.Exact, Kind.Tiny, Kind.Moderate, Kind.Loose, Kind.Guarded };
        var kindOf = new Dictionary<int, Kind>();
        for (var k = 0; k < kinds.Length; k++)
        {
            foreach (var family in order.Skip(k * PerKind).Take(PerKind))
            {
                kindOf[family] = kinds[k];
            }
        }

        // a guarded family has a number in its original, or the original gets a "not" put into the variant
        foreach (var (family, kind) in kindOf.Where(p => p.Value == Kind.Guarded).ToList())
        {
            if (family % 2 == 0)
            {
                originals[family][random.Next(originals[family].Length)] = "30";
            }
        }

        var keyed = new List<(double Key, Item Item)>();
        for (var family = 0; family < BaseChunks; family++)
        {
            var original = originals[family];
            var originalKey = random.NextDouble();
            keyed.Add((originalKey, new Item(string.Join(' ', original), family, Kind.Original, false, 1.0)));

            if (!kindOf.TryGetValue(family, out var kind))
            {
                continue;
            }

            string[] variant;
            switch (kind)
            {
                case Kind.Exact:
                    variant = original;
                    break;
                case Kind.Tiny:
                    variant = Replace(original, 1, random, Pick);
                    break;
                case Kind.Moderate:
                    variant = Replace(original, Math.Max(1, (int)Math.Round(original.Length * 0.02)), random, Pick);
                    break;
                case Kind.Loose:
                    variant = Replace(original, Math.Max(2, (int)Math.Round(original.Length * 0.05)), random, Pick);
                    break;
                default:
                    var position = Array.IndexOf(original, "30");
                    variant = position >= 0
                        ? original.Select((w, i) => i == position ? "45" : w).ToArray()
                        : original.Take(40).Append("not").Concat(original.Skip(40)).ToArray();
                    break;
            }

            var text = kind == Kind.Exact ? "  " + string.Join("  ", variant) + "\n" : string.Join(' ', variant);
            var jaccard = ExactJaccard(original, variant, 3);
            var shouldMerge = kind != Kind.Guarded && jaccard >= Threshold;
            var variantKey = originalKey + (1 - originalKey) * random.NextDouble();
            keyed.Add((variantKey, new Item(text, family, kind, shouldMerge, jaccard)));
        }

        return keyed.OrderBy(k => k.Key).Select(k => k.Item).ToList();
    }

    private static string[] Replace(string[] original, int count, Random random, Func<string> pick)
    {
        var copy = (string[])original.Clone();
        foreach (var position in Enumerable.Range(0, copy.Length).OrderBy(_ => random.Next()).Take(count))
        {
            string replacement;
            do
            {
                replacement = pick();
            }
            while (replacement == copy[position]);

            copy[position] = replacement;
        }

        return copy;
    }

    private static double ExactJaccard(string[] a, string[] b, int shingle)
    {
        static HashSet<string> Shingles(string[] w, int n) =>
            Enumerable.Range(0, Math.Max(1, w.Length - n + 1)).Select(i => string.Join(' ', w.Skip(i).Take(n))).ToHashSet();

        var sa = Shingles(a, shingle);
        var sb = Shingles(b, shingle);
        return (double)sa.Intersect(sb).Count() / sa.Union(sb).Count();
    }
}
