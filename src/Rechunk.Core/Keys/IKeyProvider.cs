namespace Rechunk;

/// <summary>
/// The current key for a scope. A missing key throws <see cref="KeyUnavailableException"/>.
/// Callers must not invent an unkeyed id when that happens.
/// </summary>
public interface IKeyProvider
{
    ValueTask<ScopeKey> GetRequiredAsync(string scope, CancellationToken ct = default);

    /// <summary>A key already unwrapped in this process. False when nothing is loaded.</summary>
    bool TryGetCached(string scope, out ScopeKey key);
}

/// <summary>Keyed work was requested and this process has no key for the scope.</summary>
public sealed class KeyUnavailableException : InvalidOperationException
{
    public KeyUnavailableException(string scope)
        : base("No key is available for scope '" + scope + "'. Create a scope key before use.")
    {
        Scope = scope ?? string.Empty;
    }

    public string Scope { get; }
}
