using System.Collections.Concurrent;

namespace Rechunk;

/// <summary>
/// Finds chunks that are similar to a given one by Jaccard similarity of their word shingles.
///
/// Every signature is cut into Bands groups of Rows slots and filed in one table per band under the
/// value of those slots. A lookup only looks at chunks that share at least one whole band with the
/// query (see LshParameters for why that works), then compares the full signatures and keeps those with
/// an estimated Jaccard of at least Threshold. So the answer is exact on the estimate, and LSH only
/// decides which chunks are worth comparing.
///
/// Each scope has its own tables, so a lookup never sees chunks from another scope. All methods are
/// safe to call from several threads. Everything is kept in memory.
/// </summary>
public sealed class MinHashLshIndex : IMinHashIndex
{
    private readonly ConcurrentDictionary<string, Shard> _shards = new(StringComparer.Ordinal);

    public MinHashLshIndex(MinHashOptions options)
        : this(
            (options ?? throw new ArgumentNullException(nameof(options))).Permutations,
            options.Threshold,
            options.Bands,
            options.Rows)
    {
    }

    /// <summary>
    /// Leave bands and rows null to derive them from the threshold. If you set one, set both.
    /// Bands times rows can be less than the permutations, then the first slots are used for banding
    /// and all of them for the estimate.
    /// </summary>
    public MinHashLshIndex(int permutations, double threshold, int? bands = null, int? rows = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permutations, 1);
        if (threshold is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), "Has to be above 0 and at most 1.");
        }

        if (bands.HasValue != rows.HasValue)
        {
            throw new ArgumentException("Set both bands and rows, or neither.");
        }

        var lsh = bands is { } b && rows is { } r ? new LshParameters(b, r) : LshParameters.Derive(permutations, threshold);
        if (lsh.Bands < 1 || lsh.Rows < 1 || (long)lsh.Bands * lsh.Rows > permutations)
        {
            throw new ArgumentException("Bands and rows have to be at least 1, and bands times rows can not be more than the permutations.");
        }

        Permutations = permutations;
        Threshold = threshold;
        Lsh = lsh;
    }

    public int Permutations { get; }

    public double Threshold { get; }

    public LshParameters Lsh { get; }

    public int Bands => Lsh.Bands;

    public int Rows => Lsh.Rows;

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
    public void Add(ChunkId id, string scope, ReadOnlySpan<uint> signature, ulong guardSignature = 0)
    {
        ArgumentNullException.ThrowIfNull(scope);
        CheckLength(signature);

        var shard = _shards.GetOrAdd(scope, _ => new Shard(Bands));
        var entry = new Entry(id, signature.ToArray(), guardSignature);

        lock (shard.Gate)
        {
            if (shard.Items.TryGetValue(id, out var old))
            {
                RemoveFromBuckets(shard, old);
            }

            shard.Items[id] = entry;
            for (var band = 0; band < Bands; band++)
            {
                var key = BandKey(entry.Signature, band);
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
    /// Every chunk in the scope with an estimated Jaccard of at least Threshold, most similar first.
    /// Ties are ordered by id so the result is the same every time.
    /// </summary>
    public IReadOnlyList<NearDuplicateMatch> Find(string scope, ReadOnlySpan<uint> signature)
    {
        ArgumentNullException.ThrowIfNull(scope);
        CheckLength(signature);

        if (!_shards.TryGetValue(scope, out var shard))
        {
            return Array.Empty<NearDuplicateMatch>();
        }

        var seen = new HashSet<ChunkId>();
        var found = new List<NearDuplicateMatch>();

        lock (shard.Gate)
        {
            for (var band = 0; band < Bands; band++)
            {
                if (!shard.Tables[band].TryGetValue(BandKey(signature, band), out var bucket))
                {
                    continue;
                }

                foreach (var entry in bucket)
                {
                    if (!seen.Add(entry.Id))
                    {
                        continue;
                    }

                    var jaccard = MinHasher.EstimateJaccard(signature, entry.Signature);
                    if (jaccard >= Threshold)
                    {
                        found.Add(new NearDuplicateMatch(entry.Id, MatchStage.MinHash, null, jaccard, entry.GuardSignature));
                    }
                }
            }
        }

        if (found.Count == 0)
        {
            return Array.Empty<NearDuplicateMatch>();
        }

        return found
            .OrderByDescending(m => m.EstimatedJaccard)
            .ThenBy(m => m.Existing.Value, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Estimated Jaccard between the signature and a chunk that is stored, or null if that chunk is not stored.
    /// Looks the chunk up by id, so it works even when LSH would not have proposed it.
    /// </summary>
    public double? EstimateJaccard(string scope, ChunkId id, ReadOnlySpan<uint> signature)
    {
        ArgumentNullException.ThrowIfNull(scope);
        CheckLength(signature);

        if (!_shards.TryGetValue(scope, out var shard))
        {
            return null;
        }

        lock (shard.Gate)
        {
            return shard.Items.TryGetValue(id, out var entry)
                ? MinHasher.EstimateJaccard(signature, entry.Signature)
                : null;
        }
    }

    public ValueTask AddAsync(ChunkId id, string scope, ChunkFingerprint fingerprint, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Add(id, scope, fingerprint.MinHash.Span, fingerprint.GuardSignature);
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
        return ValueTask.FromResult(Find(scope, fingerprint.MinHash.Span));
    }

    public ValueTask<double?> EstimateJaccardAsync(
        string scope, ChunkId id, ReadOnlyMemory<uint> signature, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(EstimateJaccard(scope, id, signature.Span));
    }

    private void CheckLength(ReadOnlySpan<uint> signature)
    {
        if (signature.Length != Permutations)
        {
            throw new ArgumentException($"The signature has {signature.Length} slots but the index expects {Permutations}.", nameof(signature));
        }
    }

    // the slots of one band folded into one 64 bit key. A clash only means one extra comparison.
    private ulong BandKey(ReadOnlySpan<uint> signature, int band)
    {
        var h = 0xcbf29ce484222325UL;
        var start = band * Rows;
        for (var i = 0; i < Rows; i++)
        {
            h = StableHash64.Combine(h, signature[start + i]);
        }

        return h;
    }

    private void RemoveFromBuckets(Shard shard, Entry entry)
    {
        for (var band = 0; band < Bands; band++)
        {
            var key = BandKey(entry.Signature, band);
            if (!shard.Tables[band].TryGetValue(key, out var bucket))
            {
                continue;
            }

            var at = bucket.FindIndex(e => e.Id == entry.Id);
            if (at >= 0)
            {
                bucket[at] = bucket[^1];
                bucket.RemoveAt(bucket.Count - 1);
            }

            if (bucket.Count == 0)
            {
                shard.Tables[band].Remove(key);
            }
        }
    }

    private sealed record Entry(ChunkId Id, uint[] Signature, ulong GuardSignature);

    private sealed class Shard
    {
        public Shard(int bands)
        {
            Tables = new Dictionary<ulong, List<Entry>>[bands];
            for (var i = 0; i < bands; i++)
            {
                Tables[i] = new Dictionary<ulong, List<Entry>>();
            }
        }

        public object Gate { get; } = new();

        public Dictionary<ChunkId, Entry> Items { get; } = new();

        public Dictionary<ulong, List<Entry>>[] Tables { get; }
    }
}
