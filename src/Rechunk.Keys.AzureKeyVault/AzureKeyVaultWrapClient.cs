using System.Security.Cryptography;
using Azure.Core;
using Azure.Security.KeyVault.Keys.Cryptography;

namespace Rechunk.Keys.AzureKeyVault;

/// <summary>
/// Calls Key Vault WrapKey and UnwrapKey. The vault key is not copied into this process.
/// RSA keys use RSA-OAEP-256. An AES key in the vault needs <see cref="VaultWrapAlgorithm.Aes256KeyWrap"/>.
/// Unwrap accepts another version of this same key and refuses a different key or a different vault.
/// </summary>
public sealed class AzureKeyVaultWrapClient : IKeyWrapClient
{
    private readonly CryptographyClient _current;
    private readonly TokenCredential _credential;
    private readonly KeyWrapAlgorithm _algorithm;
    private readonly Uri _keyId;

    public AzureKeyVaultWrapClient(Uri keyId, TokenCredential credential, VaultWrapAlgorithm algorithm = VaultWrapAlgorithm.RsaOaep256)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        ArgumentNullException.ThrowIfNull(credential);
        _keyId = keyId;
        _credential = credential;
        _algorithm = ToAzure(algorithm);
        _current = new CryptographyClient(keyId, credential);
    }

    public async ValueTask<KeyWrapOutput> WrapAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
    {
        var bytes = plaintext.ToArray();
        try
        {
            var result = await _current.WrapKeyAsync(_algorithm, bytes, ct).ConfigureAwait(false);
            var version = VersionOf(result.KeyId);
            return new KeyWrapOutput(result.KeyId, version, result.Algorithm.ToString(), result.EncryptedKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async ValueTask<byte[]> UnwrapAsync(string keyId, string version, string algorithm, ReadOnlyMemory<byte> wrapped, CancellationToken ct = default)
    {
        if (!keyId.EndsWith("/" + version, StringComparison.Ordinal))
        {
            throw new CryptographicException("The vault key id does not match the recorded version.");
        }

        EnsureSameKey(_keyId, keyId);
        var client = new CryptographyClient(new Uri(keyId), _credential);
        var result = await client.UnwrapKeyAsync(new KeyWrapAlgorithm(algorithm), wrapped.ToArray(), ct).ConfigureAwait(false);
        return result.Key;
    }

    internal static string VersionOf(string keyId)
    {
        if (!Uri.TryCreate(keyId, UriKind.Absolute, out var uri))
        {
            throw new CryptographicException("Key Vault did not return a key version.");
        }

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !string.Equals(parts[0], "keys", StringComparison.OrdinalIgnoreCase) || parts[^1].Length == 0)
        {
            throw new CryptographicException("Key Vault did not return a key version.");
        }

        return parts[^1];
    }

    /// <summary>
    /// The recorded id has to be a version of the key this client was given.
    /// A row that names a different key, or a different vault, is not unwrapped.
    /// </summary>
    internal static void EnsureSameKey(Uri configured, string recordedKeyId)
    {
        if (!Uri.TryCreate(recordedKeyId, UriKind.Absolute, out var recorded))
        {
            throw new CryptographicException("The recorded vault key is not the key this provider uses.");
        }

        if (!string.Equals(configured.Scheme, recorded.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(configured.Host, recorded.Host, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(KeyName(configured), KeyName(recorded), StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException("The recorded vault key is not the key this provider uses.");
        }
    }

    private static string KeyName(Uri keyId)
    {
        var parts = keyId.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !string.Equals(parts[0], "keys", StringComparison.OrdinalIgnoreCase) || parts[1].Length == 0)
        {
            throw new CryptographicException("The recorded vault key is not the key this provider uses.");
        }

        return parts[1];
    }

    private static KeyWrapAlgorithm ToAzure(VaultWrapAlgorithm algorithm) => algorithm switch
    {
        VaultWrapAlgorithm.RsaOaep256 => KeyWrapAlgorithm.RsaOaep256,
        VaultWrapAlgorithm.Aes256KeyWrap => KeyWrapAlgorithm.A256KW,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };
}

public enum VaultWrapAlgorithm
{
    /// <summary>RSA-OAEP with SHA-256. Use this for an RSA key in the vault.</summary>
    RsaOaep256,

    /// <summary>AES key wrap with a 256 bit key. Use this only when the vault key itself is AES.</summary>
    Aes256KeyWrap,
}
