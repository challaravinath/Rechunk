using System.Diagnostics;
using System.Globalization;

namespace Rechunk.RealData;

public readonly record struct CorpusChunk(string Path, string Text);

public readonly record struct NearHit(int Index, int Canonical, MatchStage Stage, int? Bits, double? Jaccard);

public sealed record ExactNote(int Short, int Exact, IReadOnlyList<(int Count, string Preview)> Top);

public sealed record RunStats(int Chunks, int Exact, int SimHash, int MinHash, int Unique, long Milliseconds)
{
    public int Saved => Exact + SimHash + MinHash;

    public double SavedPercent => Chunks == 0 ? 0 : 100.0 * Saved / Chunks;

    public double ChunksPerSecond => Milliseconds <= 0 ? 0 : Chunks * 1000.0 / Milliseconds;
}

/// <summary>
/// Reads markdown from a folder and runs it through the three dedup stages.
/// The dataset stays on disk. Pass the folder when you run it.
/// </summary>
public static class Program
{
    // Default, and one step to either side. Three values per stage.
    private static readonly int[] SimHashDistances = [2, 3, 4];
    private static readonly double[] MinHashThresholds = [0.80, 0.85, 0.90];

    public static async Task<int> Main(string[] args)
    {
        string? folder = null;
        var sample = 0;
        uint seed = 2026;
        var sweep = false;
        var simHash = 3;
        var minHash = 0.85;
        var pairs = 0;
        uint pairSeed = 2026;
        string? labeled = null;
        var limit = 8000;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--sample":
                    sample = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--seed":
                    seed = uint.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--sweep":
                    sweep = true;
                    break;
                case "--simhash":
                    simHash = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--minhash":
                    minHash = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--pairs":
                    pairs = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--pairs-seed":
                    pairSeed = uint.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--labeled":
                    labeled = args[++i];
                    break;
                case "--limit":
                    limit = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--help":
                case "-h":
                    Usage();
                    return 0;
                default:
                    if (args[i].StartsWith('-') || folder is not null)
                    {
                        Console.Error.WriteLine("Unknown argument: " + args[i]);
                        Usage();
                        return 1;
                    }

                    folder = args[i];
                    break;
            }
        }

        if (labeled is not null)
        {
            if (folder is not null || sweep || sample != 0 || pairs != 0 || seed != 2026 || pairSeed != 2026
                || simHash != 3 || minHash != 0.85)
            {
                Console.Error.WriteLine("--labeled runs its own four settings. Do not combine it with a folder or the other switches.");
                Usage();
                return 1;
            }

            if (limit < 1)
            {
                Console.Error.WriteLine("--limit has to be at least 1.");
                return 1;
            }

            return await LabeledPairs.RunAsync(labeled, limit);
        }

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            Console.Error.WriteLine("Give a folder of markdown files.");
            Usage();
            return 1;
        }

        var corpus = MarkdownCorpus.Load(folder, sample, seed);
        var chunks = corpus.Chunks;
        Console.WriteLine($"markdown files on disk: {corpus.FilesOnDisk}");
        Console.WriteLine($"files used: {corpus.FilesUsed}" + (sample > 0 ? $" (sample {sample}, seed {seed})" : ""));
        Console.WriteLine($"chunks: {chunks.Count}");
        Console.WriteLine();

        if (sweep)
        {
            Console.WriteLine("SimHash distance with MinHash held at 0.85, then MinHash threshold with SimHash held at 3.");
            Console.WriteLine("MinHash confirms SimHash, which is the default.");
            Console.WriteLine();
            Console.WriteLine("| simhash | minhash | exact | simhash hits | minhash hits | saved | saved % | ms | chunks/s |");
            Console.WriteLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");

            var seen = new HashSet<string>();
            ExactNote? exactNote = null;
            foreach (var distance in SimHashDistances)
            {
                exactNote = await PrintRow(chunks, distance, 0.85, seen) ?? exactNote;
            }

            foreach (var threshold in MinHashThresholds)
            {
                exactNote = await PrintRow(chunks, 3, threshold, seen) ?? exactNote;
            }

            Console.WriteLine();
            PrintExact(exactNote);
            return 0;
        }

        var hits = pairs > 0 ? new List<NearHit>() : null;
        var result = await MeasureAsync(chunks, simHash, minHash, hits, countExactText: true);
        PrintStatsLine(simHash, minHash, result.Stats);
        PrintExact(result.Exact);
        if (hits is not null)
        {
            Console.WriteLine();
            PrintPairs(chunks, hits, pairs, pairSeed);
        }

        return 0;
    }

    private static async Task<ExactNote?> PrintRow(
        IReadOnlyList<CorpusChunk> chunks, int distance, double threshold, HashSet<string> seen)
    {
        var key = distance.ToString(CultureInfo.InvariantCulture) + "|" + threshold.ToString("F2", CultureInfo.InvariantCulture);
        if (!seen.Add(key))
        {
            return null;
        }

        var countExact = distance == 3 && threshold == 0.85;
        var result = await MeasureAsync(chunks, distance, threshold, nearHits: null, countExact);
        var stats = result.Stats;
        Console.WriteLine(
            $"| {distance} | {threshold.ToString("F2", CultureInfo.InvariantCulture)} | {stats.Exact} | {stats.SimHash} | {stats.MinHash} | {stats.Saved} | {stats.SavedPercent.ToString("F1", CultureInfo.InvariantCulture)}% | {stats.Milliseconds} | {stats.ChunksPerSecond.ToString("F0", CultureInfo.InvariantCulture)} |");
        return result.Exact;
    }

    public static async Task<(RunStats Stats, ExactNote? Exact)> MeasureAsync(
        IReadOnlyList<CorpusChunk> chunks,
        int hammingDistance,
        double threshold,
        List<NearHit>? nearHits,
        bool countExactText)
    {
        var pipeline = RechunkPipeline.CreateBuilder()
            .UseSimHash(o => o.MaxHammingDistance = hammingDistance)
            .UseMinHash(o => o.Threshold = threshold)
            .Build();

        var idToIndex = new Dictionary<string, int>(chunks.Count);
        var exactText = countExactText ? new Dictionary<string, int>(StringComparer.Ordinal) : null;
        var shortExact = 0;
        int exact = 0, sim = 0, min = 0, unique = 0;

        var stopwatch = Stopwatch.StartNew();
        for (var start = 0; start < chunks.Count; start += 500)
        {
            var count = Math.Min(500, chunks.Count - start);
            var batch = new List<ChunkInput>(count);
            for (var i = 0; i < count; i++)
            {
                batch.Add(new ChunkInput(start + i, chunks[start + i].Text));
            }

            var report = await pipeline.DeduplicateAsync("docs", batch);
            foreach (var decision in report.Decisions)
            {
                if (decision.Outcome == DedupOutcome.Unique)
                {
                    unique++;
                    idToIndex[decision.Id.Value] = decision.Input.Ordinal;
                    continue;
                }

                if (decision.Outcome != DedupOutcome.Duplicate)
                {
                    continue;
                }

                switch (decision.Stage)
                {
                    case MatchStage.Exact:
                        exact++;
                        if (decision.Input.Text.Length < 40)
                        {
                            shortExact++;
                        }

                        if (exactText is not null)
                        {
                            exactText.TryGetValue(decision.Input.Text, out var n);
                            exactText[decision.Input.Text] = n + 1;
                        }

                        break;
                    case MatchStage.SimHash:
                        sim++;
                        Remember(nearHits, idToIndex, decision);
                        break;
                    case MatchStage.MinHash:
                        min++;
                        Remember(nearHits, idToIndex, decision);
                        break;
                }
            }
        }

        stopwatch.Stop();

        ExactNote? note = null;
        if (exactText is not null)
        {
            var top = exactText
                .OrderByDescending(p => p.Value)
                .ThenBy(p => p.Key, StringComparer.Ordinal)
                .Take(8)
                .Select(p =>
                {
                    var preview = p.Key.Replace('\n', ' ');
                    return (p.Value, preview.Length > 70 ? preview[..70] : preview);
                })
                .ToList();
            note = new ExactNote(shortExact, exact, top);
        }

        return (new RunStats(chunks.Count, exact, sim, min, unique, stopwatch.ElapsedMilliseconds), note);
    }

    private static void PrintExact(ExactNote? note)
    {
        if (note is null)
        {
            return;
        }

        Console.WriteLine($"exact hits shorter than 40 characters: {note.Short} of {note.Exact}");
        foreach (var (count, preview) in note.Top)
        {
            Console.WriteLine($"  exact x{count}: {preview}");
        }
    }

    private static void Remember(List<NearHit>? nearHits, Dictionary<string, int> idToIndex, DedupDecision decision)
    {
        if (nearHits is null || decision.DuplicateOf is not { } of)
        {
            return;
        }

        if (!idToIndex.TryGetValue(of.Value, out var canonical))
        {
            return;
        }

        nearHits.Add(new NearHit(decision.Input.Ordinal, canonical, decision.Stage ?? MatchStage.MinHash, decision.HammingDistance, decision.EstimatedJaccard));
    }

    private static void PrintPairs(IReadOnlyList<CorpusChunk> chunks, List<NearHit> hits, int take, uint seed)
    {
        Console.WriteLine($"near duplicate pairs: {hits.Count}, showing {Math.Min(take, hits.Count)} (seed {seed})");
        if (hits.Count == 0 || take <= 0)
        {
            return;
        }

        var picked = hits.ToList();
        var state = seed == 0 ? 1u : seed;
        var n = Math.Min(take, picked.Count);
        for (var i = 0; i < n; i++)
        {
            var j = i + Next(ref state, picked.Count - i);
            (picked[i], picked[j]) = (picked[j], picked[i]);
        }

        for (var i = 0; i < n; i++)
        {
            var hit = picked[i];
            var left = chunks[hit.Canonical];
            var right = chunks[hit.Index];
            Console.WriteLine($"PAIR {i + 1}");
            Console.WriteLine($"stage: {hit.Stage}");
            Console.WriteLine($"bits: {(hit.Bits is { } b ? b.ToString(CultureInfo.InvariantCulture) : "-")}");
            Console.WriteLine($"jaccard: {(hit.Jaccard is { } j ? j.ToString("F3", CultureInfo.InvariantCulture) : "-")}");
            Console.WriteLine("left: " + left.Path);
            Console.WriteLine("right: " + right.Path);
            Console.WriteLine("--- left");
            Console.WriteLine(Show(left.Text));
            Console.WriteLine("--- right");
            Console.WriteLine(Show(right.Text));
            Console.WriteLine();
        }
    }

    private static string Show(string text)
    {
        if (text.Length <= 700)
        {
            return text;
        }

        return text[..450] + "\n[...]\n" + text[^200..];
    }

    private static void PrintStatsLine(int distance, double threshold, RunStats stats)
    {
        Console.WriteLine(
            $"simhash {distance}, minhash {threshold.ToString("F2", CultureInfo.InvariantCulture)}: " +
            $"exact {stats.Exact}, simhash {stats.SimHash}, minhash {stats.MinHash}, " +
            $"saved {stats.Saved} ({stats.SavedPercent.ToString("F1", CultureInfo.InvariantCulture)}%), " +
            $"unique {stats.Unique}, {stats.Milliseconds} ms, {stats.ChunksPerSecond.ToString("F0", CultureInfo.InvariantCulture)} chunks/s");
    }

    // Fixed generator so a seed picks the same files on later runs.
    internal static int Next(ref uint state, int span)
    {
        state = state * 1664525u + 1013904223u;
        return (int)(state % (uint)span);
    }

    private static void Usage()
    {
        Console.WriteLine("Usage: Rechunk.RealData <folder> [--sample N] [--seed N] [--sweep]");
        Console.WriteLine("       [--simhash K] [--minhash T] [--pairs N] [--pairs-seed N]");
        Console.WriteLine("       --labeled <pairs.tsv> [--limit N]");
        Console.WriteLine("Sweep runs SimHash 2, 3 and 4 at MinHash 0.85, then MinHash 0.80, 0.85 and 0.90 at SimHash 3.");
        Console.WriteLine("--labeled reads id, sentence1, sentence2, label and keeps at most --limit pairs (8000).");
    }
}

public sealed record LoadedCorpus(List<CorpusChunk> Chunks, int FilesOnDisk, int FilesUsed);

public static class MarkdownCorpus
{
    public static LoadedCorpus Load(string folder, int sample, uint seed)
    {
        var root = Path.GetFullPath(folder);
        var files = Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        var onDisk = files.Count;

        if (sample > 0 && sample < files.Count)
        {
            var state = seed == 0 ? 1u : seed;
            for (var i = 0; i < sample; i++)
            {
                var j = i + Program.Next(ref state, files.Count - i);
                (files[i], files[j]) = (files[j], files[i]);
            }

            files.RemoveRange(sample, files.Count - sample);
            files.Sort(StringComparer.Ordinal);
        }

        var chunks = new List<CorpusChunk>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            foreach (var chunk in MarkdownChunker.Chunk(File.ReadAllText(file)))
            {
                chunks.Add(new CorpusChunk(relative, chunk));
            }
        }

        return new LoadedCorpus(chunks, onDisk, files.Count);
    }
}
