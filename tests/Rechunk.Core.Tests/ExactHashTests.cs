namespace Rechunk.Core.Tests;

public class ExactHashTests
{
    private readonly ChunkFingerprinter _fingerprinter = new(new RechunkOptions());

    [Fact]
    public void Hash_matches_known_sha256_value()
    {
        // golden value: sha256 of "hello world". If this ever fails, stored hashes would break.
        var hash = Convert.ToHexString(ExactHasher.Hash("hello world")).ToLowerInvariant();
        Assert.Equal("b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9", hash);
    }

    [Fact]
    public void Extra_spaces_and_line_endings_do_not_change_the_hash()
    {
        var a = _fingerprinter.Compute("The notice period is 30 days.");
        var b = _fingerprinter.Compute("The  notice period\r\nis 30 days.  ");

        Assert.True(a.ExactHash.Span.SequenceEqual(b.ExactHash.Span));
    }

    [Fact]
    public void Different_text_gives_a_different_hash()
    {
        var a = _fingerprinter.Compute("The notice period is 30 days.");
        var b = _fingerprinter.Compute("The notice period is 60 days.");

        Assert.False(a.ExactHash.Span.SequenceEqual(b.ExactHash.Span));
    }

    [Fact]
    public void Hash_is_the_same_every_time()
    {
        var a = _fingerprinter.Compute("same text");
        var b = _fingerprinter.Compute("same text");

        Assert.True(a.ExactHash.Span.SequenceEqual(b.ExactHash.Span));
        Assert.Equal(32, a.ExactHash.Length);
    }

    [Fact]
    public void Algorithm_version_names_the_normalizer_and_hash()
    {
        Assert.Contains(TextNormalizer.Version, _fingerprinter.AlgorithmVersion);
        Assert.Contains(ExactHasher.Version, _fingerprinter.AlgorithmVersion);
    }

    [Fact]
    public void SimHash_and_MinHash_are_off_by_default_and_leave_empty_values()
    {
        var fp = _fingerprinter.Compute("anything");

        Assert.Equal(0UL, fp.SimHash);
        Assert.True(fp.MinHash.IsEmpty);
    }

    [Fact]
    public void Turning_on_SimHash_throws_until_it_is_written()
    {
        var options = new RechunkOptions();
        options.SimHash.Enabled = true;

        Assert.Throws<NotImplementedException>(() => new ChunkFingerprinter(options).Compute("anything"));
    }

    [Fact]
    public void Turning_on_MinHash_throws_until_it_is_written()
    {
        var options = new RechunkOptions();
        options.MinHash.Enabled = true;

        Assert.Throws<NotImplementedException>(() => new ChunkFingerprinter(options).Compute("anything"));
    }
}
