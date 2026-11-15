using System.Security.Cryptography;
using System.Text;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Rechunk.Core.Tests.Store;
using Rechunk.Keys.AzureKeyVault;
using Rechunk.Storage.Sqlite;

namespace Rechunk.Core.Tests;

public class AzureKeyVaultKeyProviderTests
{
    [Fact]
    public async Task Round_trip_returns_the_same_scope_key()
    {
        var vault = new FakeKeyVault();
        var store = new InMemoryWrappedKeyStore();
        using var provider = new AzureKeyVaultKeyProvider(vault, store);

        var created = await provider.CreateAsync("alpha");
        var loaded = await provider.GetRequiredAsync("alpha");

        Assert.Equal(Id(created), Id(loaded));
        var record = await store.ReadAsync("alpha");
        Assert.NotNull(record);
        Assert.Equal("v1", record!.ProtectorVersion);
        Assert.EndsWith("/v1", record.ProtectorId);
        Assert.Equal("RSA-OAEP-256", record.WrapAlgorithm);
        Assert.Empty(record.Nonce);
        Assert.NotEqual(created.Material.ToArray(), record.Ciphertext);
    }

    [Fact]
    public async Task Wrong_vault_key_fails_and_leaves_the_record()
    {
        var store = new InMemoryWrappedKeyStore();
        await new AzureKeyVaultKeyProvider(new FakeKeyVault(), store).CreateAsync("alpha");
        var record = await store.ReadAsync("alpha");

        var reader = new AzureKeyVaultKeyProvider(new FakeKeyVault(), store);
        await Assert.ThrowsAsync<CryptographicException>(() => reader.GetRequiredAsync("alpha").AsTask());
        await Assert.ThrowsAsync<CryptographicException>(() => reader.CreateAsync("alpha").AsTask());

        var again = await store.ReadAsync("alpha");
        Assert.Equal(record!.Ciphertext, again!.Ciphertext);
    }

    [Fact]
    public async Task Tampered_record_fails()
    {
        var vault = new FakeKeyVault();
        var store = new InMemoryWrappedKeyStore();
        await new AzureKeyVaultKeyProvider(vault, store).CreateAsync("alpha");
        var record = await store.ReadAsync("alpha");
        record!.Ciphertext[^1] ^= 0xff;

        var moved = new InMemoryWrappedKeyStore();
        Assert.True(await moved.TryAddAsync(record));
        var reader = new AzureKeyVaultKeyProvider(vault, moved);
        await Assert.ThrowsAsync<CryptographicException>(() => reader.GetRequiredAsync("alpha").AsTask());
    }

    [Fact]
    public async Task Scope_or_version_swap_fails()
    {
        var vault = new FakeKeyVault();
        var store = new InMemoryWrappedKeyStore();
        await new AzureKeyVaultKeyProvider(vault, store).CreateAsync("alpha");
        var record = await store.ReadAsync("alpha");

        var otherScope = new InMemoryWrappedKeyStore();
        Assert.True(await otherScope.TryAddAsync(record! with { Scope = "beta" }));
        await Assert.ThrowsAsync<CryptographicException>(() =>
            new AzureKeyVaultKeyProvider(vault, otherScope).GetRequiredAsync("beta").AsTask());

        var otherVersion = new InMemoryWrappedKeyStore();
        Assert.True(await otherVersion.TryAddAsync(record with { Version = 2 }));
        await Assert.ThrowsAsync<CryptographicException>(() =>
            new AzureKeyVaultKeyProvider(vault, otherVersion).GetRequiredAsync("alpha").AsTask());
    }

    [Fact]
    public async Task Restart_with_sqlite_reads_the_same_key()
    {
        var vault = new FakeKeyVault();
        using var file = new TempSqlite();
        ChunkId first;
        using (var provider = new AzureKeyVaultKeyProvider(vault, new SqliteWrappedKeyStore(file.ConnectionString)))
        {
            first = Id(await provider.CreateAsync("alpha"));
        }

        using var again = new AzureKeyVaultKeyProvider(vault, new SqliteWrappedKeyStore(file.ConnectionString));
        Assert.Equal(first, Id(await again.GetRequiredAsync("alpha")));
        var record = await new SqliteWrappedKeyStore(file.ConnectionString).ReadAsync("alpha");
        Assert.Equal("v1", record!.ProtectorVersion);
        Assert.Equal("RSA-OAEP-256", record.WrapAlgorithm);
    }

    [Fact]
    public async Task Vault_key_rotation_keeps_old_records_readable()
    {
        var vault = new FakeKeyVault();
        var store = new InMemoryWrappedKeyStore();
        ChunkId oldId;
        using (var provider = new AzureKeyVaultKeyProvider(vault, store))
        {
            oldId = Id(await provider.CreateAsync("old"));
        }

        vault.AddVersion("v2");
        using var next = new AzureKeyVaultKeyProvider(vault, store);
        Assert.Equal(oldId, Id(await next.GetRequiredAsync("old")));
        var created = Id(await next.CreateAsync("new"));
        Assert.Equal("v1", (await store.ReadAsync("old"))!.ProtectorVersion);
        Assert.Equal("v2", (await store.ReadAsync("new"))!.ProtectorVersion);

        vault.Forget("v1");
        using var after = new AzureKeyVaultKeyProvider(vault, store);
        await Assert.ThrowsAsync<CryptographicException>(() => after.GetRequiredAsync("old").AsTask());
        Assert.Equal(created, Id(await after.GetRequiredAsync("new")));
    }

    [Fact]
    public async Task Concurrent_first_create_stores_one_key()
    {
        var store = new CountingStore(new InMemoryWrappedKeyStore());
        var provider = new AzureKeyVaultKeyProvider(new FakeKeyVault(), store);
        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => IdOf(provider)));

        Assert.Equal(1, store.Wins);
        Assert.True(store.Attempts >= 1);
        Assert.All(ids, id => Assert.Equal(ids[0], id));
    }

    [Fact]
    public async Task Local_and_vault_records_do_not_open_in_the_other_provider()
    {
        var wrapping = Enumerable.Repeat((byte)7, 32).ToArray();
        var localStore = new InMemoryWrappedKeyStore();
        using (var local = new LocalWrappedKeyProvider(wrapping, localStore))
        {
            await local.CreateAsync("alpha");
        }

        var vault = new FakeKeyVault();
        await Assert.ThrowsAsync<CryptographicException>(() =>
            new AzureKeyVaultKeyProvider(vault, localStore).GetRequiredAsync("alpha").AsTask());

        var vaultStore = new InMemoryWrappedKeyStore();
        await new AzureKeyVaultKeyProvider(vault, vaultStore).CreateAsync("alpha");
        await Assert.ThrowsAsync<CryptographicException>(() =>
            new LocalWrappedKeyProvider(wrapping, vaultStore).GetRequiredAsync("alpha").AsTask());
    }

    [Fact]
    public void Builder_registers_the_provider()
    {
        var services = new ServiceCollection();
        services.AddRechunk().UseAzureKeyVaultKeys(new FakeKeyVault(), new InMemoryWrappedKeyStore());
        using var provider = services.BuildServiceProvider();
        Assert.IsType<AzureKeyVaultKeyProvider>(provider.GetRequiredService<IKeyProvider>());
    }

    [Theory]
    [InlineData("https://myvault.vault.azure.net/keys/rechunk/abc123", "abc123")]
    [InlineData("https://myvault.managedhsm.azure.net/keys/rechunk/abc123", "abc123")]
    public void Version_comes_from_the_key_id(string keyId, string version)
    {
        Assert.Equal(version, AzureKeyVaultWrapClient.VersionOf(keyId));
    }

    [Fact]
    public void Unwrap_refuses_a_different_vault_key()
    {
        var configured = new Uri("https://myvault.vault.azure.net/keys/rechunk");
        AzureKeyVaultWrapClient.EnsureSameKey(configured, "https://myvault.vault.azure.net/keys/rechunk/abc123");
        AzureKeyVaultWrapClient.EnsureSameKey(
            new Uri("https://myvault.vault.azure.net/keys/rechunk/old"),
            "https://myvault.vault.azure.net/keys/rechunk/abc123");
        Assert.Throws<CryptographicException>(() =>
            AzureKeyVaultWrapClient.EnsureSameKey(configured, "https://myvault.vault.azure.net/keys/other/abc123"));
        Assert.Throws<CryptographicException>(() =>
            AzureKeyVaultWrapClient.EnsureSameKey(configured, "https://othervault.vault.azure.net/keys/rechunk/abc123"));
        Assert.Throws<CryptographicException>(() =>
            AzureKeyVaultWrapClient.EnsureSameKey(configured, "not a uri"));
    }

    [Theory]
    [InlineData("https://myvault.vault.azure.net/keys/rechunk")]
    [InlineData("https://myvault.vault.azure.net/secrets/rechunk/abc")]
    [InlineData("not a uri")]
    public void Version_rejects_an_id_without_a_key_version(string keyId)
    {
        Assert.Throws<CryptographicException>(() => AzureKeyVaultWrapClient.VersionOf(keyId));
    }

    [SkippableFact]
    public async Task Live_vault_round_trip_when_configured()
    {
        var keyId = Environment.GetEnvironmentVariable("RECHUNK_KEY_VAULT_KEY_ID");
        Skip.If(string.IsNullOrWhiteSpace(keyId), "Set RECHUNK_KEY_VAULT_KEY_ID to a Key Vault key URI to run the live test.");

        var store = new InMemoryWrappedKeyStore();
        ChunkId first;
        using (var provider = new AzureKeyVaultKeyProvider(new Uri(keyId!), new DefaultAzureCredential(), store))
        {
            first = Id(await provider.CreateAsync("rechunk-live"));
        }

        using var again = new AzureKeyVaultKeyProvider(new Uri(keyId!), new DefaultAzureCredential(), store);
        Assert.Equal(first, Id(await again.GetRequiredAsync("rechunk-live")));
        Assert.False(string.IsNullOrEmpty((await store.ReadAsync("rechunk-live"))!.ProtectorVersion));
    }

    private static ChunkId Id(ScopeKey key) => ChunkIds.FromText("hello world", null, key);

    private static async Task<ChunkId> IdOf(AzureKeyVaultKeyProvider provider) => Id(await provider.CreateAsync("alpha"));

    private sealed class CountingStore : IWrappedKeyStore
    {
        private readonly IWrappedKeyStore _inner;

        public CountingStore(IWrappedKeyStore inner) => _inner = inner;

        public int Attempts;

        public int Wins;

        public ValueTask<WrappedKeyRecord?> ReadAsync(string scope, CancellationToken ct = default) => _inner.ReadAsync(scope, ct);

        public async ValueTask<bool> TryAddAsync(WrappedKeyRecord record, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Attempts);
            var added = await _inner.TryAddAsync(record, ct).ConfigureAwait(false);
            if (added)
            {
                Interlocked.Increment(ref Wins);
            }

            return added;
        }
    }

    /// <summary>
    /// Stand-in for CryptographyClient. Each version has its own AES key, so a wrap from one version
    /// does not open with another, and a changed ciphertext does not open at all.
    /// </summary>
    private sealed class FakeKeyVault : IKeyWrapClient
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
        private string _current = "v1";

        public FakeKeyVault()
        {
            _keys["v1"] = RandomNumberGenerator.GetBytes(32);
        }

        public void AddVersion(string version)
        {
            lock (_gate)
            {
                _keys[version] = RandomNumberGenerator.GetBytes(32);
                _current = version;
            }
        }

        public void Forget(string version)
        {
            lock (_gate)
            {
                _keys.Remove(version);
            }
        }

        public ValueTask<KeyWrapOutput> WrapAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            string version;
            byte[] key;
            lock (_gate)
            {
                version = _current;
                key = (byte[])_keys[version].Clone();
            }

            try
            {
                var nonce = RandomNumberGenerator.GetBytes(12);
                var cipher = new byte[plaintext.Length];
                var tag = new byte[16];
                using (var aes = new AesGcm(key, 16))
                {
                    aes.Encrypt(nonce, plaintext.Span, cipher, tag);
                }

                var wrapped = new byte[nonce.Length + tag.Length + cipher.Length];
                nonce.CopyTo(wrapped, 0);
                tag.CopyTo(wrapped, nonce.Length);
                cipher.CopyTo(wrapped, nonce.Length + tag.Length);
                return ValueTask.FromResult(new KeyWrapOutput(
                    "https://vault.example/keys/rechunk/" + version,
                    version,
                    "RSA-OAEP-256",
                    wrapped));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        public ValueTask<byte[]> UnwrapAsync(string keyId, string version, string algorithm, ReadOnlyMemory<byte> wrapped, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.Equals(algorithm, "RSA-OAEP-256", StringComparison.Ordinal)
                || !keyId.EndsWith("/" + version, StringComparison.Ordinal)
                || wrapped.Length < 29)
            {
                throw new CryptographicException("The vault key could not unwrap this record.");
            }

            byte[] key;
            lock (_gate)
            {
                if (!_keys.TryGetValue(version, out var found))
                {
                    throw new CryptographicException("The vault key could not unwrap this record.");
                }

                key = (byte[])found.Clone();
            }

            var plain = new byte[wrapped.Length - 28];
            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(wrapped.Span[..12], wrapped.Span[28..], wrapped.Span.Slice(12, 16), plain);
                return ValueTask.FromResult(plain);
            }
            catch (CryptographicException)
            {
                CryptographicOperations.ZeroMemory(plain);
                throw new CryptographicException("The vault key could not unwrap this record.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }
}
