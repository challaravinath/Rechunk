using System.Text;

namespace Rechunk;

/// <summary>Subject links for this process only. They go away when the process exits.</summary>
public sealed class InMemorySubjectIndex : ISubjectIndex
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, SubjectKey>>> _byChunk = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, HashSet<string>>> _bySubject = new(StringComparer.Ordinal);

    public ValueTask LinkAsync(string scope, ChunkId chunk, IReadOnlyList<SubjectKey> subjects, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Clear(scope, chunk.Value);
            AddCore(scope, chunk.Value, subjects);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask AddAsync(string scope, ChunkId chunk, IReadOnlyList<SubjectKey> subjects, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            AddCore(scope, chunk.Value, subjects);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string scope, ChunkId chunk, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Clear(scope, chunk.Value);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<ChunkId>> ListChunksAsync(string scope, SubjectKey subject, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_bySubject.TryGetValue(ScopeOf(scope), out var subjects) || !subjects.TryGetValue(subject.Id, out var chunks))
            {
                return ValueTask.FromResult<IReadOnlyList<ChunkId>>(Array.Empty<ChunkId>());
            }

            var list = chunks.OrderBy(id => id, StringComparer.Ordinal).Select(id => new ChunkId(id)).ToList();
            return ValueTask.FromResult<IReadOnlyList<ChunkId>>(list);
        }
    }

    public ValueTask<int> CountAsync(string scope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_byChunk.TryGetValue(ScopeOf(scope), out var chunks))
            {
                return ValueTask.FromResult(0);
            }

            var count = 0;
            foreach (var links in chunks.Values)
            {
                count += links.Count;
            }

            return ValueTask.FromResult(count);
        }
    }

    public ValueTask<int> CountAsync(string scope, SubjectKey subject, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_bySubject.TryGetValue(ScopeOf(scope), out var subjects) || !subjects.TryGetValue(subject.Id, out var chunks))
            {
                return ValueTask.FromResult(0);
            }

            return ValueTask.FromResult(chunks.Count);
        }
    }

    private void AddCore(string scope, string chunk, IReadOnlyList<SubjectKey> subjects)
    {
        if (subjects is null || subjects.Count == 0 || string.IsNullOrEmpty(chunk))
        {
            return;
        }

        var scopeKey = ScopeOf(scope);
        if (!_byChunk.TryGetValue(scopeKey, out var chunks))
        {
            chunks = new Dictionary<string, Dictionary<string, SubjectKey>>(StringComparer.Ordinal);
            _byChunk[scopeKey] = chunks;
        }

        if (!chunks.TryGetValue(chunk, out var links))
        {
            links = new Dictionary<string, SubjectKey>(StringComparer.Ordinal);
            chunks[chunk] = links;
        }

        if (!_bySubject.TryGetValue(scopeKey, out var reverse))
        {
            reverse = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            _bySubject[scopeKey] = reverse;
        }

        foreach (var subject in subjects)
        {
            links[subject.Id] = subject;
            if (!reverse.TryGetValue(subject.Id, out var owners))
            {
                owners = new HashSet<string>(StringComparer.Ordinal);
                reverse[subject.Id] = owners;
            }

            owners.Add(chunk);
        }
    }

    private void Clear(string scope, string chunk)
    {
        var scopeKey = ScopeOf(scope);
        if (string.IsNullOrEmpty(chunk) || !_byChunk.TryGetValue(scopeKey, out var chunks) || !chunks.TryGetValue(chunk, out var links))
        {
            return;
        }

        if (_bySubject.TryGetValue(scopeKey, out var reverse))
        {
            foreach (var id in links.Keys)
            {
                if (reverse.TryGetValue(id, out var owners))
                {
                    owners.Remove(chunk);
                    if (owners.Count == 0)
                    {
                        reverse.Remove(id);
                    }
                }
            }
        }

        chunks.Remove(chunk);
    }

    internal string StoredText()
    {
        lock (_gate)
        {
            var text = new StringBuilder();
            foreach (var scope in _byChunk)
            {
                foreach (var chunk in scope.Value)
                {
                    foreach (var subject in chunk.Value.Values)
                    {
                        text.Append(scope.Key).Append(' ').Append(chunk.Key).Append(' ').Append(subject.Kind).Append(' ').Append(subject.Id).Append('\n');
                    }
                }
            }

            return text.ToString();
        }
    }

    private static string ScopeOf(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException("Scope cannot be empty.", nameof(scope));
        }

        return scope;
    }
}
