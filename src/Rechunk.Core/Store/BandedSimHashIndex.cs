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

    public BandedSimHashIndex(IBandStorage storage, int maxHammingDistance = 3)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _layout = new SimHashBandLayout(maxHammingDistance);
    }

    public int MaxHammingDistance => _layout.MaxHammingDistance;

    public ValueTask AddAsync(ChunkId id, string scope, ChunkFingerprint fingerprint, CancellationToken ct = default)
    {
        var payload = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, fingerprint.SimHash);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), fingerprint.GuardSignature);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(16), fingerprint.ContentSignature);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(24), fingerprint.IdentifierSignature);
        return _storage.PutAsync(scope, BandKind.SimHash, id.Value, payload, Keys(fingerprint.SimHash), ct);
    }

    public async ValueTask RemoveAsync(ChunkId id, string scope, CancellationToken ct = default)
    {
        var payload = await _storage.GetAsync(scope, BandKind.SimHash, id.Value, ct).ConfigureAwait(false);
        if (payload is null)
        {
            return;
        }

        var simHash = BinaryPrimitives.ReadUInt64LittleEndian(payload);
        await _storage.DeleteAsync(scope, BandKind.SimHash, id.Value, Keys(simHash), ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<NearDuplicateMatch>> FindAsync(
        ChunkFingerprint fingerprint, string scope, CancellationToken ct = default)
    {
        var entries = await _storage.QueryAsync(scope, BandKind.SimHash, Keys(fingerprint.SimHash), ct).ConfigureAwait(false);
        var found = new List<NearDuplicateMatch>();

        foreach (var entry in entries)
        {
            var other = BinaryPrimitives.ReadUInt64LittleEndian(entry.Payload);
            var guard = BinaryPrimitives.ReadUInt64LittleEndian(entry.Payload.AsSpan(8));
            var content = entry.Payload.Length >= 32 ? BinaryPrimitives.ReadUInt64LittleEndian(entry.Payload.AsSpan(16)) : 0UL;
            var identifiers = entry.Payload.Length >= 32 ? BinaryPrimitives.ReadUInt64LittleEndian(entry.Payload.AsSpan(24)) : 0UL;
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

    private BandKey[] Keys(ulong simHash)
    {
        var keys = new BandKey[_layout.BandCount];
        for (var band = 0; band < keys.Length; band++)
        {
            keys[band] = new BandKey(band, _layout.Slice(simHash, band));
        }

        return keys;
    }
}
