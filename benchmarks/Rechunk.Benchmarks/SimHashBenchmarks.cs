using BenchmarkDotNet.Attributes;

namespace Rechunk.Benchmarks;

/// <summary>How long it takes to turn a chunk of text into a SimHash.</summary>
[MemoryDiagnoser]
public class SimHashFingerprintBenchmarks
{
    private ChunkFingerprinter _fingerprinter = null!;
    private SimHasher _hasher = null!;
    private string _chunk = string.Empty;
    private string _normalized = string.Empty;

    [Params(200, 1000, 4000)]
    public int Characters { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var options = new RechunkOptions();
        options.SimHash.Enabled = true;
        _fingerprinter = new ChunkFingerprinter(options);
        _hasher = new SimHasher();

        var words = new[] { "the", "notice", "period", "is", "thirty", "days", "for", "all", "staff", "and", "managers", "except", "interns" };
        var sb = new System.Text.StringBuilder();
        var i = 0;
        while (sb.Length < Characters)
        {
            sb.Append(words[i * 7 % words.Length]).Append(i % 5 == 0 ? ", " : " ");
            i++;
        }

        _chunk = sb.ToString();
        _normalized = TextNormalizer.Normalize(_chunk);
    }

    [Benchmark]
    public ulong SimHash_only() => _hasher.Compute(_normalized);

    [Benchmark]
    public ChunkFingerprint Full_fingerprint_with_simhash() => _fingerprinter.Compute(_chunk);
}

/// <summary>How long a lookup takes when the index already holds a lot of chunks.</summary>
[MemoryDiagnoser]
public class SimHashIndexBenchmarks
{
    private SimHashBandIndex _index = null!;
    private ulong[] _stored = null!;
    private ulong[] _nearQueries = null!;
    private ulong[] _randomQueries = null!;
    private int _next;

    [Params(10_000, 100_000)]
    public int Size { get; set; }

    [Params(3)]
    public int MaxDistance { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(42);
        _index = new SimHashBandIndex(MaxDistance);
        _stored = new ulong[Size];
        for (var i = 0; i < Size; i++)
        {
            _stored[i] = (ulong)random.NextInt64() ^ ((ulong)random.NextInt64() << 1);
            _index.Add(new ChunkId($"chunk-{i}"), "bench", _stored[i]);
        }

        // queries that are a few bits away from something stored, and queries that match nothing
        _nearQueries = new ulong[1024];
        _randomQueries = new ulong[1024];
        for (var i = 0; i < _nearQueries.Length; i++)
        {
            var hash = _stored[random.Next(Size)];
            for (var flip = 0; flip < 2; flip++)
            {
                hash ^= 1UL << random.Next(64);
            }

            _nearQueries[i] = hash;
            _randomQueries[i] = (ulong)random.NextInt64() ^ ((ulong)random.NextInt64() << 1);
        }
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
