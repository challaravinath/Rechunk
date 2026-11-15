using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Rechunk.Keys.AzureKeyVault;

public static class RechunkBuilderExtensions
{
    /// <summary>
    /// Uses Key Vault to wrap scope keys. The vault key stays in the vault.
    /// The wrapped bytes are stored in <paramref name="store"/>.
    /// </summary>
    public static IRechunkBuilder UseAzureKeyVaultKeys(
        this IRechunkBuilder builder,
        Uri keyId,
        TokenCredential credential,
        IWrappedKeyStore store,
        VaultWrapAlgorithm algorithm = VaultWrapAlgorithm.RsaOaep256)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseAzureKeyVaultKeys(new AzureKeyVaultWrapClient(keyId, credential, algorithm), store);
    }

    /// <summary>Same as the Key Vault constructor, with a wrap client you supply. Tests use this.</summary>
    public static IRechunkBuilder UseAzureKeyVaultKeys(this IRechunkBuilder builder, IKeyWrapClient vault, IWrappedKeyStore store)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(store);
        var provider = new AzureKeyVaultKeyProvider(vault, store);
        builder.Services.Replace(ServiceDescriptor.Singleton(provider));
        builder.Services.Replace(ServiceDescriptor.Singleton<IKeyProvider>(provider));
        return builder;
    }
}
