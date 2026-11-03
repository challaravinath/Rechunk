namespace Rechunk;

/// <summary>
/// How a MinHash signature is cut up for LSH: Bands groups of Rows slots each.
///
/// Two chunks become candidates when all Rows slots of at least one band are equal. If their Jaccard
/// similarity is s, one slot is equal with probability s, a whole band with s^Rows, and at least one
/// band with 1 - (1 - s^Rows)^Bands. That curve is an S: low similarity almost never matches, high
/// similarity almost always does, and it turns steeply around (1 / Bands)^(1 / Rows).
/// For 9 bands of 14 rows that point is (1/9)^(1/14), about 0.855, so a threshold of 0.85 fits.
///
/// Derive picks the Bands and Rows (using at most all slots) whose curve is closest to a step at the
/// threshold. A missed pair costs more than a wrong candidate, because candidates are checked against
/// the real estimate afterwards, so misses are weighted 0.7 and wrong candidates 0.3.
/// </summary>
public readonly record struct LshParameters(int Bands, int Rows)
{
    private const double MissWeight = 0.7;
    private const double WrongCandidateWeight = 0.3;
    private const int Steps = 100;

    /// <summary>Similarity where the S curve is steepest.</summary>
    public double MidPoint => Math.Pow(1.0 / Bands, 1.0 / Rows);

    /// <summary>Chance that two chunks with this Jaccard similarity share at least one band.</summary>
    public double CandidateProbability(double similarity) => 1 - Math.Pow(1 - Math.Pow(similarity, Rows), Bands);

    public static LshParameters Derive(int permutations, double threshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permutations, 1);
        if (threshold is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), "Has to be above 0 and at most 1.");
        }

        var best = new LshParameters(1, 1);
        var bestError = double.MaxValue;

        for (var bands = 1; bands <= permutations; bands++)
        {
            for (var rows = 1; rows <= permutations / bands; rows++)
            {
                var candidate = new LshParameters(bands, rows);
                var error = candidate.Error(threshold);
                if (error < bestError - 1e-12)
                {
                    best = candidate;
                    bestError = error;
                }
            }
        }

        return best;
    }

    private double Error(double threshold)
    {
        double wrongCandidates = 0;
        double misses = 0;
        for (var i = 0; i < Steps; i++)
        {
            var s = (i + 0.5) / Steps;
            var p = CandidateProbability(s);
            if (s < threshold)
            {
                wrongCandidates += p / Steps;
            }
            else
            {
                misses += (1 - p) / Steps;
            }
        }

        return WrongCandidateWeight * wrongCandidates + MissWeight * misses;
    }
}
