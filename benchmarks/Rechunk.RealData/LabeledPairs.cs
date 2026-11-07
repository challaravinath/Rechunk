using System.Diagnostics;
using System.Globalization;

namespace Rechunk.RealData;

/// <summary>
/// Scores labeled sentence pairs. Each pair is its own scope, so the second sentence can only
/// match the first one. Label 1 is a paraphrase. Label 0 is not.
/// </summary>
public static class LabeledPairs
{
    private const int DefaultSimHashDistance = 3;

    public static async Task<int> RunAsync(string path, int limit)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("Pair file not found: " + path);
            return 1;
        }

        var loaded = Load(path, limit);
        var pairs = loaded.Pairs;
        var paraphrases = 0;
        foreach (var pair in pairs)
        {
            if (pair.Label == 1)
            {
                paraphrases++;
            }
        }

        var defaults = new RechunkOptions();
        Console.WriteLine("file: " + path);
        Console.WriteLine("pairs used: " + pairs.Count.ToString(CultureInfo.InvariantCulture) + (loaded.Capped ? " (stopped at the limit)" : ""));
        Console.WriteLine("label 1: " + paraphrases.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("label 0: " + (pairs.Count - paraphrases).ToString(CultureInfo.InvariantCulture));
        Console.WriteLine(
            "defaults: simhash " + defaults.SimHash.MaxHammingDistance.ToString(CultureInfo.InvariantCulture)
            + ", minhash " + defaults.MinHash.Threshold.ToString("F2", CultureInfo.InvariantCulture)
            + ", NearDuplicateMinTokens " + defaults.NearDuplicateMinTokens.ToString(CultureInfo.InvariantCulture)
            + ", SkipCodeLikeChunks " + defaults.SkipCodeLikeChunks.ToString()
            + ", GuardIdentifierOnlyChanges " + defaults.GuardIdentifierOnlyChanges.ToString()
            + ", GuardNumbersAndNegations " + defaults.GuardNumbersAndNegations.ToString());
        Console.WriteLine("Guards off sets NearDuplicateMinTokens to MinWords ("
            + defaults.SimHash.MinWords.ToString(CultureInfo.InvariantCulture)
            + "), SkipCodeLikeChunks false, GuardIdentifierOnlyChanges false. The number and negation guard stays on.");
        Console.WriteLine();

        var settings = new[]
        {
            new PairSetting("defaults", defaults.MinHash.Threshold, Guards: true),
            new PairSetting("guards off", defaults.MinHash.Threshold, Guards: false),
            new PairSetting("minhash 0.80", 0.80, Guards: true),
            new PairSetting("minhash 0.90", 0.90, Guards: true),
        };

        var results = new List<PairScore>(settings.Length);
        foreach (var setting in settings)
        {
            results.Add(await ScoreAsync(pairs, setting, defaults.SimHash.MinWords));
        }

        Console.WriteLine("All pairs. A merge means the second sentence was a duplicate of the first. Precision is true merges / all merges. Recall is true merges / label 1.");
        Console.WriteLine();
        Console.WriteLine("| setting | minhash | guards | true merges | false merges | precision | recall | exact true | exact false | simhash true | simhash false | minhash true | minhash false | ms |");
        Console.WriteLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var score in results)
        {
            Console.WriteLine(
                "| " + score.Name
                + " | " + score.MinHash.ToString("F2", CultureInfo.InvariantCulture)
                + " | " + (score.Guards ? "on" : "off")
                + " | " + score.TrueMerges.ToString(CultureInfo.InvariantCulture)
                + " | " + score.FalseMerges.ToString(CultureInfo.InvariantCulture)
                + " | " + Percent(score.TrueMerges, score.TrueMerges + score.FalseMerges)
                + " | " + Percent(score.TrueMerges, paraphrases)
                + " | " + score.ExactTrue.ToString(CultureInfo.InvariantCulture)
                + " | " + score.ExactFalse.ToString(CultureInfo.InvariantCulture)
                + " | " + score.SimHashTrue.ToString(CultureInfo.InvariantCulture)
                + " | " + score.SimHashFalse.ToString(CultureInfo.InvariantCulture)
                + " | " + score.MinHashTrue.ToString(CultureInfo.InvariantCulture)
                + " | " + score.MinHashFalse.ToString(CultureInfo.InvariantCulture)
                + " | " + score.Milliseconds.ToString(CultureInfo.InvariantCulture)
                + " |");
        }

        Console.WriteLine();
        Console.WriteLine("Pairs where both sentences got a MinHash signature, so the near stages were allowed to look. Recall here is true merges / label 1 in this subset.");
        Console.WriteLine();
        Console.WriteLine("| setting | pairs | label 1 | label 0 | true merges | false merges | precision | recall |");
        Console.WriteLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var score in results)
        {
            Console.WriteLine(
                "| " + score.Name
                + " | " + score.BothNear.ToString(CultureInfo.InvariantCulture)
                + " | " + score.BothNearParaphrases.ToString(CultureInfo.InvariantCulture)
                + " | " + (score.BothNear - score.BothNearParaphrases).ToString(CultureInfo.InvariantCulture)
                + " | " + score.BothNearTrue.ToString(CultureInfo.InvariantCulture)
                + " | " + score.BothNearFalse.ToString(CultureInfo.InvariantCulture)
                + " | " + Percent(score.BothNearTrue, score.BothNearTrue + score.BothNearFalse)
                + " | " + Percent(score.BothNearTrue, score.BothNearParaphrases)
                + " |");
        }

        return 0;
    }

    public static async Task<PairScore> ScoreAsync(IReadOnlyList<LabeledPair> pairs, PairSetting setting, int olderMinWords)
    {
        var builder = RechunkPipeline.CreateBuilder()
            .UseSimHash()
            .UseMinHash(o => o.Threshold = setting.MinHash);
        if (!setting.Guards)
        {
            builder
                .NearDuplicateMinTokens(olderMinWords)
                .SkipCodeLikeChunks(false)
                .GuardIdentifierOnlyChanges(false);
        }

        var pipeline = builder.Build();
        if (pipeline.Options.SimHash.MaxHammingDistance != DefaultSimHashDistance)
        {
            throw new InvalidOperationException("SimHash distance is no longer 3.");
        }

        var fingerprinter = new ChunkFingerprinter(pipeline.Options);
        var batch = new ChunkInput[2];
        var trueMerges = 0;
        var falseMerges = 0;
        var exactTrue = 0;
        var exactFalse = 0;
        var simTrue = 0;
        var simFalse = 0;
        var minTrue = 0;
        var minFalse = 0;
        var bothNear = 0;
        var bothNearParaphrases = 0;
        var bothNearTrue = 0;
        var bothNearFalse = 0;

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            var left = fingerprinter.Compute(pair.Left);
            var right = fingerprinter.Compute(pair.Right);
            var eligible = left.HasMinHash && right.HasMinHash;
            if (eligible)
            {
                bothNear++;
                if (pair.Label == 1)
                {
                    bothNearParaphrases++;
                }
            }

            batch[0] = new ChunkInput(0, pair.Left);
            batch[1] = new ChunkInput(1, pair.Right);
            var report = await pipeline.DeduplicateAsync(i.ToString(CultureInfo.InvariantCulture), batch);
            if (report.Decisions.Count != 2 || report.Decisions[0].Outcome != DedupOutcome.Unique)
            {
                throw new InvalidOperationException("Pair " + i.ToString(CultureInfo.InvariantCulture) + " did not keep the first sentence as new.");
            }

            var decision = report.Decisions[1];
            var merged = decision.Outcome is DedupOutcome.Duplicate or DedupOutcome.Flagged;
            if (!merged)
            {
                continue;
            }

            if (decision.DuplicateOf != report.Decisions[0].Id || decision.Stage is null)
            {
                throw new InvalidOperationException("Pair " + i.ToString(CultureInfo.InvariantCulture) + " matched something other than the first sentence.");
            }

            if (!eligible && decision.Stage != MatchStage.Exact)
            {
                throw new InvalidOperationException("Pair " + i.ToString(CultureInfo.InvariantCulture) + " was a near merge without a near fingerprint.");
            }

            var paraphrase = pair.Label == 1;
            if (paraphrase)
            {
                trueMerges++;
            }
            else
            {
                falseMerges++;
            }

            if (eligible)
            {
                if (paraphrase)
                {
                    bothNearTrue++;
                }
                else
                {
                    bothNearFalse++;
                }
            }

            switch (decision.Stage)
            {
                case MatchStage.Exact:
                    if (paraphrase) exactTrue++;
                    else exactFalse++;
                    break;
                case MatchStage.SimHash:
                    if (paraphrase) simTrue++;
                    else simFalse++;
                    break;
                case MatchStage.MinHash:
                    if (paraphrase) minTrue++;
                    else minFalse++;
                    break;
                default:
                    throw new InvalidOperationException("Unexpected stage " + decision.Stage);
            }
        }

        stopwatch.Stop();
        return new PairScore(
            setting.Name,
            setting.MinHash,
            setting.Guards,
            trueMerges,
            falseMerges,
            exactTrue,
            exactFalse,
            simTrue,
            simFalse,
            minTrue,
            minFalse,
            bothNear,
            bothNearParaphrases,
            bothNearTrue,
            bothNearFalse,
            stopwatch.ElapsedMilliseconds);
    }

    public static LoadedPairs Load(string path, int limit)
    {
        var pairs = new List<LabeledPair>();
        var capped = false;
        using var reader = new StreamReader(path);
        var header = reader.ReadLine();
        if (header is null || header.TrimStart('\uFEFF') != "id\tsentence1\tsentence2\tlabel")
        {
            throw new InvalidDataException("Expected a header of id, sentence1, sentence2, label.");
        }

        var row = 1;
        while (reader.ReadLine() is { } line)
        {
            row++;
            if (line.Length == 0)
            {
                continue;
            }

            if (pairs.Count >= limit)
            {
                capped = true;
                break;
            }

            pairs.Add(Parse(line, row));
        }

        return new LoadedPairs(pairs, capped);
    }

    private static LabeledPair Parse(string line, int row)
    {
        var parts = line.Split('\t');
        if (parts.Length != 4)
        {
            throw new InvalidDataException("Row " + row.ToString(CultureInfo.InvariantCulture) + " does not have 4 columns.");
        }

        if (parts[3] is not ("0" or "1"))
        {
            throw new InvalidDataException("Row " + row.ToString(CultureInfo.InvariantCulture) + " has a label other than 0 or 1.");
        }

        return new LabeledPair(parts[0], parts[1], parts[2], parts[3][0] - '0');
    }

    private static string Percent(int numerator, int denominator)
    {
        if (denominator == 0)
        {
            return "n/a";
        }

        return (100.0 * numerator / denominator).ToString("F1", CultureInfo.InvariantCulture) + "%";
    }
}

public readonly record struct LabeledPair(string Id, string Left, string Right, int Label);

public readonly record struct LoadedPairs(List<LabeledPair> Pairs, bool Capped);

public readonly record struct PairSetting(string Name, double MinHash, bool Guards);

public readonly record struct PairScore(
    string Name,
    double MinHash,
    bool Guards,
    int TrueMerges,
    int FalseMerges,
    int ExactTrue,
    int ExactFalse,
    int SimHashTrue,
    int SimHashFalse,
    int MinHashTrue,
    int MinHashFalse,
    int BothNear,
    int BothNearParaphrases,
    int BothNearTrue,
    int BothNearFalse,
    long Milliseconds);
