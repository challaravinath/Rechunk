namespace Rechunk.Core.Tests;

public class FingerprintGuardTests
{
    private static ChunkFingerprinter Fingerprinter(bool simHash = true, bool guard = true, int minWords = 8)
    {
        var options = new RechunkOptions { GuardNumbersAndNegations = guard };
        options.SimHash.Enabled = simHash;
        options.SimHash.MinWords = minWords;
        return new ChunkFingerprinter(options);
    }

    private const string Policy30 = "Employees must give a notice period of 30 days before leaving the company and returning all equipment.";
    private const string Policy60 = "Employees must give a notice period of 60 days before leaving the company and returning all equipment.";

    [Fact]
    public void A_different_number_gives_a_different_guard_signature()
    {
        var f = Fingerprinter();

        Assert.NotEqual(f.Compute(Policy30).GuardSignature, f.Compute(Policy60).GuardSignature);
    }

    [Fact]
    public void Adding_not_gives_a_different_guard_signature()
    {
        var f = Fingerprinter();

        var allowed = f.Compute("Employees are allowed to work from home on Fridays with approval from their manager.");
        var forbidden = f.Compute("Employees are not allowed to work from home on Fridays with approval from their manager.");

        Assert.NotEqual(allowed.GuardSignature, forbidden.GuardSignature);
    }

    [Fact]
    public void Contractions_count_as_negations_with_or_without_the_apostrophe()
    {
        var f = Fingerprinter();

        var plain = f.Compute("We ship to every country in the world and we change addresses at any time.");
        var spelled = f.Compute("We ship to every country in the world and we don't change addresses at any time.");
        var typed = f.Compute("We ship to every country in the world and we dont change addresses at any time.");

        Assert.NotEqual(plain.GuardSignature, spelled.GuardSignature);
        Assert.Equal(spelled.GuardSignature, typed.GuardSignature);
    }

    [Fact]
    public void Wording_changes_without_numbers_or_negations_keep_the_same_signature()
    {
        var f = Fingerprinter();

        var a = f.Compute("Employees must give notice of 30 days before leaving the company and returning equipment.");
        var b = f.Compute("Staff should give notice of 30 days before departing the firm and handing back equipment.");

        Assert.Equal(a.GuardSignature, b.GuardSignature);
    }

    [Fact]
    public void Signature_is_empty_when_the_guard_is_off()
    {
        Assert.Equal(0UL, Fingerprinter(guard: false).Compute(Policy30).GuardSignature);
    }

    [Fact]
    public void Chunks_under_the_minimum_word_count_get_no_simhash()
    {
        var f = Fingerprinter(minWords: 8);

        Assert.False(f.Compute("only five words in here").HasSimHash);
        Assert.True(f.Compute("this one has quite a few more words than the limit").HasSimHash);
    }

    [Fact]
    public void Simhash_is_not_computed_when_the_stage_is_off()
    {
        Assert.False(Fingerprinter(simHash: false).Compute(Policy30).HasSimHash);
    }

    [Fact]
    public void Algorithm_version_names_every_part_that_is_switched_on()
    {
        var off = Fingerprinter(simHash: false, guard: false).AlgorithmVersion;
        var on = Fingerprinter().AlgorithmVersion;

        Assert.DoesNotContain("simhash", off);
        Assert.DoesNotContain("guard", off);
        Assert.Contains("simhash-v1", on);
        Assert.Contains("guard-v1", on);
    }

    [Fact]
    public void Changing_the_shingle_size_changes_the_algorithm_version()
    {
        var a = new RechunkOptions();
        a.SimHash.Enabled = true;
        var b = new RechunkOptions();
        b.SimHash.Enabled = true;
        b.SimHash.ShingleSize = 4;

        Assert.NotEqual(new ChunkFingerprinter(a).AlgorithmVersion, new ChunkFingerprinter(b).AlgorithmVersion);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(64)]
    public void Options_reject_a_hamming_distance_outside_0_to_63(int distance)
    {
        var options = new RechunkOptions();
        options.SimHash.MaxHammingDistance = distance;

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void Options_reject_a_shingle_size_of_zero()
    {
        var options = new RechunkOptions();
        options.SimHash.ShingleSize = 0;

        Assert.ThrowsAny<ArgumentException>(options.Validate);
    }
}
