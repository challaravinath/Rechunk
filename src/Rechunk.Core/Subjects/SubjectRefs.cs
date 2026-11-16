namespace Rechunk;

/// <summary>Joins subject lists without dropping either side. Used when a request and a chunk, or a scan and a caller, both name people.</summary>
internal static class SubjectRefs
{
    public static IReadOnlyList<ChunkInput> MergeOnto(IReadOnlyList<ChunkInput> chunks, IReadOnlyList<SubjectRef>? extra)
    {
        if (extra is null || extra.Count == 0)
        {
            return chunks;
        }

        var merged = new ChunkInput[chunks.Count];
        for (var i = 0; i < chunks.Count; i++)
        {
            merged[i] = chunks[i] with { Subjects = Merge(chunks[i].Subjects, extra) };
        }

        return merged;
    }

    public static IReadOnlyList<SubjectRef>? Merge(IReadOnlyList<SubjectRef>? left, IReadOnlyList<SubjectRef>? right)
    {
        if (right is null || right.Count == 0)
        {
            return left;
        }

        if (left is null || left.Count == 0)
        {
            return right;
        }

        var list = new List<SubjectRef>(left.Count + right.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subject in left)
        {
            Add(list, seen, subject);
        }

        foreach (var subject in right)
        {
            Add(list, seen, subject);
        }

        return list;
    }

    private static void Add(List<SubjectRef> list, HashSet<string> seen, SubjectRef subject)
    {
        if (string.IsNullOrWhiteSpace(subject.Kind) || string.IsNullOrEmpty(subject.Value))
        {
            return;
        }

        if (seen.Add(subject.Kind.Trim().ToLowerInvariant() + "\n" + subject.Value))
        {
            list.Add(subject);
        }
    }
}
