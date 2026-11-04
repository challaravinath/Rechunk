using System.Buffers.Binary;

namespace Rechunk;

/// <summary>
/// The MinHash LSH index on top of a database. Bands and rows are worked out the same way as in MinHashLshIndex.
/// A lookup asks the storage for the chunks that share a band, then checks the full signatures here.
/// </summary>
public sealed class BandedMinHashIndex : IMinHashIndex
{
    private readonly IBandStorage _storage;

    public BandedMinHashIndex(IBandStorage storage, MinHashOptions options)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        ArgumentNullException.ThrowIfNull(options);

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

        var payload = new byte[8 + (4 * signature.Length)];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, fingerprint.GuardSignature);
        for (var i = 0; i < signature.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8 + (4 * i)), signature[i]);
        }

        return _storage.PutAsync(scope, BandKind.MinHash, id.Value, payload, Keys(signature), ct);
    }

    public async ValueTask RemoveAsync(ChunkId id, string scope, CancellationToken ct = default)
    {
        var payload = await _storage.GetAsync(scope, BandKind.MinHash, id.Value, ct).ConfigureAwait(false);
        if (payload is null)
        {
            return;
        }

        await _storage.DeleteAsync(scope, BandKind.MinHash, id.Value, Keys(ReadSignature(payload)), ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<NearDuplicateMatch>> FindAsync(
        ChunkFingerprint fingerprint, string scope, CancellationToken ct = default)
    {
        var signature = fingerprint.MinHash;
        CheckLength(signature.Span);

        var entries = await _storage.QueryAsync(scope, BandKind.MinHash, Keys(signature.Span), ct).ConfigureAwait(false);
        var found = new List<NearDuplicateMatch>();

        foreach (var entry in entries)
        {
            var jaccard = MinHasher.EstimateJaccard(signature.Span, ReadSignature(entry.Payload));
            if (jaccard >= Threshold)
            {
                var guard = BinaryPrimitives.ReadUInt64LittleEndian(entry.Payload);
                found.Add(new NearDuplicateMatch(new ChunkId(entry.Id), MatchStage.MinHash, null, jaccard, guard));
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
        var payload = await _storage.GetAsync(scope, BandKind.MinHash, id.Value, ct).ConfigureAwait(false);
        return payload is null ? null : MinHasher.EstimateJaccard(signature.Span, ReadSignature(payload));
    }

    private static uint[] ReadSignature(byte[] payload)
    {
        var signature = new uint[(payload.Length - 8) / 4];
        for (var i = 0; i < signature.Length; i++)
        {
            signature[i] = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8 + (4 * i)));
        }

        return signature;
    }

    private BandKey[] Keys(ReadOnlySpan<uint> signature)
    {
        var keys = new BandKey[Lsh.Bands];
        for (var band = 0; band < keys.Length; band++)
        {
            keys[band] = new BandKey(band, Lsh.BandKey(signature, band));
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
