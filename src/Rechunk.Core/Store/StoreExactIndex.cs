namespace Rechunk;

/// <summary>
/// The IExactHashIndex view of a fingerprint store. Find only sees committed hashes. AddIfAbsent is a claim
/// and a commit in one step, for callers that do not need the pending state.
/// </summary>
internal sealed class StoreExactIndex : IExactHashIndex
{
    private static readonly TimeSpan DirectLease = TimeSpan.FromMinutes(1);

    private readonly IFingerprintStore _store;
    private readonly Func<string, ReadOnlyMemory<byte>, CancellationToken, ValueTask<ChunkId?>> _find;
    private readonly Func<string, ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> _remove;

    public StoreExactIndex(
        IFingerprintStore store,
        Func<string, ReadOnlyMemory<byte>, CancellationToken, ValueTask<ChunkId?>> find,
        Func<string, ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> remove)
    {
        _store = store;
        _find = find;
        _remove = remove;
    }

    public ValueTask<ChunkId?> FindAsync(string scope, ReadOnlyMemory<byte> exactHash, CancellationToken ct = default) =>
        _find(scope, exactHash, ct);

    public async ValueTask<ExactAddResult> AddIfAbsentAsync(
        string scope, ReadOnlyMemory<byte> exactHash, ChunkId id, CancellationToken ct = default)
    {
        var holder = "direct-" + Guid.NewGuid().ToString("N");
        var claim = await _store.ClaimAsync(new ClaimRequest(scope, id, exactHash, holder, DirectLease), ct).ConfigureAwait(false);
        if (!claim.Won)
        {
            return new ExactAddResult(claim.Owner, false);
        }

        var commit = await _store.CommitAsync(new CommitRequest(scope, id, exactHash, holder), ct).ConfigureAwait(false);
        return commit == CommitStatus.NotHeld
            ? new ExactAddResult(claim.Owner, false)
            : new ExactAddResult(id, true);
    }

    public ValueTask<bool> RemoveAsync(string scope, ReadOnlyMemory<byte> exactHash, CancellationToken ct = default) =>
        _remove(scope, exactHash, ct);
}
