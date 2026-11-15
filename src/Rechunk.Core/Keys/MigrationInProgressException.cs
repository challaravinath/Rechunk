namespace Rechunk;

/// <summary>
/// A keyed-id migration is in progress for this scope. Claims, commits, releases, deletes and revision
/// commits are refused until it finishes. The scope version stays at 1 until the last step, then becomes 2.
/// </summary>
public sealed class MigrationInProgressException : InvalidOperationException
{
    public MigrationInProgressException(string scope)
        : base("Scope '" + scope + "' is being migrated to keyed ids. Writes are refused until that finishes.")
    {
        Scope = scope ?? string.Empty;
    }

    public string Scope { get; }
}
