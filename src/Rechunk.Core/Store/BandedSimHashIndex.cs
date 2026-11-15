using System.Buffers.Binary;
using System.Numerics;

namespace Rechunk;

/// <summary>
/// The SimHash band index on top of a database. The slicing is the same as in SimHashBandIndex (it uses the same
/// layout class), only the buckets live in the storage so every worker sees the same ones.
/// </summary>
public sealed class BandedSimHashIndex : INearDuplicateIndex
{
    private readonly IBandStorage _storage;
    private readonly SimHashBandLayout _layout;
    private readonly BandKeyDirectory? _bands;

    public BandedSimHashIndex(IBandStorage storage, int maxHammingDistance = 3)
        : this(storage, maxHammingDistance, null)
    {
    }

    internal BandedSimHashIndex(IBandStorage storage, int maxHammingDistance, BandKeyDirectory? bands)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _layout = new SimHashBandLayout(maxHammingDistance);
        _bands = bands;
    }

    public int MaxHammingDistance => _layout.MaxHammingDistance;

    public ValueTask AddAsync(ChunkId id, string scope, ChunkFingerprint fingerprint, CancellationToken ct = default)
    {
        var payload = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, fingerprint.SimHash);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), fingerprint.GuardSignature);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(16), fingerprint.ContentSignature);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(24), fingerprint.IdentifierSignature);
        return _storage.PutAsync(scope, BandKind.SimHash, id.Value, Seal(scope, id.Value, payload), Keys(scope, fingerprint.SimHash), ct);
    }

    public async ValueTask RemoveAsync(ChunkId id, string scope, CancellationToken ct = default)
    {
        var stored = await _storage.GetAsync(scope, BandKind.SimHash, id.Value, ct).ConfigureAwait(false);
        if (stored is null)
        {
            return;
        }

        var payload = Open(scope, id.Value, stored);
        var simHash = BinaryPrimitives.ReadUInt64LittleEndian(payload);
        await _storage.DeleteAsync(scope, BandKind.SimHash, id.Value, Keys(scope, simHash), ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<NearDuplicateMatch>> FindAsync(
        ChunkFingerprint fingerprint, string scope, CancellationToken ct = default)
    {
        var entries = await _storage.QueryAsync(scope, BandKind.SimHash, Keys(scope, fingerprint.SimHash), ct).ConfigureAwait(false);
        var found = new List<NearDuplicateMatch>();

        foreach (var entry in entries)
        {
            var payload = Open(scope, entry.Id, entry.Payload);
            var other = BinaryPrimitives.ReadUInt64LittleEndian(payload);
            var guard = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(8));
            var content = payload.Length >= 32 ? BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(16)) : 0UL;
            var identifiers = payload.Length >= 32 ? BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(24)) : 0UL;
            var distance = BitOperations.PopCount(other ^ fingerprint.SimHash);
            if (distance <= MaxHammingDistance)
            {
                found.Add(new NearDuplicateMatch(new ChunkId(entry.Id), MatchStage.SimHash, distance, null, guard, content, identifiers));
            }
        }

        return found
            .OrderBy(m => m.HammingDistance)
            .ThenBy(m => m.Existing.Value, StringComparer.Ordinal)
            .ToList();
    }

    private BandKey[] Keys(string scope, ulong simHash)
    {
        var keys = new BandKey[_layout.BandCount];
        for (var band = 0; band < keys.Length; band++)
        {
            var raw = _layout.Slice(simHash, band);
            var stored = _bands is null ? raw : _bands.Protect(scope, BandKind.SimHash, band, raw);
            keys[band] = new BandKey(band, stored);
        }

        return keys;
    }

    private byte[] Seal(string scope, string id, byte[] payload) =>
        _bands is null ? payload : _bands.Seal(scope, id, "simhash", payload);

    private byte[] Open(string scope, string id, byte[] payload) =>
        _bands is null ? payload : _bands.Open(scope, id, "simhash", payload);
}
