namespace Rechunk.Core.Tests;

public class MinHasherTests
{
    private static readonly MinHasher Default = new(new MinHashOptions());

    // Golden values come from /workspace/ref/minhash_ref.py, a separate Python version of the same steps
    // (FNV-1a plus murmur3 finalizer for the word hashes, one seeded mix per slot). If one of these changes, the
    // signature changed, and so every stored signature is out of date and the algorithm version has to move.
    public static TheoryData<string, uint[], uint[], ulong, uint> Goldens => new()
    {
        {
            "The quick brown fox jumps over the lazy dog near the river bank today.",
            new uint[] { 275441617, 291661738, 768290347, 99895899 }, new uint[] { 282632731, 708735066 }, 47145891505, 2837883549
        },
        {
            "The quick brown fox jumps over the lazy dog near the river bank this morning.",
            new uint[] { 275441617, 291661738, 462572668, 99895899 }, new uint[] { 282632731, 708735066 }, 43960203418, 1671346518
        },
        {
            "Employees must give a notice period of 30 days before leaving the company and returning all equipment to the office.",
            new uint[] { 377172696, 17188267, 327439944, 153364489 }, new uint[] { 28005044, 50125495 }, 26640820903, 89865541
        },
        {
            "We don't ship to PO boxes, and we won't change the delivery address after the order has left our warehouse.",
            new uint[] { 241103620, 256795603, 563305292, 15154496 }, new uint[] { 255627889, 116174512 }, 28219753421, 1006996373
        },
        {
            "Chapter 1",
            new uint[] { 3228551136, 1260425159, 2362171962, 3676923490 }, new uint[] { 2215777106, 1503054109 }, 267460683117, 2707098463
        },
    };

    [Theory]
    [MemberData(nameof(Goldens))]
    public void Signature_matches_the_python_reference(string text, uint[] first, uint[] last, ulong sum, uint xor)
    {
        var signature = Default.Compute(text);

        Assert.Equal(128, signature.Length);
        Assert.Equal(first, signature[..4].ToArray());
        Assert.Equal(last, signature[^2..].ToArray());
        Assert.Equal(sum, signature.ToArray().Aggregate(0UL, (acc, v) => acc + v));
        Assert.Equal(xor, signature.ToArray().Aggregate(0u, (acc, v) => acc ^ v));
    }

    [Fact]
    public void Same_text_gives_the_same_signature_every_time()
    {
        var text = TestText.Paragraph(4, 80);

        Assert.Equal(Default.Compute(text).ToArray(), new MinHasher(new MinHashOptions()).Compute(text).ToArray());
    }

    [Fact]
    public void Identical_signatures_estimate_one_and_an_empty_one_estimates_zero()
    {
        var a = Default.Compute(TestText.Paragraph(5, 60));

        Assert.Equal(1.0, MinHasher.EstimateJaccard(a.Span, a.Span));
        Assert.Equal(0.0, MinHasher.EstimateJaccard(ReadOnlySpan<uint>.Empty, ReadOnlySpan<uint>.Empty));
    }

    [Fact]
    public void Estimate_rejects_signatures_of_different_length()
    {
        var a = new uint[128];
        var b = new uint[64];

        Assert.Throws<ArgumentException>(() => MinHasher.EstimateJaccard(a, b));
    }

    [Fact]
    public void Text_without_words_gives_no_signature()
    {
        Assert.True(Default.Compute("   ...   ").IsEmpty);
    }

    [Fact]
    public void Text_shorter_than_one_shingle_still_gets_a_signature_from_what_there_is()
    {
        var hasher = new MinHasher(new MinHashOptions { ShingleSize = 5 });

        var signature = hasher.Compute("two words");

        Assert.Equal(128, signature.Length);
        Assert.NotEqual(Enumerable.Repeat(uint.MaxValue, 128), signature.ToArray());
    }

    [Fact]
    public void Case_and_punctuation_spacing_do_not_change_the_signature_of_normalized_text()
    {
        // the fingerprinter normalizes first, the hasher itself works on words
        var a = Default.Compute("Notice period: thirty days, in writing.");
        var b = Default.Compute("notice   period thirty days   in writing");

        Assert.Equal(a.ToArray(), b.ToArray());
    }

    [Fact]
    public void The_number_of_permutations_sets_the_signature_length()
    {
        foreach (var n in new[] { 16, 64, 200 })
        {
            Assert.Equal(n, new MinHasher(new MinHashOptions { Permutations = n }).Compute(TestText.Paragraph(6, 40)).Length);
        }
    }

    [Fact]
    public void A_bigger_shingle_makes_one_changed_word_count_for_more()
    {
        var words = TestText.Words(7, 100);
        var edited = (string[])words.Clone();
        edited[50] = "changed";
        var a = string.Join(' ', words);
        var b = string.Join(' ', edited);

        double Estimate(int shingle)
        {
            var hasher = new MinHasher(new MinHashOptions { ShingleSize = shingle, Permutations = 512 });
            return MinHasher.EstimateJaccard(hasher.Compute(a).Span, hasher.Compute(b).Span);
        }

        Assert.True(Estimate(1) > Estimate(3));
        Assert.True(Estimate(3) > Estimate(6));
    }

    [Fact]
    public void Options_that_make_no_sense_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MinHasher(new MinHashOptions { Permutations = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MinHasher(new MinHashOptions { ShingleSize = 0 }));
    }

    // The estimate is the share of equal slots, so its error is about sqrt(J(1-J)/128), at most 0.044.
    // Exact Jaccard is worked out here from plain word sets, without the library tokenizer.
    [Fact]
    public void Estimate_stays_close_to_the_exact_jaccard_on_labelled_pairs()
    {
        var rnd = new Random(2026);
        var errors = new List<double>();

        foreach (var (words, edits) in new[]
        {
            (60, 0), (60, 1), (60, 3), (120, 2), (120, 6), (120, 12), (200, 5), (200, 20), (200, 60), (40, 10), (100, 50),
        })
        {
            for (var round = 0; round < 10; round++)
            {
                var a = Enumerable.Range(0, words).Select(_ => "w" + rnd.Next(5000)).ToArray();
                var b = (string[])a.Clone();
                foreach (var position in Enumerable.Range(0, words).OrderBy(_ => rnd.Next()).Take(edits))
                {
                    b[position] = "x" + b[position];
                }

                var exact = ExactJaccard(a, b, 3);
                var estimate = MinHasher.EstimateJaccard(
                    Default.Compute(string.Join(' ', a)).Span, Default.Compute(string.Join(' ', b)).Span);

                Assert.InRange(estimate, exact - 0.13, exact + 0.13);
                errors.Add(estimate - exact);
            }
        }

        Assert.True(errors.Average(Math.Abs) < 0.04, $"mean absolute error {errors.Average(Math.Abs):F4}");
        Assert.True(Math.Abs(errors.Average()) < 0.015, $"mean error {errors.Average():F4}, the estimate should not lean one way");
    }

    [Fact]
    public void Unrelated_text_estimates_close_to_zero()
    {
        var a = Default.Compute(TestText.Paragraph(11, 120));
        var b = Default.Compute(TestText.Paragraph(12, 120));

        Assert.True(MinHasher.EstimateJaccard(a.Span, b.Span) < 0.05);
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
