using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Rechunk.Core.Tests.Store;
using Rechunk.Storage.Redis;
using Rechunk.Storage.Sqlite;

namespace Rechunk.Core.Tests;

public class KeyedIdIntegrationTests
{
    private static readonly byte[] WrappingKey = Convert.FromHexString(
        "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff");

    [Fact]
    public void SipHash_2_4_matches_the_published_short_vectors()
    {
        var k0 = 0x0706050403020100UL;
        var k1 = 0x0f0e0d0c0b0a0908UL;
        Assert.Equal(0x726fdb47dd0e0e31UL, SipHash24.Compute(k0, k1, ReadOnlySpan<byte>.Empty));
        Assert.Equal(0x74f839c593dc67fdUL, SipHash24.Compute(k0, k1, new byte[] { 0x00 }));

        var key = new ScopeKey(1, WrappingKey);
        var (d0, d1) = BandKeys.Derive(key);
        Assert.NotEqual(k0, d0);
        Assert.False(WrappingKey.AsSpan(0, 8).SequenceEqual(BitConverter.GetBytes(d0)));
    }

    [Fact]
    public async Task Without_a_provider_ids_stay_the_digest()
    {
        var pipeline = RechunkPipeline.CreateBuilder().Build();
        var digest = ExactHasher.Hash("hello world");
        Assert.Equal(Convert.ToHexString(digest).ToLowerInvariant(), pipeline.IdFor("hello world").Value);
        var report = await pipeline.DeduplicateAsync("docs", new[] { new ChunkInput(0, "hello world") });
        Assert.Equal(pipeline.IdFor("hello world"), report.Decisions[0].Id);
        Assert.False(ChunkIds.IsKeyed(report.Decisions[0].Id));
    }

    [Fact]
    public async Task A_missing_key_fails_before_anything_is_stored()
    {
        using var keys = new LocalWrappedKeyProvider(WrappingKey, new InMemoryWrappedKeyStore());
        var (pipeline, store) = Open(keys);
        var error = await Assert.ThrowsAsync<KeyUnavailableException>(() =>
            pipeline.DeduplicateAsync("docs", new[] { new ChunkInput(0, "hello world") }).AsTask());
        Assert.Equal("docs", error.Scope);
        Assert.DoesNotContain(Convert.ToHexString(WrappingKey), error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(await store.Exact.FindAsync("docs", ExactHasher.Hash("hello world")));
    }

    [Fact]
    public async Task Exact_and_near_matches_stay_inside_one_scope()
    {
        using var keys = new LocalWrappedKeyProvider(WrappingKey, new InMemoryWrappedKeyStore());
        await keys.CreateAsync("alpha");
        await keys.CreateAsync("beta");
        var (pipeline, store) = Open(keys);
        var original = TestText.Paragraph(21, 300);
        var edited = TestText.WithWordChanged(21, 300, 100, "revised");

        var first = await pipeline.DeduplicateAsync("alpha", new[] { new ChunkInput(0, original), new ChunkInput(1, edited) });
        Assert.Equal(DedupOutcome.Unique, first.Decisions[0].Outcome);
        Assert.Equal(DedupOutcome.Duplicate, first.Decisions[1].Outcome);
        Assert.NotEqual(MatchStage.Exact, first.Decisions[1].Stage);
        Assert.True(ChunkIds.IsKeyed(first.Decisions[0].Id));
        Assert.NotEqual(first.Decisions[0].Id, first.Decisions[1].Id);
        await store.CommitAsync(first.Decisions[0].Claim!.ToCommit());

        var other = await pipeline.DeduplicateAsync("beta", new[] { new ChunkInput(0, original) });
        Assert.Equal(DedupOutcome.Unique, other.Decisions[0].Outcome);
        Assert.NotEqual(first.Decisions[0].Id, other.Decisions[0].Id);

        var again = await pipeline.DeduplicateAsync("alpha", new[] { new ChunkInput(0, original) });
        Assert.Equal(DedupOutcome.Duplicate, again.Decisions[0].Outcome);
        Assert.Equal(MatchStage.Exact, again.Decisions[0].Stage);
        Assert.Equal(first.Decisions[0].Id, again.Decisions[0].DuplicateOf);
    }

    [Fact]
    public async Task Sqlite_restart_keeps_the_id_and_a_bad_wrapping_key_does_not_replace_the_row()
    {
        using var file = new TempSqlite();
        var text = "hello world";
        ChunkId id;
        using (var keys = new LocalWrappedKeyProvider(WrappingKey, new SqliteWrappedKeyStore(file.ConnectionString)))
        {
            await keys.CreateAsync("docs");
            var (pipeline, store) = Open(keys, file.ConnectionString);
            var report = await pipeline.DeduplicateAsync("docs", new[] { new ChunkInput(0, text) }, new DedupClaimOptions("holder"));
            id = report.Decisions[0].Id;
            Assert.Equal(CommitStatus.Committed, await store.CommitAsync(report.Decisions[0].Claim!.ToCommit()));
        }

        var before = await ReadExact(file.ConnectionString, "docs");
        Assert.Equal(2, before.HashVersion);
        Assert.Equal(1, before.KeyVersion);
        Assert.Equal(id.Value, before.Chunk);
        Assert.NotEqual(before.Hash, Encoding.UTF8.GetBytes(text));

        using (var keys = new LocalWrappedKeyProvider(WrappingKey, new SqliteWrappedKeyStore(file.ConnectionString)))
        {
            var (pipeline, _) = Open(keys, file.ConnectionString);
            var again = await pipeline.DeduplicateAsync("docs", new[] { new ChunkInput(0, text) });
            Assert.Equal(id, again.Decisions[0].Id);
            Assert.Equal(DedupOutcome.Duplicate, again.Decisions[0].Outcome);
        }

        var wrong = new LocalWrappedKeyProvider(Enumerable.Repeat((byte)9, 32).ToArray(), new SqliteWrappedKeyStore(file.ConnectionString));
        var (bad, _) = Open(wrong, file.ConnectionString);
        var denied = await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            bad.DeduplicateAsync("docs", new[] { new ChunkInput(0, "other text") }).AsTask());
        Assert.DoesNotContain(Convert.ToHexString(WrappingKey), denied.ToString(), StringComparison.OrdinalIgnoreCase);
        AssertSame(before, await ReadExact(file.ConnectionString, "docs"));

        await FlipWrappedCiphertext(file.ConnectionString, "docs");
        var tampered = new LocalWrappedKeyProvider(WrappingKey, new SqliteWrappedKeyStore(file.ConnectionString));
        var (broken, _) = Open(tampered, file.ConnectionString);
        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            broken.DeduplicateAsync("docs", new[] { new ChunkInput(0, text) }).AsTask());
        Assert.Equal(id.Value, (await ReadExact(file.ConnectionString, "docs")).Chunk);
    }

    [Fact]
    public async Task A_scope_cannot_mix_legacy_and_keyed_rows()
    {
        using var file = new TempSqlite();
        var legacy = RechunkPipeline.CreateBuilder()
            .UseFingerprintStore(new BackedFingerprintStore(new SqliteFingerprintBackend(file.ConnectionString)))
            .Build();
        var first = await legacy.DeduplicateAsync("docs", new[] { new ChunkInput(0, "hello world") }, new DedupClaimOptions("h"));
        await legacy.Store!.CommitAsync(first.Decisions[0].Claim!.ToCommit());
        var stored = await ReadExact(file.ConnectionString, "docs");

        using var keys = new LocalWrappedKeyProvider(WrappingKey, new SqliteWrappedKeyStore(file.ConnectionString));
        await keys.CreateAsync("docs");
        var (keyed, _) = Open(keys, file.ConnectionString);
        var mixed = await Assert.ThrowsAsync<MixedHashVersionException>(() =>
            keyed.DeduplicateAsync("docs", new[] { new ChunkInput(0, "a different sentence") }, new DedupClaimOptions("h2")).AsTask());
        Assert.Equal(1, mixed.StoredHashVersion);
        Assert.Equal(0, mixed.StoredKeyVersion);
        Assert.Equal(2, mixed.HashVersion);
        Assert.DoesNotContain("hello world", mixed.ToString(), StringComparison.Ordinal);
        AssertSame(stored, await ReadExact(file.ConnectionString, "docs"));

        using var fresh = new TempSqlite();
        using var keysOnly = new LocalWrappedKeyProvider(WrappingKey, new SqliteWrappedKeyStore(fresh.ConnectionString));
        await keysOnly.CreateAsync("docs");
        var (only, _) = Open(keysOnly, fresh.ConnectionString);
        await only.DeduplicateAsync("docs", new[] { new ChunkInput(0, "hello world") }, new DedupClaimOptions("h"));
        var plain = RechunkPipeline.CreateBuilder()
            .UseFingerprintStore(new BackedFingerprintStore(new SqliteFingerprintBackend(fresh.ConnectionString)))
            .Build();
        await Assert.ThrowsAsync<MixedHashVersionException>(() =>
            plain.DeduplicateAsync("docs", new[] { new ChunkInput(0, "another") }, new DedupClaimOptions("h2")).AsTask());
    }

    [Fact]
    public async Task Stored_band_keys_are_not_the_raw_slices()
    {
        using var file = new TempSqlite();
        using var keys = new LocalWrappedKeyProvider(WrappingKey, new SqliteWrappedKeyStore(file.ConnectionString));
        await keys.CreateAsync("docs");
        await keys.CreateAsync("other");
        var (pipeline, store) = Open(keys, file.ConnectionString);
        var text = TestText.Paragraph(21, 300);
        var report = await pipeline.DeduplicateAsync("docs", new[] { new ChunkInput(0, text) }, new DedupClaimOptions("h"));
        await store.CommitAsync(report.Decisions[0].Claim!.ToCommit());
        var other = await pipeline.DeduplicateAsync("other", new[] { new ChunkInput(0, text) }, new DedupClaimOptions("h"));
        await store.CommitAsync(other.Decisions[0].Claim!.ToCommit());

        var options = new RechunkOptions();
        options.SimHash.Enabled = true;
        options.SimHash.MaxHammingDistance = 6;
        var fingerprint = new ChunkFingerprinter(options).Compute(text);
        var layout = new SimHashBandLayout(6);
        var raw = new HashSet<ulong>();
        for (var band = 0; band < layout.BandCount; band++)
        {
            raw.Add(layout.Slice(fingerprint.SimHash, band));
        }

        var docs = await BandKeysOf(file.ConnectionString, "docs");
        var elsewhere = await BandKeysOf(file.ConnectionString, "other");
        Assert.NotEmpty(docs);
        Assert.Equal(docs.Count, elsewhere.Count);
        Assert.All(docs, key => Assert.DoesNotContain(key, raw));
        Assert.NotEqual(docs.OrderBy(k => k).ToArray(), elsewhere.OrderBy(k => k).ToArray());
    }

    [Fact]
    public async Task Concurrent_claims_keep_one_keyed_row()
    {
        using var file = new TempSqlite();
        using (var keys = new LocalWrappedKeyProvider(WrappingKey, new SqliteWrappedKeyStore(file.ConnectionString)))
        {
            await keys.CreateAsync("docs");
        }

        var text = "hello world";
        var reports = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            var keys = new LocalWrappedKeyProvider(WrappingKey, new SqliteWrappedKeyStore(file.ConnectionString));
            var (pipeline, _) = Open(keys, file.ConnectionString);
            try
            {
                return await pipeline.DeduplicateAsync("docs", new[] { new ChunkInput(0, text) }, new DedupClaimOptions("h" + i));
            }
            finally
            {
                keys.Dispose();
            }
        }));

        var id = reports[0].Decisions[0].Id;
        Assert.All(reports, report => Assert.Equal(id, report.Decisions[0].Id));
        Assert.Equal(1, reports.Count(r => r.Decisions[0].Outcome == DedupOutcome.Unique));

        await using var connection = new SqliteConnection(file.ConnectionString);
        await connection.OpenAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM rechunk_exact WHERE scope = 'docs'";
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Memory_refuses_a_mixed_scope()
    {
        var store = new InMemoryFingerprintStore();
        var legacy = Pipeline(new RechunkOptions(), store);
        var claimed = await legacy.DeduplicateAsync("docs", new[] { new ChunkInput(0, "hello world") }, new DedupClaimOptions("h"));
        await store.CommitAsync(claimed.Decisions[0].Claim!.ToCommit());

        using var keys = new LocalWrappedKeyProvider(WrappingKey, new InMemoryWrappedKeyStore());
        await keys.CreateAsync("docs");
        var keyed = Pipeline(KeyedOptions(keys), store);
        var error = await Assert.ThrowsAsync<MixedHashVersionException>(() =>
            keyed.DeduplicateAsync("docs", new[] { new ChunkInput(0, "another sentence entirely") }, new DedupClaimOptions("h2")).AsTask());
        Assert.Equal(1, error.StoredHashVersion);
        Assert.Equal(0, error.StoredKeyVersion);
        Assert.NotNull(await store.Exact.FindAsync("docs", ExactHasher.Hash("hello world")));
        Assert.Null(await store.Exact.FindAsync("docs", ExactHasher.Hash("another sentence entirely")));
    }

    [Fact]
    public async Task A_revision_can_delete_a_keyed_chunk()
    {
        using var keys = new LocalWrappedKeyProvider(WrappingKey, new InMemoryWrappedKeyStore());
        await keys.CreateAsync("docs");
        var (pipeline, store) = Open(keys);
        var tracker = new RevisionTracker(pipeline, new InMemoryRevisionStore(), new InMemoryDeadLetterStore());
        var embedder = new ScriptedEmbedder();
        var sink = new RecordingSink();
        var kept = TestText.Paragraph(4, 40);
        var dropped = TestText.Paragraph(8, 40);
        var document = new DocumentId("doc");

        await tracker.ReviseAsync(document, "docs", new[] { new ChunkInput(0, kept), new ChunkInput(1, dropped) }, embedder, sink);
        Assert.NotNull(await store.Exact.FindAsync("docs", ExactHasher.Hash(dropped)));

        await tracker.ReviseAsync(document, "docs", new[] { new ChunkInput(0, kept) }, embedder, sink);
        Assert.Null(await store.Exact.FindAsync("docs", ExactHasher.Hash(dropped)));
        Assert.NotNull(await store.Exact.FindAsync("docs", ExactHasher.Hash(kept)));

        using var file = new TempSqlite();
        using var sqliteKeys = new LocalWrappedKeyProvider(WrappingKey, new SqliteWrappedKeyStore(file.ConnectionString));
        await sqliteKeys.CreateAsync("docs");
        var (sqlitePipeline, sqliteStore) = Open(sqliteKeys, file.ConnectionString);
        var sqliteTracker = new RevisionTracker(sqlitePipeline, new InMemoryRevisionStore(), new InMemoryDeadLetterStore());
        await sqliteTracker.ReviseAsync(document, "docs", new[] { new ChunkInput(0, kept), new ChunkInput(1, dropped) }, embedder, sink);
        await sqliteTracker.ReviseAsync(document, "docs", new[] { new ChunkInput(0, kept) }, embedder, sink);
        Assert.Null(await sqliteStore.Exact.FindAsync("docs", ExactHasher.Hash(dropped)));
        Assert.NotNull(await sqliteStore.Exact.FindAsync("docs", ExactHasher.Hash(kept)));
    }

    [Fact]
    public async Task Errors_audit_and_dead_letters_do_not_contain_the_key()
    {
        using var keys = new LocalWrappedKeyProvider(WrappingKey, new InMemoryWrappedKeyStore());
        await keys.CreateAsync("docs");
        var (pipeline, _) = Open(keys);
        var embedder = new ScriptedEmbedder { Hook = (_, _) => throw new InvalidOperationException("embed failed") };
        var sink = new RecordingSink();
        var letters = new InMemoryDeadLetterStore();
        var audit = new InMemoryAuditLog(100);
        var ingestor = new ChunkIngestor(pipeline, embedder, sink, letters, audit: new AuditWriter(audit, new InMemoryAuditOutbox(), new AuditOptions(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, TimeProvider.System));
        pipeline.Options.Failures.RetryDelay = TimeSpan.Zero;

        var result = await ingestor.IngestAsync(new IngestRequest("docs", new DocumentId("d"), new[] { new ChunkInput(0, "hello world") }));
        Assert.NotEmpty(result.DeadLetters);
        var secret = Convert.ToHexString(WrappingKey);
        Assert.DoesNotContain(secret, result.DeadLetters[0].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, result.DeadLetters[0].ToString(), StringComparison.OrdinalIgnoreCase);
        await foreach (var evt in audit.QueryAsync(new AuditQuery { Scope = "docs" }))
        {
            Assert.DoesNotContain(secret, evt.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [SkippableFact]
    public async Task Redis_keeps_the_keyed_row_and_refuses_a_legacy_write()
    {
        using var redis = new RedisTestEnvironment();
        var options = new RedisStorageOptions { KeyPrefix = redis.Prefix };
        using var keys = new LocalWrappedKeyProvider(WrappingKey, new InMemoryWrappedKeyStore());
        await keys.CreateAsync("docs");
        var rechunk = KeyedOptions(keys);
        var store = new BackedFingerprintStore(new RedisFingerprintBackend(redis.Redis, options), rechunk);
        var pipeline = Pipeline(rechunk, store);
        var report = await pipeline.DeduplicateAsync("docs", new[] { new ChunkInput(0, "hello world") }, new DedupClaimOptions("h"));
        await store.CommitAsync(report.Decisions[0].Claim!.ToCommit());

        var again = new BackedFingerprintStore(new RedisFingerprintBackend(redis.Redis, options), rechunk);
        var second = Pipeline(rechunk, again);
        var loaded = await second.DeduplicateAsync("docs", new[] { new ChunkInput(0, "hello world") });
        Assert.Equal(report.Decisions[0].Id, loaded.Decisions[0].Id);
        Assert.Equal(DedupOutcome.Duplicate, loaded.Decisions[0].Outcome);

        var legacy = new BackedFingerprintStore(new RedisFingerprintBackend(redis.Redis, options), new RechunkOptions());
        var plain = Pipeline(new RechunkOptions(), legacy);
        await Assert.ThrowsAsync<MixedHashVersionException>(() =>
            plain.DeduplicateAsync("docs", new[] { new ChunkInput(0, "other words here") }, new DedupClaimOptions("h2")).AsTask());
    }

    private static RechunkOptions KeyedOptions(IKeyProvider keys)
    {
        var options = new RechunkOptions { KeyProvider = keys };
        options.SimHash.Enabled = true;
        options.SimHash.MaxHammingDistance = 6;
        options.MinHash.Enabled = true;
        return options;
    }

    private static (RechunkPipeline Pipeline, IFingerprintStore Store) Open(IKeyProvider keys, string? sqlite = null)
    {
        var options = KeyedOptions(keys);
        IFingerprintStore store = sqlite is null
            ? new InMemoryFingerprintStore(options)
            : new BackedFingerprintStore(new SqliteFingerprintBackend(sqlite), options);
        return (Pipeline(options, store), store);
    }

    private static RechunkPipeline Pipeline(RechunkOptions options, IFingerprintStore store) =>
        new(options, new ChunkFingerprinter(options), store.Exact, null, store.SimHash, store.MinHash, store);

    private static async Task<StoredExact> ReadExact(string connectionString, string scope)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT chunk, hash_version, key_version, hash FROM rechunk_exact WHERE scope = $scope";
        command.Parameters.AddWithValue("$scope", scope);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new StoredExact(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), (byte[])reader[3]);
    }

    private static async Task<List<ulong>> BandKeysOf(string connectionString, string scope)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key FROM rechunk_band_key WHERE scope = $scope AND kind = $kind";
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$kind", (int)BandKind.SimHash);
        var keys = new List<ulong>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            keys.Add(unchecked((ulong)reader.GetInt64(0)));
        }

        return keys;
    }

    private static async Task FlipWrappedCiphertext(string connectionString, string scope)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT ciphertext FROM rechunk_wrapped_key WHERE scope = $scope";
        read.Parameters.AddWithValue("$scope", scope);
        var cipher = (byte[])(await read.ExecuteScalarAsync())!;
        cipher[0] ^= 0x01;
        await using var write = connection.CreateCommand();
        write.CommandText = "UPDATE rechunk_wrapped_key SET ciphertext = $cipher WHERE scope = $scope";
        write.Parameters.AddWithValue("$cipher", cipher);
        write.Parameters.AddWithValue("$scope", scope);
        await write.ExecuteNonQueryAsync();
    }

    private static void AssertSame(StoredExact expected, StoredExact actual)
    {
        Assert.Equal(expected.Chunk, actual.Chunk);
        Assert.Equal(expected.HashVersion, actual.HashVersion);
        Assert.Equal(expected.KeyVersion, actual.KeyVersion);
        Assert.Equal(expected.Hash, actual.Hash);
    }

    private sealed record StoredExact(string Chunk, int HashVersion, int KeyVersion, byte[] Hash);
}
