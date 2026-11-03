namespace Rechunk.Core.Tests;

public class MinHashLshIndexTests
{
    private const int N = 128;

    private static ChunkId Id(int n) => new($"chunk-{n:D6}");

    private static uint[] RandomSignature(Random random)
    {
        var s = new uint[N];
        for (var i = 0; i < N; i++)
        {
            s[i] = (uint)random.NextInt64(0, uint.MaxValue);
        }

        return s;
    }

    // a signature that shares about `similarity` of its slots with the original
    private static uint[] Neighbour(uint[] original, double similarity, Random random)
    {
        var copy = (uint[])original.Clone();
        var changes = (int)Math.Round((1 - similarity) * N);
        foreach (var slot in Enumerable.Range(0, N).OrderBy(_ => random.Next()).Take(changes))
        {
            copy[slot] = (uint)random.NextInt64(0, uint.MaxValue);
        }

        return copy;
    }

    [Fact]
    public void Bands_and_rows_come_from_the_threshold_when_not_given()
    {
        var index = new MinHashLshIndex(N, 0.85);

        Assert.Equal((9, 14), (index.Bands, index.Rows));
    }

    [Fact]
    public void Bands_and_rows_can_be_set_by_hand_and_are_checked()
    {
        var index = new MinHashLshIndex(N, 0.85, bands: 16, rows: 8);

        Assert.Equal((16, 8), (index.Bands, index.Rows));
        Assert.Throws<ArgumentException>(() => new MinHashLshIndex(N, 0.85, bands: 20, rows: 10));
        Assert.Throws<ArgumentException>(() => new MinHashLshIndex(N, 0.85, bands: 16));
        Assert.Throws<ArgumentException>(() => new MinHashLshIndex(N, 0.85, rows: 8));
    }

    [Fact]
    public void An_exact_copy_is_found_with_estimate_one()
    {
        var index = new MinHashLshIndex(N, 0.85);
        var signature = RandomSignature(new Random(1));
        index.Add(Id(1), "docs", signature);

        var match = Assert.Single(index.Find("docs", signature));

        Assert.Equal(Id(1), match.Existing);
        Assert.Equal(MatchStage.MinHash, match.Stage);
        Assert.Equal(1.0, match.EstimatedJaccard);
        Assert.Null(match.HammingDistance);
    }

    [Fact]
    public void Candidates_below_the_threshold_are_dropped_by_the_estimate()
    {
        // with one slot per row every pair is a candidate, so only the estimate stands in the way
        var index = new MinHashLshIndex(N, 0.85, bands: N, rows: 1);
        var random = new Random(2);
        var original = RandomSignature(random);
        index.Add(Id(1), "docs", Neighbour(original, 0.70, random));
        index.Add(Id(2), "docs", Neighbour(original, 0.90, random));

        var matches = index.Find("docs", original);

        Assert.Equal(new[] { Id(2) }, matches.Select(m => m.Existing));
        Assert.All(matches, m => Assert.True(m.EstimatedJaccard >= 0.85));
    }

    [Fact]
    public void Results_come_best_first_and_ties_are_ordered_by_id()
    {
        var index = new MinHashLshIndex(N, 0.5, bands: N, rows: 1);
        var random = new Random(3);
        var original = RandomSignature(random);
        index.Add(Id(5), "docs", Neighbour(original, 0.8, random));
        index.Add(Id(2), "docs", original);
        index.Add(Id(1), "docs", original);

        var matches = index.Find("docs", original);

        Assert.Equal(new[] { Id(1), Id(2), Id(5) }, matches.Select(m => m.Existing));
    }

    [Fact]
    public void Index_with_one_slot_per_row_finds_exactly_what_brute_force_finds()
    {
        var index = new MinHashLshIndex(N, 0.85, bands: N, rows: 1);
        var random = new Random(4);
        var stored = Enumerable.Range(0, 400).Select(_ => RandomSignature(random)).ToList();
        for (var i = 0; i < stored.Count; i++)
        {
            index.Add(Id(i), "docs", stored[i]);
        }

        var queries = stored.Take(100).Select(s => Neighbour(s, 0.9, random)).ToList();
        foreach (var query in queries)
        {
            var expected = BruteForce(stored, query, 0.85);
            Assert.Equal(expected, index.Find("docs", query).Select(m => m.Existing).OrderBy(x => x.Value).ToList());
        }
    }

    [Fact]
    public void Derived_bands_find_nearly_everything_brute_force_finds_and_nothing_extra()
    {
        var index = new MinHashLshIndex(N, 0.85);
        var random = new Random(5);
        var stored = Enumerable.Range(0, 1000).Select(_ => RandomSignature(random)).ToList();
        for (var i = 0; i < stored.Count; i++)
        {
            index.Add(Id(i), "docs", stored[i]);
        }

        int truePositives = 0, expectedTotal = 0, extra = 0;
        for (var q = 0; q < 300; q++)
        {
            // similarities from 0.80 to 0.99, so some are below and most are above the threshold
            var similarity = 0.80 + 0.19 * q / 300.0;
            var query = Neighbour(stored[q], similarity, random);
            var expected = BruteForce(stored, query, 0.85);
            var found = index.Find("docs", query).Select(m => m.Existing).ToHashSet();

            expectedTotal += expected.Count;
            truePositives += expected.Count(found.Contains);
            extra += found.Count(f => !expected.Contains(f));
        }

        var recall = (double)truePositives / expectedTotal;
        Assert.Equal(0, extra);
        Assert.True(recall >= 0.95, $"recall was {recall:P1}");
    }

    [Fact]
    public void Scopes_do_not_see_each_other()
    {
        var index = new MinHashLshIndex(N, 0.85);
        var signature = RandomSignature(new Random(6));
        index.Add(Id(1), "tenant-a", signature);

        Assert.Empty(index.Find("tenant-b", signature));
        Assert.Single(index.Find("tenant-a", signature));
        Assert.Equal(1, index.Count("tenant-a"));
        Assert.Equal(0, index.Count("tenant-b"));
        Assert.False(index.Remove(Id(1), "tenant-b"));
        Assert.Single(index.Find("tenant-a", signature));
    }

    [Fact]
    public void Removed_chunks_are_no_longer_found_and_can_be_added_again()
    {
        var index = new MinHashLshIndex(N, 0.85);
        var signature = RandomSignature(new Random(7));
        index.Add(Id(1), "docs", signature);

        Assert.True(index.Remove(Id(1), "docs"));
        Assert.False(index.Remove(Id(1), "docs"));
        Assert.Empty(index.Find("docs", signature));
        Assert.Equal(0, index.Count("docs"));

        index.Add(Id(1), "docs", signature);
        Assert.Single(index.Find("docs", signature));
    }

    [Fact]
    public void Adding_the_same_id_again_replaces_the_old_signature()
    {
        var index = new MinHashLshIndex(N, 0.85);
        var random = new Random(8);
        var first = RandomSignature(random);
        var second = RandomSignature(random);
        index.Add(Id(1), "docs", first);
        index.Add(Id(1), "docs", second);

        Assert.Equal(1, index.Count("docs"));
        Assert.Empty(index.Find("docs", first));
        Assert.Single(index.Find("docs", second));
    }

    [Fact]
    public void The_guard_signature_comes_back_with_the_match()
    {
        var index = new MinHashLshIndex(N, 0.85);
        var signature = RandomSignature(new Random(9));
        index.Add(Id(1), "docs", signature, guardSignature: 77);

        Assert.Equal(77UL, Assert.Single(index.Find("docs", signature)).GuardSignature);
    }

    [Fact]
    public void Estimate_by_id_works_for_stored_chunks_and_is_null_for_unknown_ones()
    {
        var index = new MinHashLshIndex(N, 0.85);
        var random = new Random(10);
        var original = RandomSignature(random);
        var other = Neighbour(original, 0.75, random);
        index.Add(Id(1), "docs", original);

        Assert.Equal(MinHasher.EstimateJaccard(other, original), index.EstimateJaccard("docs", Id(1), other));
        Assert.Null(index.EstimateJaccard("docs", Id(2), other));
        Assert.Null(index.EstimateJaccard("elsewhere", Id(1), other));
    }

    [Fact]
    public void A_signature_of_the_wrong_length_is_rejected()
    {
        var index = new MinHashLshIndex(N, 0.85);

        Assert.Throws<ArgumentException>(() => index.Add(Id(1), "docs", new uint[10]));
        Assert.Throws<ArgumentException>(() => index.Find("docs", new uint[10]));
    }

    [Fact]
    public async Task Async_methods_work_and_honour_cancellation()
    {
        var index = new MinHashLshIndex(N, 0.85);
        var fp = new ChunkFingerprint(new byte[32], 0, RandomSignature(new Random(11)));

        await index.AddAsync(Id(1), "docs", fp);
        var found = await index.FindAsync(fp, "docs");
        var estimate = await index.EstimateJaccardAsync("docs", Id(1), fp.MinHash);

        Assert.Single(found);
        Assert.Equal(1.0, estimate);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await index.FindAsync(fp, "docs", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await index.AddAsync(Id(2), "docs", fp, cancelled.Token));

        await index.RemoveAsync(Id(1), "docs");
        Assert.Empty(await index.FindAsync(fp, "docs"));
    }

    [Fact]
    public async Task Many_threads_can_add_remove_and_search_at_the_same_time()
    {
        var index = new MinHashLshIndex(N, 0.85);
        var random = new Random(12);
        var signatures = Enumerable.Range(0, 400).Select(_ => RandomSignature(random)).ToArray();

        // writers add everything, half of it gets removed again, readers search the whole time
        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            for (var i = w; i < signatures.Length; i += 4)
            {
                index.Add(Id(i), "docs", signatures[i]);
                if (i % 2 == 1)
                {
                    index.Remove(Id(i), "docs");
                }
            }
        })).ToArray();

        var readers = Enumerable.Range(0, 4).Select(r => Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
            {
                foreach (var m in index.Find("docs", signatures[(i + r) % signatures.Length]))
                {
                    Assert.True(m.EstimatedJaccard >= 0.85);
                }
            }
        })).ToArray();

        await Task.WhenAll(writers.Concat(readers));

        Assert.Equal(200, index.Count("docs"));
        for (var i = 0; i < signatures.Length; i++)
        {
            var found = index.Find("docs", signatures[i]).Select(m => m.Existing).ToList();
            Assert.Equal(i % 2 == 0, found.Contains(Id(i)));
        }
    }

    private static List<ChunkId> BruteForce(List<uint[]> stored, uint[] query, double threshold) =>
        Enumerable.Range(0, stored.Count)
            .Where(i => MinHasher.EstimateJaccard(query, stored[i]) >= threshold)
            .Select(Id)
            .OrderBy(id => id.Value)
            .ToList();
}
