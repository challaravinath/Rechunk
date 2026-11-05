using System.Collections.Concurrent;
using System.Numerics;

namespace Rechunk;

/// <summary>
/// Finds chunks whose SimHash is within a few bits of a given one, without comparing against everything.
///
/// The trick (pigeonhole): to find hashes that differ in at most k bits, cut the 64 bits into k + 1
/// slices. If two hashes differ in at most k bits, those bits can touch at most k slices, so at least one
/// slice is identical. So every hash is filed under each of its k + 1 slices, and a lookup only has to
/// look at the chunks that share a slice with the query, then count the real bit difference.
/// With the default k of 3 that is four slices of 16 bits.
///
/// Each scope has its own tables, so a lookup never sees chunks from another scope. All methods are
/// safe to call from several threads. Memory is kept in the process, nothing is stored on disk.
///
/// One thing to know: removing a chunk scans the bucket it was filed in. With well spread hashes the
/// buckets hold a handful of chunks, so this is cheap in practice.
/// </summary>
public sealed class SimHashBandIndex : INearDuplicateIndex
{
    private readonly ConcurrentDictionary<string, Shard> _shards = new(StringComparer.Ordinal);
    private readonly SimHashBandLayout _layout;
    private readonly int _bandCount;

    public SimHashBandIndex(int maxHammingDistance = 3)
    {
        if (maxHammingDistance is < 0 or > 63)
        {
            throw new ArgumentOutOfRangeException(nameof(maxHammingDistance), "Has to be between 0 and 63.");
        }

        MaxHammingDistance = maxHammingDistance;
        _layout = new SimHashBandLayout(maxHammingDistance);
        _bandCount = _layout.BandCount;
    }

    public int MaxHammingDistance { get; }

    /// <summary>Number of chunks stored for the scope.</summary>
    public int Count(string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!_shards.TryGetValue(scope, out var shard))
        {
            return 0;
        }

        lock (shard.Gate)
        {
            return shard.Items.Count;
        }
    }

    /// <summary>Adds a chunk. Adding an id that is already there replaces the old entry.</summary>
    public void Add(ChunkId id, string scope, ulong simHash, ulong guardSignature = 0)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var shard = _shards.GetOrAdd(scope, _ => new Shard(_bandCount));
        var entry = new Entry(id, simHash, guardSignature);

        lock (shard.Gate)
        {
            if (shard.Items.TryGetValue(id, out var old))
            {
                RemoveFromBuckets(shard, old);
            }

            shard.Items[id] = entry;
            for (var band = 0; band < _bandCount; band++)
            {
                var key = Slice(simHash, band);
                if (!shard.Tables[band].TryGetValue(key, out var bucket))
                {
                    bucket = new List<Entry>(2);
                    shard.Tables[band][key] = bucket;
                }

                bucket.Add(entry);
            }
        }
    }

    /// <summary>Removes a chunk. Says whether it was there.</summary>
    public bool Remove(ChunkId id, string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!_shards.TryGetValue(scope, out var shard))
        {
            return false;
        }

        lock (shard.Gate)
        {
            if (!shard.Items.Remove(id, out var entry))
            {
                return false;
            }

            RemoveFromBuckets(shard, entry);
            return true;
        }
    }

    /// <summary>
    /// Every chunk in the scope within MaxHammingDistance bits of simHash, closest first.
    /// Ties are ordered by id so the result is the same every time.
    /// </summary>
    public IReadOnlyList<NearDuplicateMatch> Find(string scope, ulong simHash)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!_shards.TryGetValue(scope, out var shard))
        {
            return Array.Empty<NearDuplicateMatch>();
        }

        var found = new Dictionary<ChunkId, NearDuplicateMatch>();

        lock (shard.Gate)
        {
            for (var band = 0; band < _bandCount; band++)
            {
                if (!shard.Tables[band].TryGetValue(Slice(simHash, band), out var bucket))
                {
                    continue;
                }

                foreach (var entry in bucket)
                {
                    var distance = BitOperations.PopCount(entry.SimHash ^ simHash);
                    if (distance <= MaxHammingDistance && !found.ContainsKey(entry.Id))
                    {
                        found[entry.Id] = new NearDuplicateMatch(
                            entry.Id, MatchStage.SimHash, distance, null, entry.GuardSignature);
                    }
                }
            }
        }

        if (found.Count == 0)
        {
            return Array.Empty<NearDuplicateMatch>();
        }

        return found.Values
            .OrderBy(m => m.HammingDistance)
            .ThenBy(m => m.Existing.Value, StringComparer.Ordinal)
            .ToList();
    }

    public ValueTask AddAsync(ChunkId id, string scope, ChunkFingerprint fingerprint, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Add(id, scope, fingerprint.SimHash, fingerprint.GuardSignature);
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(ChunkId id, string scope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Remove(id, scope);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<NearDuplicateMatch>> FindAsync(
        ChunkFingerprint fingerprint, string scope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Find(scope, fingerprint.SimHash));
    }

    private ulong Slice(ulong hash, int band) => _layout.Slice(hash, band);

    private void RemoveFromBuckets(Shard shard, Entry entry)
    {
        for (var band = 0; band < _bandCount; band++)
        {
            var key = Slice(entry.SimHash, band);
            if (!shard.Tables[band].TryGetValue(key, out var bucket))
            {
                continue;
            }

            var at = bucket.FindIndex(e => e.Id == entry.Id);
            if (at >= 0)
            {
                // order inside a bucket does not matter, so move the last one into the gap
                bucket[at] = bucket[^1];
                bucket.RemoveAt(bucket.Count - 1);
            }

            if (bucket.Count == 0)
            {
                shard.Tables[band].Remove(key);
            }
        }
    }

    private readonly record struct Entry(ChunkId Id, ulong SimHash, ulong GuardSignature);

    private sealed class Shard
    {
        public Shard(int bandCount)
        {
            Tables = new Dictionary<ulong, List<Entry>>[bandCount];
            for (var i = 0; i < bandCount; i++)
            {
                Tables[i] = new Dictionary<ulong, List<Entry>>();
            }
        }

        public object Gate { get; } = new();

        public Dictionary<ChunkId, Entry> Items { get; } = new();

        public Dictionary<ulong, List<Entry>>[] Tables { get; }
    }
}
