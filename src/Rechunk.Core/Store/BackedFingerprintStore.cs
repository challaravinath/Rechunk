namespace Rechunk;

/// <summary>
/// A shared store built on a backend (SQLite, Redis or your own). The exact hash rows and the band storage live
/// in the backend. The SimHash and MinHash logic runs here, so every backend behaves the same way.
/// </summary>
public class BackedFingerprintStore : IFingerprintStore
{
    private readonly IFingerprintBackend _backend;
    private readonly TimeProvider _time;
    private readonly BandedSimHashIndex _simHash;
    private readonly BandedMinHashIndex _minHash;

    public BackedFingerprintStore(IFingerprintBackend backend, RechunkOptions? options = null, TimeProvider? time = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        options ??= new RechunkOptions();
        _time = time ?? TimeProvider.System;

        _simHash = new BandedSimHashIndex(backend, options.SimHash.MaxHammingDistance);
        _minHash = new BandedMinHashIndex(backend, options.MinHash);
        Exact = new StoreExactIndex(this, backend.FindCommittedAsync, backend.RemoveAsync);
    }

    public IExactHashIndex Exact { get; }

    public INearDuplicateIndex SimHash => _simHash;

    public IMinHashIndex MinHash => _minHash;

    public ValueTask<ClaimResult> ClaimAsync(ClaimRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _backend.ClaimAsync(request, _time.GetUtcNow(), ct);
    }

    public async ValueTask<CommitStatus> CommitAsync(CommitRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var status = await _backend.CommitAsync(request, ct).ConfigureAwait(false);

        // The hash is final first, the near indexes follow. Running this again repairs a half finished commit.
        if (status != CommitStatus.NotHeld && request.AliasOf is null && request.Fingerprint is { } fingerprint)
        {
            if (fingerprint.HasSimHash)
            {
                await _simHash.AddAsync(request.Id, request.Scope, fingerprint, ct).ConfigureAwait(false);
            }

            if (fingerprint.HasMinHash)
            {
                await _minHash.AddAsync(request.Id, request.Scope, fingerprint, ct).ConfigureAwait(false);
            }
        }

        return status;
    }

    public ValueTask<bool> ReleaseAsync(ReleaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _backend.ReleaseAsync(request, ct);
    }
}
