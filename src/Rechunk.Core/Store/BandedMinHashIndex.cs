using System.Buffers.Binary;

namespace Rechunk;

/// <summary>
/// The MinHash LSH index on top of a database. Bands and rows are worked out the same way as in MinHashLshIndex.
/// A lookup asks the storage for the chunks that share a band, then checks the full signatures here.
/// </summary>
public sealed class BandedMinHashIndex : IMinHashIndex
{
    private readonly IBandStorage _storage;
    private readonly BandKeyDirectory? _bands;

    public BandedMinHashIndex(IBandStorage storage, MinHashOptions options)
        : this(storage, options, null)
    {
    }

    internal BandedMinHashIndex(IBandStorage storage, MinHashOptions options, BandKeyDirectory? bands)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        ArgumentNullException.ThrowIfNull(options);
        _bands = bands;

        Permutations = options.Permutations;
        Threshold = options.Threshold;
        Lsh = options.Bands is { } b && options.Rows is { } r
            ? new LshParameters(b, r)
            : LshParameters.Derive(options.Permutations, options.Threshold);
    }

    public int Permutations { get; }

    public double Threshold { get; }

    public LshParameters Lsh { get; }

    public ValueTask AddAsync(ChunkId id, string scope, ChunkFingerprint fingerprint, CancellationToken ct = default)
    {
        var signature = fingerprint.MinHash.Span;
        CheckLength(signature);

        var payload = new byte[HeaderBytes + (4 * signature.Length)];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, fingerprint.GuardSignature);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), fingerprint.ContentSignature);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(16), fingerprint.IdentifierSignature);
        for (var i = 0; i < signature.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(HeaderBytes + (4 * i)), signature[i]);
        }

        return _storage.PutAsync(scope, BandKind.MinHash, id.Value, Seal(scope, id.Value, payload), Keys(scope, signature), ct);
    }

    public async ValueTask RemoveAsync(ChunkId id, string scope, CancellationToken ct = default)
    {
        var stored = await _storage.GetAsync(scope, BandKind.MinHash, id.Value, ct).ConfigureAwait(false);
        if (stored is null)
        {
            return;
        }

        var payload = Open(scope, id.Value, stored);
        await _storage.DeleteAsync(scope, BandKind.MinHash, id.Value, Keys(scope, ReadSignature(payload)), ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<NearDuplicateMatch>> FindAsync(
        ChunkFingerprint fingerprint, string scope, CancellationToken ct = default)
    {
        var signature = fingerprint.MinHash;
        CheckLength(signature.Span);

        var entries = await _storage.QueryAsync(scope, BandKind.MinHash, Keys(scope, signature.Span), ct).ConfigureAwait(false);
        var found = new List<NearDuplicateMatch>();

        foreach (var entry in entries)
        {
            var payload = Open(scope, entry.Id, entry.Payload);
            var jaccard = MinHasher.EstimateJaccard(signature.Span, ReadSignature(payload));
            if (jaccard >= Threshold)
            {
                var guard = BinaryPrimitives.ReadUInt64LittleEndian(payload);
                var content = payload.Length >= HeaderBytes ? BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(8)) : 0UL;
                var identifiers = payload.Length >= HeaderBytes ? BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(16)) : 0UL;
                found.Add(new NearDuplicateMatch(new ChunkId(entry.Id), MatchStage.MinHash, null, jaccard, guard, content, identifiers));
            }
        }

        return found
            .OrderByDescending(m => m.EstimatedJaccard)
            .ThenBy(m => m.Existing.Value, StringComparer.Ordinal)
            .ToList();
    }

    public async ValueTask<double?> EstimateJaccardAsync(
        string scope, ChunkId id, ReadOnlyMemory<uint> signature, CancellationToken ct = default)
    {
        CheckLength(signature.Span);
        var stored = await _storage.GetAsync(scope, BandKind.MinHash, id.Value, ct).ConfigureAwait(false);
        return stored is null ? null : MinHasher.EstimateJaccard(signature.Span, ReadSignature(Open(scope, id.Value, stored)));
    }

    private const int HeaderBytes = 24;

    private static uint[] ReadSignature(byte[] payload)
    {
        var signature = new uint[(payload.Length - HeaderBytes) / 4];
        for (var i = 0; i < signature.Length; i++)
        {
            signature[i] = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(HeaderBytes + (4 * i)));
        }

        return signature;
    }

    private byte[] Seal(string scope, string id, byte[] payload) =>
        _bands is null ? payload : _bands.Seal(scope, id, "minhash", payload);

    private byte[] Open(string scope, string id, byte[] payload) =>
        _bands is null ? payload : _bands.Open(scope, id, "minhash", payload);

    private BandKey[] Keys(string scope, ReadOnlySpan<uint> signature)
    {
        var keys = new BandKey[Lsh.Bands];
        for (var band = 0; band < keys.Length; band++)
        {
            var raw = Lsh.BandKey(signature, band);
            var stored = _bands is null ? raw : _bands.Protect(scope, BandKind.MinHash, band, raw);
            keys[band] = new BandKey(band, stored);
        }

        return keys;
    }

    private void CheckLength(ReadOnlySpan<uint> signature)
    {
        if (signature.Length != Permutations)
        {
            throw new ArgumentException($"The signature has {signature.Length} slots but the index expects {Permutations}.", nameof(signature));
        }
    }
}
