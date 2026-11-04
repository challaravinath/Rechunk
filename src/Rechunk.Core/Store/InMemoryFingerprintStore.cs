namespace Rechunk;

/// <summary>
/// The default store. It keeps everything in this process, using the same in memory indexes the pipeline
/// uses on its own. Good for tests, a single instance and local runs. It does nothing for workers in different
/// processes, use one of the storage packages for that.
/// </summary>
public sealed class InMemoryFingerprintStore : IFingerprintStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Scope, string Hash), Row> _rows = new();
    private readonly TimeProvider _time;

    public InMemoryFingerprintStore(RechunkOptions? options = null, TimeProvider? time = null)
    {
        options ??= new RechunkOptions();
        _time = time ?? TimeProvider.System;

        SimHashIndex = new SimHashBandIndex(options.SimHash.MaxHammingDistance);
        MinHashIndex = new MinHashLshIndex(options.MinHash);
        Exact = new StoreExactIndex(this, FindCommitted, RemoveRow);
    }

    public IExactHashIndex Exact { get; }

    public INearDuplicateIndex SimHash => SimHashIndex;

    public IMinHashIndex MinHash => MinHashIndex;

    public SimHashBandIndex SimHashIndex { get; }

    public MinHashLshIndex MinHashIndex { get; }

    public ValueTask<ClaimResult> ClaimAsync(ClaimRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var now = _time.GetUtcNow();
        var until = now + request.Lease;
        var key = Key(request.Scope, request.ExactHash);

        lock (_gate)
        {
            if (!_rows.TryGetValue(key, out var row))
            {
                _rows[key] = new Row(request.Id, false, request.Holder, until);
                return ValueTask.FromResult(new ClaimResult(ClaimStatus.Claimed, request.Id, until));
            }

            if (row.Committed)
            {
                return ValueTask.FromResult(new ClaimResult(ClaimStatus.AlreadyCommitted, row.Chunk));
            }

            if (row.Holder == request.Holder && row.Chunk == request.Id)
            {
                _rows[key] = row with { LeaseUntil = until };
                return ValueTask.FromResult(new ClaimResult(ClaimStatus.Renewed, request.Id, until));
            }

            if (row.LeaseUntil <= now)
            {
                _rows[key] = new Row(request.Id, false, request.Holder, until);
                return ValueTask.FromResult(new ClaimResult(ClaimStatus.TakenOver, request.Id, until));
            }

            return ValueTask.FromResult(new ClaimResult(ClaimStatus.InFlight, row.Chunk, row.LeaseUntil));
        }
    }

    public async ValueTask<CommitStatus> CommitAsync(CommitRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var owner = request.AliasOf ?? request.Id;
        var key = Key(request.Scope, request.ExactHash);
        CommitStatus status;

        lock (_gate)
        {
            if (!_rows.TryGetValue(key, out var row))
            {
                status = CommitStatus.NotHeld;
            }
            else if (row.Committed)
            {
                status = row.Chunk == owner ? CommitStatus.AlreadyCommitted : CommitStatus.NotHeld;
            }
            else if (row.Holder == request.Holder && row.Chunk == request.Id)
            {
                _rows[key] = new Row(owner, true, string.Empty, default);
                status = CommitStatus.Committed;
            }
            else
            {
                status = CommitStatus.NotHeld;
            }
        }

        // The near indexes are written after the hash is final. Doing it again on a repeated commit repairs a
        // run that stopped half way, and adding the same id twice just replaces the entry.
        if (status != CommitStatus.NotHeld && request.AliasOf is null && request.Fingerprint is { } fingerprint)
        {
            if (fingerprint.HasSimHash)
            {
                await SimHashIndex.AddAsync(request.Id, request.Scope, fingerprint, ct).ConfigureAwait(false);
            }

            if (fingerprint.HasMinHash)
            {
                await MinHashIndex.AddAsync(request.Id, request.Scope, fingerprint, ct).ConfigureAwait(false);
            }
        }

        return status;
    }

    public ValueTask<bool> ReleaseAsync(ReleaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var key = Key(request.Scope, request.ExactHash);

        lock (_gate)
        {
            if (_rows.TryGetValue(key, out var row)
                && !row.Committed
                && row.Holder == request.Holder
                && row.Chunk == request.Id)
            {
                _rows.Remove(key);
                return ValueTask.FromResult(true);
            }
        }

        return ValueTask.FromResult(false);
    }

    private ValueTask<ChunkId?> FindCommitted(string scope, ReadOnlyMemory<byte> hash, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ChunkId? found = _rows.TryGetValue(Key(scope, hash), out var row) && row.Committed ? row.Chunk : null;
            return ValueTask.FromResult(found);
        }
    }

    private ValueTask<bool> RemoveRow(string scope, ReadOnlyMemory<byte> hash, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_rows.Remove(Key(scope, hash)));
        }
    }

    private static (string, string) Key(string scope, ReadOnlyMemory<byte> hash) =>
        (scope, Convert.ToHexString(hash.Span));

    private sealed record Row(ChunkId Chunk, bool Committed, string Holder, DateTimeOffset LeaseUntil);
}
