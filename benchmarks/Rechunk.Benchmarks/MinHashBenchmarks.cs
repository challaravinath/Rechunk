using BenchmarkDotNet.Attributes;

namespace Rechunk.Benchmarks;

/// <summary>How long it takes to turn a chunk of text into a MinHash signature (128 permutations by default).</summary>
[MemoryDiagnoser]
public class MinHashFingerprintBenchmarks
{
    private ChunkFingerprinter _fingerprinter = null!;
    private MinHasher _hasher = null!;
    private string _chunk = string.Empty;
    private string _normalized = string.Empty;

    [Params(200, 1000, 4000)]
    public int Characters { get; set; }

    [Params(64, 128)]
    public int Permutations { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var options = new RechunkOptions();
        options.MinHash.Enabled = true;
        options.MinHash.Permutations = Permutations;
        _fingerprinter = new ChunkFingerprinter(options);
        _hasher = new MinHasher(options.MinHash);

        var random = new Random(42);
        var sb = new System.Text.StringBuilder();
        while (sb.Length < Characters)
        {
            sb.Append("w").Append(random.Next(5000)).Append(' ');
        }

        _chunk = sb.ToString();
        _normalized = TextNormalizer.Normalize(_chunk);
    }

    [Benchmark]
    public ReadOnlyMemory<uint> MinHash_only() => _hasher.Compute(_normalized);

    [Benchmark]
    public ChunkFingerprint Full_fingerprint_with_minhash() => _fingerprinter.Compute(_chunk);
}

/// <summary>How long a lookup takes when the LSH index already holds a lot of signatures.</summary>
[MemoryDiagnoser]
public class MinHashIndexBenchmarks
{
    private const int Permutations = 128;

    private MinHashLshIndex _index = null!;
    private uint[][] _nearQueries = null!;
    private uint[][] _randomQueries = null!;
    private int _next;

    [Params(10_000, 100_000)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(42);
        _index = new MinHashLshIndex(Permutations, 0.85);
        var stored = new uint[Size][];
        for (var i = 0; i < Size; i++)
        {
            stored[i] = Random128(random);
            _index.Add(new ChunkId($"chunk-{i}"), "bench", stored[i]);
        }

        // queries that share about 90 percent of the slots with something stored, and queries that match nothing
        _nearQueries = new uint[1024][];
        _randomQueries = new uint[1024][];
        for (var i = 0; i < _nearQueries.Length; i++)
        {
            var copy = (uint[])stored[random.Next(Size)].Clone();
            for (var change = 0; change < 13; change++)
            {
                copy[random.Next(Permutations)] = (uint)random.NextInt64(0, uint.MaxValue);
            }

            _nearQueries[i] = copy;
            _randomQueries[i] = Random128(random);
        }
    }

    private static uint[] Random128(Random random)
    {
        var s = new uint[Permutations];
        for (var i = 0; i < s.Length; i++)
        {
            s[i] = (uint)random.NextInt64(0, uint.MaxValue);
        }

        return s;
    }

    [Benchmark]
    public int Find_near_neighbour() => _index.Find("bench", _nearQueries[_next++ & 1023]).Count;

    [Benchmark]
    public int Find_with_no_match() => _index.Find("bench", _randomQueries[_next++ & 1023]).Count;

    [Benchmark]
    public void Add_then_remove()
    {
        var id = new ChunkId("temp");
        _index.Add(id, "bench", _nearQueries[_next++ & 1023]);
        _index.Remove(id, "bench");
    }
}
