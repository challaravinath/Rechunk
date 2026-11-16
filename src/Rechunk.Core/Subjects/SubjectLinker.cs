namespace Rechunk;

/// <summary>One chunk whose subjects should be written onto the chunk id that was stored.</summary>
internal readonly record struct SubjectLink(ChunkId Stored, ChunkInput Input, bool Replace);

/// <summary>
/// Writes subject links for one batch. Replace drops the previous links for that chunk.
/// A duplicate that points at another chunk only adds, so the stored chunk keeps the subjects it already had.
/// </summary>
internal static class SubjectLinker
{
    public static async ValueTask ApplyAsync(ISubjectIndex index, string scope, ScopeKey key, IReadOnlyList<SubjectLink> links, CancellationToken ct)
    {
        var buckets = new Dictionary<string, Bucket>(StringComparer.Ordinal);
        foreach (var link in links)
        {
            if (string.IsNullOrEmpty(link.Stored.Value))
            {
                continue;
            }

            if (!buckets.TryGetValue(link.Stored.Value, out var bucket))
            {
                bucket = new Bucket(link.Stored);
                buckets[link.Stored.Value] = bucket;
            }

            bucket.Replace |= link.Replace;
            foreach (var subject in SubjectIds.KeyAll(link.Input.Subjects, key))
            {
                bucket.Subjects[subject.Id] = subject;
            }
        }

        foreach (var bucket in buckets.Values)
        {
            var subjects = bucket.Subjects.Values.ToList();
            if (bucket.Replace)
            {
                await index.LinkAsync(scope, bucket.Chunk, subjects, ct).ConfigureAwait(false);
            }
            else if (subjects.Count > 0)
            {
                await index.AddAsync(scope, bucket.Chunk, subjects, ct).ConfigureAwait(false);
            }
        }
    }

    private sealed class Bucket(ChunkId chunk)
    {
        public ChunkId Chunk { get; } = chunk;

        public bool Replace { get; set; }

        public Dictionary<string, SubjectKey> Subjects { get; } = new(StringComparer.Ordinal);
    }
}
