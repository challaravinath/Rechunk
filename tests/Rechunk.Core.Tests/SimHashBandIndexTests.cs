using System.Numerics;

namespace Rechunk.Core.Tests;

public class SimHashBandIndexTests
{
    private static ChunkId Id(int n) => new($"chunk-{n:D6}");

    private static ulong RandomHash(Random random) => (ulong)random.NextInt64() ^ ((ulong)random.NextInt64() << 1);

    private static ulong FlipBits(ulong hash, Random random, int count)
    {
        var positions = Enumerable.Range(0, 64).OrderBy(_ => random.Next()).Take(count);
        foreach (var p in positions)
        {
            hash ^= 1UL << p;
        }

        return hash;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(20)]
    [InlineData(63)]
    public void Finds_exactly_what_a_brute_force_scan_finds(int maxDistance)
    {
        var random = new Random(1234 + maxDistance);
        var index = new SimHashBandIndex(maxDistance);
        var stored = new List<(ChunkId Id, ulong Hash)>();

        // random hashes, plus neighbours that sit just inside and just outside the limit
        for (var i = 0; i < 600; i++)
        {
            var hash = RandomHash(random);
            stored.Add((Id(i * 3), hash));
            stored.Add((Id(i * 3 + 1), FlipBits(hash, random, Math.Min(64, maxDistance))));
            stored.Add((Id(i * 3 + 2), FlipBits(hash, random, Math.Min(64, maxDistance + 1))));
        }

        foreach (var (id, hash) in stored)
        {
            index.Add(id, "s", hash);
        }

        for (var q = 0; q < 300; q++)
        {
            var query = q % 2 == 0 ? RandomHash(random) : FlipBits(stored[random.Next(stored.Count)].Hash, random, random.Next(0, maxDistance + 2));

            var expected = stored
                .Where(s => BitOperations.PopCount(s.Hash ^ query) <= maxDistance)
                .Select(s => s.Id)
                .OrderBy(i => i.Value, StringComparer.Ordinal)
                .ToList();

            var actual = index.Find("s", query).Select(m => m.Existing).OrderBy(i => i.Value, StringComparer.Ordinal).ToList();

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Reports_the_real_hamming_distance_closest_first()
    {
        var index = new SimHashBandIndex(3);
        var query = 0xF0F0F0F0F0F0F0F0UL;
        index.Add(Id(1), "s", query ^ 0b111UL);   // 3 away
        index.Add(Id(2), "s", query);             // 0 away
        index.Add(Id(3), "s", query ^ 0b1UL);     // 1 away
        index.Add(Id(4), "s", query ^ 0b1111UL);  // 4 away, too far

        var matches = index.Find("s", query);

        Assert.Equal(new[] { Id(2), Id(3), Id(1) }, matches.Select(m => m.Existing));
        Assert.Equal(new[] { 0, 1, 3 }, matches.Select(m => m.HammingDistance));
        Assert.All(matches, m => Assert.Equal(MatchStage.SimHash, m.Stage));
    }

    [Fact]
    public void Ties_come_back_in_id_order()
    {
        var index = new SimHashBandIndex(3);
        index.Add(Id(9), "s", 100);
        index.Add(Id(2), "s", 100);
        index.Add(Id(5), "s", 100);

        Assert.Equal(new[] { Id(2), Id(5), Id(9) }, index.Find("s", 100).Select(m => m.Existing));
    }

    [Fact]
    public void Finds_a_neighbour_that_differs_in_every_slice_but_one_bit_each()
    {
        // k = 3 gives four slices of 16 bits. Flipping one bit in three different slices means the fourth
        // slice is the only one that still matches, which is the case the whole method relies on.
        var index = new SimHashBandIndex(3);
        var original = 0x0123456789ABCDEFUL;
        var neighbour = original ^ (1UL << 3) ^ (1UL << 20) ^ (1UL << 40);
        index.Add(Id(1), "s", neighbour);

        Assert.Single(index.Find("s", original));
    }

    [Fact]
    public void Never_matches_across_scopes()
    {
        var index = new SimHashBandIndex(3);
        index.Add(Id(1), "tenant-a", 42);

        Assert.Empty(index.Find("tenant-b", 42));
        Assert.Single(index.Find("tenant-a", 42));
        Assert.Equal(1, index.Count("tenant-a"));
        Assert.Equal(0, index.Count("tenant-b"));
    }

    [Fact]
    public void Same_id_can_live_in_two_scopes()
    {
        var index = new SimHashBandIndex(3);
        index.Add(Id(1), "a", 42);
        index.Add(Id(1), "b", 42);

        Assert.True(index.Remove(Id(1), "a"));

        Assert.Empty(index.Find("a", 42));
        Assert.Single(index.Find("b", 42));
    }

    [Fact]
    public void Removed_chunks_are_not_found_any_more()
    {
        var index = new SimHashBandIndex(3);
        index.Add(Id(1), "s", 1000);
        index.Add(Id(2), "s", 1000);

        Assert.True(index.Remove(Id(1), "s"));

        Assert.Equal(new[] { Id(2) }, index.Find("s", 1000).Select(m => m.Existing));
        Assert.Equal(1, index.Count("s"));
    }

    [Fact]
    public void Remove_says_false_for_unknown_ids_and_scopes()
    {
        var index = new SimHashBandIndex(3);
        index.Add(Id(1), "s", 1);

        Assert.False(index.Remove(Id(2), "s"));
        Assert.False(index.Remove(Id(1), "nope"));
        Assert.True(index.Remove(Id(1), "s"));
        Assert.False(index.Remove(Id(1), "s"));
    }

    [Fact]
    public void Adding_the_same_id_again_replaces_the_old_hash()
    {
        var index = new SimHashBandIndex(3);
        index.Add(Id(1), "s", 0UL);
        index.Add(Id(1), "s", ulong.MaxValue);

        Assert.Empty(index.Find("s", 0UL));
        Assert.Single(index.Find("s", ulong.MaxValue));
        Assert.Equal(1, index.Count("s"));
    }

    [Fact]
    public void Add_and_remove_many_leaves_nothing_behind()
    {
        var random = new Random(99);
        var index = new SimHashBandIndex(3);
        var hashes = Enumerable.Range(0, 2000).Select(_ => RandomHash(random)).ToArray();

        for (var i = 0; i < hashes.Length; i++)
        {
            index.Add(Id(i), "s", hashes[i]);
        }

        for (var i = 0; i < hashes.Length; i++)
        {
            Assert.True(index.Remove(Id(i), "s"));
        }

        Assert.Equal(0, index.Count("s"));
        Assert.All(hashes.Take(50), h => Assert.Empty(index.Find("s", h)));
    }

    [Fact]
    public void Guard_signature_comes_back_with_the_match()
    {
        var index = new SimHashBandIndex(3);
        index.Add(Id(1), "s", 77, guardSignature: 12345);

        Assert.Equal(12345UL, index.Find("s", 77).Single().GuardSignature);
    }

    [Fact]
    public async Task Async_methods_work_the_same_way()
    {
        INearDuplicateIndex index = new SimHashBandIndex(3);
        var fp = new ChunkFingerprint(new byte[32], 555, default, 9);

        await index.AddAsync(Id(1), "s", fp);
        var found = await index.FindAsync(fp, "s");
        Assert.Single(found);

        await index.RemoveAsync(Id(1), "s");
        Assert.Empty(await index.FindAsync(fp, "s"));
    }

    [Fact]
    public async Task Async_methods_respect_cancellation()
    {
        INearDuplicateIndex index = new SimHashBandIndex(3);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await index.FindAsync(default, "s", cts.Token));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(64)]
    public void Distance_outside_the_supported_range_is_rejected(int distance)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimHashBandIndex(distance));
    }

    [Fact]
    public void Concurrent_adds_finds_and_removes_stay_consistent()
    {
        var index = new SimHashBandIndex(3);
        const int perWorker = 1500;
        var workers = Math.Max(4, Environment.ProcessorCount);

        // phase 1: lots of threads add and look things up at the same time
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, w =>
        {
            var random = new Random(w);
            for (var i = 0; i < perWorker; i++)
            {
                var hash = RandomHash(random);
                var id = Id(w * perWorker + i);
                index.Add(id, "shared", hash);

                // what we just added must be findable right away, whatever the other threads do
                Assert.Contains(index.Find("shared", hash), m => m.Existing == id);
            }
        });

        Assert.Equal(workers * perWorker, index.Count("shared"));

        // phase 2: threads remove their own chunks while others keep reading
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, w =>
        {
            var random = new Random(w);
            for (var i = 0; i < perWorker; i++)
            {
                var hash = RandomHash(random);
                var id = Id(w * perWorker + i);
                if (i % 2 == 0)
                {
                    Assert.True(index.Remove(id, "shared"));
                }
                else
                {
                    Assert.Contains(index.Find("shared", hash), m => m.Existing == id);
                }
            }
        });

        Assert.Equal(workers * perWorker / 2, index.Count("shared"));
    }
}
