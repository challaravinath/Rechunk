using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Rechunk.Keys.AzureKeyVault;

/// <summary>
/// Random 32 byte key per scope, wrapped by a key that stays in Key Vault.
/// The store keeps the wrapped bytes plus the vault key id and version that produced them.
/// A later vault key does not unwrap old records. Unwrap uses the version saved on the row.
///
/// Key Vault WrapKey does not take associated data. The scope name and the scope key version
/// are placed inside the bytes that are wrapped, and checked after unwrap. A row whose scope
/// or version was changed does not open. This is not the AES-GCM associated data used by
/// <see cref="LocalWrappedKeyProvider"/>.
///
/// A record this provider cannot read is left as it is. Create does not replace it.
/// </summary>
public sealed class AzureKeyVaultKeyProvider : IKeyProvider, IDisposable
{
    private readonly IKeyWrapClient _vault;
    private readonly IWrappedKeyStore _store;
    private readonly object _gate = new();
    private readonly Dictionary<string, ScopeKey> _cache = new(StringComparer.Ordinal);
    private bool _disposed;

    public AzureKeyVaultKeyProvider(IKeyWrapClient vault, IWrappedKeyStore store)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public AzureKeyVaultKeyProvider(Uri keyId, Azure.Core.TokenCredential credential, IWrappedKeyStore store, VaultWrapAlgorithm algorithm = VaultWrapAlgorithm.RsaOaep256)
        : this(new AzureKeyVaultWrapClient(keyId, credential, algorithm), store)
    {
    }

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
            var record = await WrapAsync(scope, 1, material, ct).ConfigureAwait(false);
            if (!await _store.TryAddAsync(record, ct).ConfigureAwait(false))
            {
                return await GetRequiredAsync(scope, ct).ConfigureAwait(false);
            }

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
            // Scope keys already returned share their bytes with the caller. Clearing those
            // would wipe a live key and leave its HMAC schedule behind. There is no local wrapping key.
        }
    }

    private async ValueTask<ScopeKey?> ReadAsync(string scope, CancellationToken ct)
    {
        var record = await _store.ReadAsync(scope, ct).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        var material = await UnwrapAsync(record, ct).ConfigureAwait(false);
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

    private async ValueTask<WrappedKeyRecord> WrapAsync(string scope, int version, byte[] material, CancellationToken ct)
    {
        var payload = Encode(scope, version, material);
        try
        {
            var wrapped = await _vault.WrapAsync(payload, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(wrapped.KeyId) || string.IsNullOrEmpty(wrapped.Version)
                || string.IsNullOrEmpty(wrapped.Algorithm)
                || wrapped.Wrapped is null || wrapped.Wrapped.Length == 0)
            {
                throw new CryptographicException("Key Vault did not return a wrapped key.");
            }

            return new WrappedKeyRecord(scope, version, Array.Empty<byte>(), Array.Empty<byte>(), wrapped.Wrapped)
            {
                ProtectorId = wrapped.KeyId,
                ProtectorVersion = wrapped.Version,
                WrapAlgorithm = wrapped.Algorithm,
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private async ValueTask<byte[]> UnwrapAsync(WrappedKeyRecord record, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(record.ProtectorId) || string.IsNullOrEmpty(record.ProtectorVersion) || string.IsNullOrEmpty(record.WrapAlgorithm)
            || record.Ciphertext is null || record.Ciphertext.Length == 0)
        {
            throw new CryptographicException("The wrapped scope key is not a Key Vault record.");
        }

        if (!record.ProtectorId.EndsWith("/" + record.ProtectorVersion, StringComparison.Ordinal))
        {
            throw new CryptographicException("The vault key id does not match the recorded version.");
        }

        var payload = await _vault.UnwrapAsync(record.ProtectorId, record.ProtectorVersion, record.WrapAlgorithm, record.Ciphertext, ct).ConfigureAwait(false);
        try
        {
            return Decode(record.Scope, record.Version, payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private void Remember(string scope, ScopeKey key)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _cache[scope] = key;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AzureKeyVaultKeyProvider));
        }
    }

    internal static byte[] Encode(string scope, int version, ReadOnlySpan<byte> material)
    {
        var scopeBytes = Encoding.UTF8.GetBytes(scope);
        var rawLength = 12 + scopeBytes.Length + material.Length;
        // AES key wrap needs a multiple of 8. RSA-OAEP does not. Pad so either algorithm can carry the same bytes.
        var payload = new byte[(rawLength + 7) & ~7];
        var span = payload.AsSpan();
        "RK01"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], scopeBytes.Length);
        scopeBytes.CopyTo(span[8..]);
        var versionAt = 8 + scopeBytes.Length;
        BinaryPrimitives.WriteInt32LittleEndian(span[versionAt..], version);
        material.CopyTo(span[(versionAt + 4)..]);
        return payload;
    }

    internal static byte[] Decode(string scope, int version, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 44 || !payload[..4].SequenceEqual("RK01"u8))
        {
            throw new CryptographicException("The unwrapped scope key is not a record this version can read.");
        }

        var scopeLength = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        if (scopeLength < 1 || scopeLength > payload.Length - 44)
        {
            throw new CryptographicException("The unwrapped scope key is not a record this version can read.");
        }

        var storedScope = Encoding.UTF8.GetString(payload.Slice(8, scopeLength));
        var versionAt = 8 + scopeLength;
        var storedVersion = BinaryPrimitives.ReadInt32LittleEndian(payload[versionAt..]);
        var exact = versionAt + 4 + ScopeKey.MaterialLength;
        if (!string.Equals(storedScope, scope, StringComparison.Ordinal) || storedVersion != version
            || payload.Length < exact || payload.Length - exact >= 8)
        {
            throw new CryptographicException("The wrapped scope key does not match this row.");
        }

        for (var i = exact; i < payload.Length; i++)
        {
            if (payload[i] != 0)
            {
                throw new CryptographicException("The wrapped scope key does not match this row.");
            }
        }

        return payload.Slice(versionAt + 4, ScopeKey.MaterialLength).ToArray();
    }

    private static void RequireScope(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException("Scope cannot be empty.", nameof(scope));
        }
    }
}
