using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Rechunk.Core.Tests.Store;
using Rechunk.Storage.Redis;
using Rechunk.Storage.Sqlite;
using StackExchange.Redis;

namespace Rechunk.Core.Tests;

public class SignatureEncryptionTests
{
    private static readonly byte[] WrappingKey = Convert.FromHexString(
        "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff");

    [Fact]
    public void A_tampered_or_swapped_payload_does_not_open()
    {
        var key = new ScopeKey(1, WrappingKey);
        var other = new ScopeKey(1, Enumerable.Repeat((byte)9, 32).ToArray());
        var plain = new byte[32];
        plain[0] = 7;
        var packed = PayloadCipher.Encrypt(key, "docs", "chunk-a", "simhash", plain);
        Assert.Equal(plain, PayloadCipher.Decrypt(key, "docs", "chunk-a", "simhash", packed));

        foreach (var index in new[] { packed.Length - 1, 1, 13 })
        {
            var flipped = (byte[])packed.Clone();
            flipped[index] ^= 1;
            Assert.ThrowsAny<CryptographicException>(() => PayloadCipher.Decrypt(key, "docs", "chunk-a", "simhash", flipped));
        }

        Assert.ThrowsAny<CryptographicException>(() => PayloadCipher.Decrypt(key, "docs", "chunk-b", "simhash", packed));
        Assert.ThrowsAny<CryptographicException>(() => PayloadCipher.Decrypt(key, "other", "chunk-a", "simhash", packed));
        Assert.ThrowsAny<CryptographicException>(() => PayloadCipher.Decrypt(key, "docs", "chunk-a", "minhash", packed));
        Assert.ThrowsAny<CryptographicException>(() => PayloadCipher.Decrypt(other, "docs", "chunk-a", "simhash", packed));
    }

    [Fact]
    public async Task Sqlite_round_trip_restart_and_tamper()
    {
        using var file = new TempSqlite();
        var text = TestText.Paragraph(21, 80);
        var edited = TestText.WithWordChanged(21, 80, 10, "revised");
        using (var keys = new LocalWrappedKeyProvider(WrappingKey, new SqliteWrappedKeyStore(file.ConnectionString)))
        {
            await keys.CreateAsync("docs");
            var store = Store(file.ConnectionString, keys);
            await Write(store, keys, "docs", text);
            AssertEncrypted(file.ConnectionString);
        }

        using (var keys = new LocalWrappedKeyProvider(WrappingKey, new SqliteWrappedKeyStore(file.ConnectionString)))
        {
            var store = Store(file.ConnectionString, keys);
            var again = await Pipeline(store, keys).DeduplicateAsync("docs", new[] { new ChunkInput(0, edited) });
            Assert.Equal(DedupOutcome.Duplicate, again.Decisions[0].Outcome);
            Assert.NotEqual(MatchStage.Exact, again.Decisions[0].Stage);
        }

        Assert.Equal(1, await FlipPayload(file.ConnectionString));
        using var broken = new LocalWrappedKeyProvider(WrappingKey, new SqliteWrappedKeyStore(file.ConnectionString));
        var another = TestText.WithWordChanged(21, 80, 20, "altered");
        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            Pipeline(Store(file.ConnectionString, broken), broken).DeduplicateAsync("docs", new[] { new ChunkInput(0, another) }).AsTask());

        var wrong = new LocalWrappedKeyProvider(Enumerable.Repeat((byte)4, 32).ToArray(), new SqliteWrappedKeyStore(file.ConnectionString));
        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            Pipeline(Store(file.ConnectionString, wrong), wrong).DeduplicateAsync("docs", new[] { new ChunkInput(0, text) }).AsTask());
    }

    [Fact]
    public async Task An_unkeyed_sqlite_payload_stays_plain()
    {
        using var file = new TempSqlite();
        var options = Options();
        var store = new BackedFingerprintStore(new SqliteFingerprintBackend(file.ConnectionString), options);
        await Write(store, null, "docs", TestText.Paragraph(4, 80));
        await using var connection = new SqliteConnection(file.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM rechunk_band_entry WHERE scope = 'docs' AND kind = 1 LIMIT 1";
        var payload = (byte[])(await command.ExecuteScalarAsync())!;
        Assert.Equal(32, payload.Length);
    }

    [Fact]
    public async Task Migration_stores_ciphertext_and_resume_still_finishes()
    {
        using var file = new TempSqlite();
        var options = Options();
        using var keys = new LocalWrappedKeyProvider(WrappingKey, new SqliteWrappedKeyStore(file.ConnectionString));
        await keys.CreateAsync("docs");
        var store = new BackedFingerprintStore(new SqliteFingerprintBackend(file.ConnectionString), options);
        var revisions = new SqliteRevisionStore(file.ConnectionString);
        var pipeline = new RechunkPipeline(options, new ChunkFingerprinter(options), store.Exact, null, store.SimHash, store.MinHash, store);
        var texts = new[] { TestText.Paragraph(21, 80), TestText.Paragraph(22, 40), TestText.Paragraph(23, 40) };
        var report = await pipeline.DeduplicateAsync("docs", texts.Select((t, i) => new ChunkInput(i, t)).ToList(), new DedupClaimOptions("seed"));
        foreach (var decision in report.Decisions)
        {
            if (decision.Claim is { } claim)
            {
                await store.CommitAsync(claim.ToCommit());
            }
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new KeyedIdMigrator(store, keys, options, revisions).RunAsync("docs", new MigrationRun
            {
                OnStep = step =>
                {
                    if (step.Phase == "fingerprint" && step.Count == 1)
                    {
                        throw new InvalidOperationException("stop");
                    }
                },
            }).AsTask());
        await new KeyedIdMigrator(store, keys, options, revisions).RunAsync("docs");
        AssertEncrypted(file.ConnectionString);
        var reading = Store(file.ConnectionString, keys);
        var edited = TestText.WithWordChanged(21, 80, 10, "revised");
        var near = await Pipeline(reading, keys).DeduplicateAsync("docs", new[] { new ChunkInput(0, edited) });
        Assert.Equal(DedupOutcome.Duplicate, near.Decisions[0].Outcome);
    }

    [SkippableFact]
    public async Task Redis_round_trip_and_a_swapped_payload_fails()
    {
        using var env = new RedisTestEnvironment();
        var redisOptions = new RedisStorageOptions { KeyPrefix = env.Prefix };
        using var keys = new LocalWrappedKeyProvider(WrappingKey, new InMemoryWrappedKeyStore());
        await keys.CreateAsync("docs");
        var store = new BackedFingerprintStore(new RedisFingerprintBackend(env.Redis, redisOptions), Options(keys));
        await Write(store, keys, "docs", TestText.Paragraph(21, 80));
        await Write(store, keys, "docs", TestText.Paragraph(30, 80));

        var db = env.Redis.GetDatabase();
        var entries = new List<RedisKey>();
        foreach (var endpoint in env.Redis.GetEndPoints())
        {
            var server = env.Redis.GetServer(endpoint);
            if (!server.IsReplica)
            {
                entries.AddRange(server.Keys(pattern: env.Prefix + ":*:e1:*"));
            }
        }

        Assert.True(entries.Count >= 2);
        var first = (byte[])(await db.StringGetAsync(entries[0]))!;
        var second = (byte[])(await db.StringGetAsync(entries[1]))!;
        Assert.True(first.Length > 32);
        await db.StringSetAsync(entries[0], second);
        await db.StringSetAsync(entries[1], first);
        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            Pipeline(store, keys).DeduplicateAsync("docs", new[] { new ChunkInput(0, TestText.WithWordChanged(21, 80, 10, "revised")) }).AsTask());
    }

    private static async Task Write(IFingerprintStore store, IKeyProvider? keys, string scope, string text)
    {
        var report = await Pipeline(store, keys).DeduplicateAsync(scope, new[] { new ChunkInput(0, text) }, new DedupClaimOptions("h"));
        if (report.Decisions[0].Claim is { } claim)
        {
            await store.CommitAsync(claim.ToCommit());
        }
    }

    private static void AssertEncrypted(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM rechunk_band_entry WHERE kind = 1 LIMIT 1";
        var payload = (byte[])command.ExecuteScalar()!;
        Assert.True(payload.Length > 32);
        Assert.Equal(PayloadCipher.Version, payload[0]);
    }

    private static async Task<int> FlipPayload(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT id, payload FROM rechunk_band_entry WHERE kind = 1 LIMIT 1";
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var id = reader.GetString(0);
        var payload = (byte[])reader[1];
        payload[^1] ^= 1;
        await reader.DisposeAsync();
        await using var write = connection.CreateCommand();
        write.CommandText = "UPDATE rechunk_band_entry SET payload = $payload WHERE kind = 1 AND id = $id";
        write.Parameters.AddWithValue("$payload", payload);
        write.Parameters.AddWithValue("$id", id);
        return await write.ExecuteNonQueryAsync();
    }

    private static RechunkOptions Options(IKeyProvider? keys = null)
    {
        var options = new RechunkOptions { KeyProvider = keys };
        options.SimHash.Enabled = true;
        options.SimHash.MaxHammingDistance = 6;
        options.MinHash.Enabled = true;
        return options;
    }

    private static BackedFingerprintStore Store(string connection, IKeyProvider keys) =>
        new(new SqliteFingerprintBackend(connection), Options(keys));

    private static RechunkPipeline Pipeline(IFingerprintStore store, IKeyProvider? keys)
    {
        var options = Options(keys);
        return new RechunkPipeline(options, new ChunkFingerprinter(options), store.Exact, null, store.SimHash, store.MinHash, store);
    }
}
