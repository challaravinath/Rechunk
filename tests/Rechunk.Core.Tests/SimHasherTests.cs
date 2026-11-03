namespace Rechunk.Core.Tests;

public class SimHasherTests
{
    private readonly SimHasher _hasher = new();

    private ulong Hash(string text) => _hasher.Compute(TextNormalizer.Normalize(text));

    // Golden values. They were made with a separate Python implementation of the same steps
    // (FNV-1a, murmur3 finalizer, 3 word shingles, seed "RECHUNK1"), not with this code.
    // If one of these fails, every stored SimHash would be wrong, so do not just update the number:
    // change SimHasher.Version as well.
    [Theory]
    [InlineData("The quick brown fox jumps over the lazy dog near the river bank today.", 0x208D21022F54D43AUL)]
    [InlineData("The quick brown fox jumps over the lazy dog near the river bank this morning.", 0xD08D2166AFD3D72FUL)]
    [InlineData("Quarterly revenue grew because enterprise customers renewed their annual contracts earlier than planned.", 0xC99FB021750B0281UL)]
    [InlineData("Employees must give a notice period of 30 days before leaving the company and returning all equipment to the office.", 0x47C0C7ED2487DAE0UL)]
    [InlineData("Employees must give a notice period of 60 days before leaving the company and returning all equipment to the office.", 0x4641074C141BA260UL)]
    [InlineData("We don't ship to PO boxes, and we won't change the delivery address after the order has left our warehouse.", 0x11A2484E60163E9CUL)]
    [InlineData("Chapter 1", 0xEB8A7A003917A0FEUL)]
    public void Matches_golden_values(string text, ulong expected)
    {
        Assert.Equal(expected, Hash(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("... --- !!!")]
    public void Text_without_words_gives_zero(string text)
    {
        Assert.Equal(0UL, Hash(text));
    }

    [Fact]
    public void Same_text_gives_the_same_hash_from_any_instance_any_time()
    {
        var text = TestText.Paragraph(7, 120);
        var first = Hash(text);

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(first, new SimHasher().Compute(TextNormalizer.Normalize(text)));
        }
    }

    [Fact]
    public void Case_punctuation_and_spacing_do_not_matter()
    {
        var plain = "the notice period is thirty days for all staff and managers";
        var messy = "The  Notice period, is THIRTY days -- for all staff (and managers)!";

        Assert.Equal(Hash(plain), Hash(messy));
    }

    [Fact]
    public void Apostrophes_inside_words_are_ignored()
    {
        Assert.Equal(Hash("we dont ship to boxes and we wont change it"), Hash("we don't ship to boxes and we won\u2019t change it"));
    }

    [Fact]
    public void Composed_and_decomposed_accents_give_the_same_hash()
    {
        Assert.Equal(Hash("a small caf\u00e9 near the old station gate"), Hash("a small cafe\u0301 near the old station gate"));
    }

    [Fact]
    public void Word_order_matters()
    {
        Assert.NotEqual(Hash("one two three four five six seven eight"), Hash("eight seven six five four three two one"));
    }

    [Fact]
    public void One_changed_word_in_a_long_text_stays_close()
    {
        var original = Hash(TestText.Paragraph(11, 300));
        var edited = Hash(TestText.WithWordChanged(11, 300, 150, "zzzedit"));

        Assert.InRange(SimHasher.Distance(original, edited), 0, 8);
    }

    [Fact]
    public void Unrelated_texts_are_far_apart()
    {
        // two random 64 bit values differ in 32 bits on average, anything under 12 would be suspicious
        for (var seed = 0; seed < 25; seed++)
        {
            var a = Hash(TestText.Paragraph(seed, 150));
            var b = Hash(TestText.Paragraph(seed + 1000, 150));

            Assert.True(SimHasher.Distance(a, b) >= 12, $"seed {seed}");
        }
    }

    [Fact]
    public void Edits_move_the_hash_further_the_more_text_changes()
    {
        var words = TestText.Words(5, 200);
        var baseline = _hasher.Compute(string.Join(' ', words));

        int DistanceAfterChanging(int count)
        {
            var edited = (string[])words.Clone();
            for (var i = 0; i < count; i++)
            {
                edited[i * 10] = "edit" + i;
            }

            return SimHasher.Distance(baseline, _hasher.Compute(string.Join(' ', edited)));
        }

        var small = DistanceAfterChanging(1);
        var large = DistanceAfterChanging(15);

        Assert.True(small < large, $"small {small}, large {large}");
    }

    [Fact]
    public void Text_shorter_than_one_shingle_still_gets_a_hash()
    {
        Assert.NotEqual(0UL, new SimHasher(shingleSize: 5).Compute("two words"));
    }

    [Fact]
    public void Shingle_size_changes_the_result()
    {
        var text = TextNormalizer.Normalize(TestText.Paragraph(3, 60));

        Assert.NotEqual(new SimHasher(2).Compute(text), new SimHasher(4).Compute(text));
    }

    [Fact]
    public void Different_seed_gives_a_different_hash()
    {
        var text = TextNormalizer.Normalize(TestText.Paragraph(3, 60));

        Assert.NotEqual(new SimHasher(3, 1).Compute(text), new SimHasher(3, 2).Compute(text));
    }

    [Fact]
    public void Shingle_size_below_one_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimHasher(0));
    }

    [Fact]
    public void Distance_counts_differing_bits()
    {
        Assert.Equal(0, SimHasher.Distance(5UL, 5UL));
        Assert.Equal(1, SimHasher.Distance(0UL, 1UL));
        Assert.Equal(64, SimHasher.Distance(0UL, ulong.MaxValue));
        Assert.Equal(3, SimHasher.Distance(0b1010UL, 0b0001UL));
    }

    [Fact]
    public void Bits_are_spread_over_the_whole_hash()
    {
        // over many different texts every one of the 64 bits should be set about half of the time
        var counts = new int[64];
        const int n = 400;
        for (var seed = 0; seed < n; seed++)
        {
            var h = Hash(TestText.Paragraph(seed, 60));
            for (var bit = 0; bit < 64; bit++)
            {
                counts[bit] += (int)((h >> bit) & 1);
            }
        }

        Assert.All(counts, c => Assert.InRange(c, 130, 270));
    }
}
