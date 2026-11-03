namespace Rechunk.Core.Tests;

public class LshParametersTests
{
    // the same numbers come out of derive() in /workspace/ref/minhash_ref.py
    [Theory]
    [InlineData(128, 0.85, 9, 14)]
    [InlineData(128, 0.50, 25, 5)]
    [InlineData(128, 0.95, 4, 32)]
    public void Derived_values_for_common_thresholds(int permutations, double threshold, int bands, int rows)
    {
        var lsh = LshParameters.Derive(permutations, threshold);

        Assert.Equal(new LshParameters(bands, rows), lsh);
    }

    [Theory]
    [InlineData(64, 0.7)]
    [InlineData(128, 0.8)]
    [InlineData(128, 0.9)]
    [InlineData(256, 0.85)]
    public void The_steepest_part_of_the_curve_sits_near_the_threshold(int permutations, double threshold)
    {
        var lsh = LshParameters.Derive(permutations, threshold);

        Assert.True(lsh.Bands * lsh.Rows <= permutations);
        Assert.InRange(lsh.MidPoint, threshold - 0.05, threshold + 0.05);
    }

    [Fact]
    public void Midpoint_is_the_bands_root_of_one_over_bands()
    {
        // (1/b)^(1/r): with 20 bands of 5 rows that is 0.05^0.2
        Assert.Equal(Math.Pow(1.0 / 20, 1.0 / 5), new LshParameters(20, 5).MidPoint, 12);
        Assert.Equal(0.5493, new LshParameters(20, 5).MidPoint, 4);
    }

    [Fact]
    public void Candidate_probability_follows_the_s_curve()
    {
        var lsh = LshParameters.Derive(128, 0.85);

        Assert.Equal(0.0, lsh.CandidateProbability(0.0), 12);
        Assert.Equal(1.0, lsh.CandidateProbability(1.0), 12);
        Assert.True(lsh.CandidateProbability(0.95) > 0.95, "very similar chunks are nearly always candidates");
        Assert.True(lsh.CandidateProbability(0.60) < 0.05, "clearly different chunks are nearly never candidates");
        Assert.InRange(lsh.CandidateProbability(lsh.MidPoint), 0.5, 0.75);
    }

    [Fact]
    public void Derive_rejects_values_that_make_no_sense()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LshParameters.Derive(0, 0.8));
        Assert.Throws<ArgumentOutOfRangeException>(() => LshParameters.Derive(128, 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LshParameters.Derive(128, 1.5));
    }
}
