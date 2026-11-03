namespace Rechunk;

/// <summary>Id of one chunk. For now it is built from the exact hash of the text.</summary>
public readonly record struct ChunkId(string Value)
{
    // TODO: decide if ids should stay content based or become stable per document and position
    public static ChunkId FromExactHash(ReadOnlySpan<byte> exactHash) =>
        new(Convert.ToHexString(exactHash).ToLowerInvariant());

    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Id of a source document, chosen by the caller.</summary>
public readonly record struct DocumentId(string Value)
{
    public override string ToString() => Value ?? string.Empty;
}
