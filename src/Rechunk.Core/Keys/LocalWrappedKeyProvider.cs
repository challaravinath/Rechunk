using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;

namespace Rechunk;

/// <summary>
/// Random 32 byte key per scope, wrapped with AES-GCM under a key the caller supplies.
/// Scope keys are not derived from the wrapping key or from each other.
/// The wrapping key is copied into this object and cleared on <see cref="Dispose"/>. It is not written to the store.
/// Two callers that create the same missing scope share the first key that was saved.
/// </summary>
public sealed class LocalWrappedKeyProvider : IKeyProvider, IDisposable
{
    private readonly byte[] _wrappingKey;
    private readonly IWrappedKeyStore _store;
    private readonly object _gate = new();
    private readonly Dictionary<string, ScopeKey> _cache = new(StringComparer.Ordinal);
    private bool _disposed;

    public LocalWrappedKeyProvider(ReadOnlyMemory<byte> wrappingKey, IWrappedKeyStore store)
    {
        if (wrappingKey.Length != ScopeKey.MaterialLength)
        {
            throw new ArgumentException("The wrapping key has to be 32 bytes.", nameof(wrappingKey));
        }

        ArgumentNullException.ThrowIfNull(store);
        _wrappingKey = wrappingKey.ToArray();
        _store = store;
    }

    /// <summary>
    /// The current key, or a new random key at version 1 when the scope has none.
    /// Calling it again returns the same key.
    /// </summary>
    public async ValueTask<ScopeKey> CreateAsync(string scope, CancellationToken ct = default)
    {
        RequireScope(scope);
        ThrowIfDisposed();
        if (TryGetCached(scope, out var cached))
        {
            return cached;
        }

        var existing = await ReadAsync(scope, ct).ConfigureAwait(false);
        if (existing is { } found)
        {
            return found;
        }

        ThrowIfDisposed();
        var material = new byte[ScopeKey.MaterialLength];
        RandomNumberGenerator.Fill(material);
        try
        {
            var record = Wrap(scope, 1, material);
            if (!await _store.TryAddAsync(record, ct).ConfigureAwait(false))
            {
                return await GetRequiredAsync(scope, ct).ConfigureAwait(false);
            }

            // ScopeKey copies the bytes. The temporary can be cleared.
            var key = new ScopeKey(1, material);
            Remember(scope, key);
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    public async ValueTask<ScopeKey> GetRequiredAsync(string scope, CancellationToken ct = default)
    {
        RequireScope(scope);
        if (TryGetCached(scope, out var cached))
        {
            return cached;
        }

        ThrowIfDisposed();
        var loaded = await ReadAsync(scope, ct).ConfigureAwait(false);
        if (loaded is not { } key)
        {
            ThrowIfDisposed();
            throw new KeyUnavailableException(scope);
        }

        return key;
    }

    public bool TryGetCached(string scope, out ScopeKey key)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                key = default;
                return false;
            }

            return _cache.TryGetValue(scope, out key);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cache.Clear();
            // Only the wrapping key belongs solely to this object. Scope keys already returned
            // share their bytes with the caller, so clearing them here would wipe a live key
            // and leave the HMAC schedule behind.
            CryptographicOperations.ZeroMemory(_wrappingKey);
        }
    }

    private async ValueTask<ScopeKey?> ReadAsync(string scope, CancellationToken ct)
    {
        var record = await _store.ReadAsync(scope, ct).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        var material = Unwrap(record);
        try
        {
            var key = new ScopeKey(record.Version, material);
            Remember(scope, key);
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private void Remember(string scope, ScopeKey key)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            // Do not clear the previous key. Another caller may still hold that ScopeKey.
            _cache[scope] = key;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LocalWrappedKeyProvider));
        }
    }

    private WrappedKeyRecord Wrap(string scope, int version, ReadOnlySpan<byte> material)
    {
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[ScopeKey.MaterialLength];
        var tag = new byte[16];
        var aad = Aad(scope, version);
        var wrapping = CopyWrappingKey();
        try
        {
            using var aes = new AesGcm(wrapping, 16);
            aes.Encrypt(nonce, material, ciphertext, tag, aad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapping);
        }

        return new WrappedKeyRecord(scope, version, nonce, tag, ciphertext);
    }

    private byte[] Unwrap(WrappedKeyRecord record)
    {
        if (record.Nonce is null || record.Nonce.Length != 12
            || record.Tag is null || record.Tag.Length != 16
            || record.Ciphertext is null || record.Ciphertext.Length != ScopeKey.MaterialLength)
        {
            throw new CryptographicException("The wrapped scope key is not a record this version can unwrap.");
        }

        var material = new byte[ScopeKey.MaterialLength];
        var aad = Aad(record.Scope, record.Version);
        var wrapping = CopyWrappingKey();
        try
        {
            using var aes = new AesGcm(wrapping, 16);
            aes.Decrypt(record.Nonce, record.Ciphertext, record.Tag, material, aad);
            return material;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(material);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapping);
        }
    }

    private byte[] CopyWrappingKey()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return (byte[])_wrappingKey.Clone();
        }
    }

    private static byte[] Aad(string scope, int version)
    {
        var scopeBytes = Encoding.UTF8.GetBytes(scope);
        var aad = new byte[scopeBytes.Length + 4];
        scopeBytes.CopyTo(aad, 0);
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(scopeBytes.Length), version);
        return aad;
    }

    private static void RequireScope(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException("Scope cannot be empty.", nameof(scope));
        }
    }
}
