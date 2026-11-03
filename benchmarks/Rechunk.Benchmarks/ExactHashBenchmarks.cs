using BenchmarkDotNet.Attributes;

namespace Rechunk.Benchmarks;

[MemoryDiagnoser]
public class ExactHashBenchmarks
{
    private readonly ChunkFingerprinter _fingerprinter = new(new RechunkOptions());
    private string _chunk = string.Empty;

    [Params(200, 1000, 4000)]
    public int Characters { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // text with some double spaces so the normalizer has real work to do
        var words = new[] { "the", "notice", "period", "is", "thirty", "days", "for", "all", "staff" };
        var sb = new System.Text.StringBuilder();
        var i = 0;
        while (sb.Length < Characters)
        {
            sb.Append(words[i++ % words.Length]).Append(i % 5 == 0 ? "  " : " ");
        }

        _chunk = sb.ToString();
    }

    [Benchmark]
    public byte[] Normalize_and_hash() => ExactHasher.Hash(_chunk);

    [Benchmark]
    public ChunkFingerprint Fingerprint() => _fingerprinter.Compute(_chunk);
}
