namespace Rechunk;

/// <summary>
/// How a 64 bit SimHash is cut into slices for the band index: k + 1 slices for k allowed bit differences.
/// The in memory index and the shared stores use this same class, so they always file a hash the same way.
/// </summary>
public sealed class SimHashBandLayout
{
    private readonly int[] _shifts;
    private readonly ulong[] _masks;

    public SimHashBandLayout(int maxHammingDistance)
    {
        if (maxHammingDistance is < 0 or > 63)
        {
            throw new ArgumentOutOfRangeException(nameof(maxHammingDistance), "Has to be between 0 and 63.");
        }

        MaxHammingDistance = maxHammingDistance;
        BandCount = maxHammingDistance + 1;

        // 64 bits over BandCount slices. When it does not divide evenly the first slices get one bit more.
        _shifts = new int[BandCount];
        _masks = new ulong[BandCount];
        var baseWidth = 64 / BandCount;
        var wider = 64 % BandCount;
        var shift = 0;
        for (var i = 0; i < BandCount; i++)
        {
            var width = baseWidth + (i < wider ? 1 : 0);
            _shifts[i] = shift;
            _masks[i] = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
            shift += width;
        }
    }

    public int MaxHammingDistance { get; }

    public int BandCount { get; }

    public ulong Slice(ulong hash, int band) => (hash >> _shifts[band]) & _masks[band];
}
