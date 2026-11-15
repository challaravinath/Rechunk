namespace Rechunk.Keys.AzureKeyVault;

/// <summary>Wraps and unwraps a scope key. The real client calls Key Vault. Tests use a fake.</summary>
public interface IKeyWrapClient
{
    ValueTask<KeyWrapOutput> WrapAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct = default);

    ValueTask<byte[]> UnwrapAsync(string keyId, string version, string algorithm, ReadOnlyMemory<byte> wrapped, CancellationToken ct = default);
}

/// <summary>One wrap. KeyId includes the version that produced it.</summary>
public readonly record struct KeyWrapOutput(string KeyId, string Version, string Algorithm, byte[] Wrapped);
