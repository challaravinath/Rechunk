namespace Rechunk;

/// <summary>
/// Keeps document manifests and chunk counts in this process. The default, good for tests and a single instance.
/// Use the SQLite or Redis package when more than one process revises documents.
/// </summary>
public sealed class InMemoryRevisionStore : IRevisionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Scope, string Document), DocumentManifest> _docs = new();
    private readonly Dictionary<(string Scope, string Chunk), Ref> _refs = new();
    private readonly Dictionary<(string Scope, string Chunk), HashSet<string>> _aliases = new();

    public ValueTask<DocumentManifest?> GetAsync(string scope, DocumentId document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_docs.GetValueOrDefault((scope, document.Value)));
        }
    }

    public ValueTask<CommitRevisionResult> CommitAsync(CommitRevisionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        RevisionRules.Check(request);

        lock (_gate)
        {
            _docs.TryGetValue((request.Scope, request.Document.Value), out var current);
            var currentRevision = current?.Revision ?? 0;
            if (currentRevision != request.ExpectedRevision)
            {
                return ValueTask.FromResult(new CommitRevisionResult(
                    CommitRevisionStatus.Conflict, currentRevision, Array.Empty<ChunkId>(), Array.Empty<ChunkId>()));
            }

            var (added, removed) = RevisionRules.Diff(current?.Entries, request.Entries);

            var blocking = added
                .Where(t => _refs.TryGetValue((request.Scope, t), out var r) && r.State == ChunkRefState.Retiring)
                .Select(t => new ChunkId(t))
                .ToList();
            if (blocking.Count > 0)
            {
                return ValueTask.FromResult(new CommitRevisionResult(
                    CommitRevisionStatus.Blocked, currentRevision, Array.Empty<ChunkId>(), blocking));
            }

            foreach (var target in added)
            {
                var key = (request.Scope, target);
                if (_refs.TryGetValue(key, out var r))
                {
                    r.Count++;
                    r.State = ChunkRefState.Active;
                }
                else
                {
                    _refs[key] = new Ref { Count = 1, State = ChunkRefState.Active };
                }
            }

            var orphaned = new List<ChunkId>();
            foreach (var target in removed)
            {
                if (_refs.TryGetValue((request.Scope, target), out var r) && r.Count > 0)
                {
                    r.Count--;
                    if (r.Count == 0)
                    {
                        r.State = ChunkRefState.Orphaned;
                        orphaned.Add(new ChunkId(target));
                    }
                }
            }

            foreach (var entry in request.Entries.Where(e => e.Id != e.Target))
            {
                var key = (request.Scope, entry.Target.Value);
                if (!_aliases.TryGetValue(key, out var set))
                {
                    _aliases[key] = set = new HashSet<string>(StringComparer.Ordinal);
                }

                set.Add(entry.Id.Value);
            }

            var revision = currentRevision + 1;
            _docs[(request.Scope, request.Document.Value)] = new DocumentManifest(
                request.Scope, request.Document, revision, request.Entries.ToList(), request.At);

            return ValueTask.FromResult(new CommitRevisionResult(
                CommitRevisionStatus.Committed, revision, orphaned, Array.Empty<ChunkId>()));
        }
    }

    public ValueTask<IReadOnlyList<ChunkId>> ListPendingAsync(string scope, DateTimeOffset now, int take, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<ChunkId> found = _refs
                .Where(p => p.Key.Scope == scope && p.Value.Count == 0
                    && (p.Value.State == ChunkRefState.Orphaned
                        || (p.Value.State == ChunkRefState.Retiring && p.Value.LeaseUntil <= now)))
                .Select(p => p.Key.Chunk)
                .OrderBy(c => c, StringComparer.Ordinal)
                .Take(take)
                .Select(c => new ChunkId(c))
                .ToList();
            return ValueTask.FromResult(found);
        }
    }

    public ValueTask<RetireClaim?> BeginRetireAsync(
        string scope, ChunkId chunk, string holder, TimeSpan lease, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(holder);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = (scope, chunk.Value);
            if (!_refs.TryGetValue(key, out var r) || r.Count != 0)
            {
                return ValueTask.FromResult<RetireClaim?>(null);
            }

            var free = r.State == ChunkRefState.Orphaned
                || (r.State == ChunkRefState.Retiring && (r.Holder == holder || r.LeaseUntil <= now));
            if (!free)
            {
                return ValueTask.FromResult<RetireClaim?>(null);
            }

            r.State = ChunkRefState.Retiring;
            r.Holder = holder;
            r.LeaseUntil = now + lease;
            var aliases = _aliases.TryGetValue(key, out var set)
                ? set.OrderBy(a => a, StringComparer.Ordinal).Select(a => new ChunkId(a)).ToList()
                : new List<ChunkId>();
            return ValueTask.FromResult<RetireClaim?>(new RetireClaim(chunk, aliases));
        }
    }

    public ValueTask<bool> FinishRetireAsync(string scope, ChunkId chunk, string holder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(holder);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = (scope, chunk.Value);
            if (_refs.TryGetValue(key, out var r) && r.Count == 0 && r.State == ChunkRefState.Retiring && r.Holder == holder)
            {
                _refs.Remove(key);
                _aliases.Remove(key);
                return ValueTask.FromResult(true);
            }

            return ValueTask.FromResult(false);
        }
    }

    public ValueTask<ChunkRef?> GetRefAsync(string scope, ChunkId chunk, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ChunkRef? found = _refs.TryGetValue((scope, chunk.Value), out var r) ? new ChunkRef(chunk, r.Count, r.State) : null;
            return ValueTask.FromResult(found);
        }
    }

    public ValueTask<IReadOnlyList<ChunkRef>> ListRefsAsync(string scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<ChunkRef> found = _refs
                .Where(p => p.Key.Scope == scope)
                .OrderBy(p => p.Key.Chunk, StringComparer.Ordinal)
                .Select(p => new ChunkRef(new ChunkId(p.Key.Chunk), p.Value.Count, p.Value.State))
                .ToList();
            return ValueTask.FromResult(found);
        }
    }

    private sealed class Ref
    {
        public int Count;
        public ChunkRefState State;
        public string Holder = string.Empty;
        public DateTimeOffset LeaseUntil;
    }
}

/// <summary>The small rules every revision store follows, written once. Public so a store of your own can use them too.</summary>
public static class RevisionRules
{
    /// <summary>Throws when the request can not be valid, whatever the state of the store.</summary>
    public static void Check(CommitRevisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Scope);
        ArgumentNullException.ThrowIfNull(request.Entries);
        if (request.ExpectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "ExpectedRevision can not be negative.");
        }
    }

    /// <summary>The chunks a document starts using and the ones it stops using. A chunk counts once per document.</summary>
    public static (List<string> Added, List<string> Removed) Diff(
        IReadOnlyList<ManifestEntry>? before, IReadOnlyList<ManifestEntry> after)
    {
        var oldTargets = new HashSet<string>((before ?? Array.Empty<ManifestEntry>()).Select(e => e.Target.Value), StringComparer.Ordinal);
        var newTargets = new HashSet<string>(after.Select(e => e.Target.Value), StringComparer.Ordinal);
        var added = newTargets.Where(t => !oldTargets.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToList();
        var removed = oldTargets.Where(t => !newTargets.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToList();
        return (added, removed);
    }
}
