# Rechunk.Keys.AzureKeyVault

Wraps each Rechunk scope key with a key that stays in Azure Key Vault. The wrapped bytes, the vault key id, and the vault key version are stored in an `IWrappedKeyStore` (in memory, or `SqliteWrappedKeyStore`). The vault key is not copied into the process.

An RSA key in the vault uses RSA-OAEP-256. Pass `VaultWrapAlgorithm.Aes256KeyWrap` only when the vault key itself is AES. Unwrap uses the version saved on the row, so a newer vault key does not break records that were wrapped earlier. The old key version has to remain able to unwrap. The wrapped plaintext is padded to a multiple of 8 so AES key wrap can carry it. A 2048 bit RSA key can wrap a short scope name. A very long scope name can be too large for that key.

Key Vault WrapKey does not take associated data. The scope name and the scope key version are inside the wrapped bytes and are checked after unwrap. A local AES wrapping key uses AES-GCM associated data instead. A record written by one provider does not open with the other. Unwrap also refuses a stored key id that is a different key, or a different vault, than the one you passed in.

```csharp
using Azure.Identity;
using Rechunk.Keys.AzureKeyVault;
using Rechunk.Storage.Sqlite;

var store = new SqliteWrappedKeyStore("Data Source=rechunk.db");
var keys = new AzureKeyVaultKeyProvider(
    new Uri("https://myvault.vault.azure.net/keys/rechunk"),
    new DefaultAzureCredential(),
    store);
var scopeKey = await keys.CreateAsync("tenant-a");
```

`CreateAsync` is on `AzureKeyVaultKeyProvider`. `IKeyProvider` only reads a key that already exists. Or, after `AddRechunk`:

```csharp
builder.UseAzureKeyVaultKeys(
    new Uri("https://myvault.vault.azure.net/keys/rechunk"),
    new DefaultAzureCredential(),
    store);
```

Depends on the Rechunk package.

https://github.com/challaravinath/Rechunk
