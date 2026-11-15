using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Rechunk.Core.Tests.Store;
using Rechunk.Storage.Redis;
using Rechunk.Storage.Sqlite;

namespace Rechunk.Core.Tests;

public class KeyedIdMigrationTests
{
    private static readonly byte[] WrappingKey = Convert.FromHexString(
        "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff");

    [Fact]
    public async Task Memory_migration_rewrites_ids_bands_and_revisions_and_a_rerun_does_nothing()
    {
        var options = Options();
        using var keys = Keys();
        await keys.CreateAsync("docs");
        var store = new InMemoryFingerprintStore(options);
        var revisions = new InMemoryRevisionStore();
        var seeded = await Seed(options, store, revisions, "docs");
        var mapped = new List<(string Old, string New)>();
        var report = await new KeyedIdMigrator(store, keys, options, revisions).RunAsync("docs", new MigrationRun { OnMapped = (o, n) => mapped.Add((o, n)) });

        Assert.True(report.Completed);
        Assert.False(report.AlreadyDone);
        Assert.True(report.FingerprintRows >= seeded.Hashes.Count);
        Assert.Equal(seeded.OldIds.Count, mapped.Select(p => p.Old).Distinct().Count());
        Assert.All(seeded.OldIds, id => Assert.Contains(mapped, p => p.Old == id));
        await AssertKeyed(options, keys, store, revisions, "docs", seeded);
        var again = await new KeyedIdMigrator(store, keys, options, revisions).RunAsync("docs", new MigrationRun { OnMapped = (_, _) => throw new InvalidOperationException("rerun") });
        Assert.True(again.AlreadyDone);
        Assert.Equal(0, again.FingerprintRows);
    }

    [Fact]
    public async Task An_interrupted_memory_run_resumes_to_the_same_ids()
    {
        var options = Options();
        using var keys = Keys();
        await keys.CreateAsync("docs");
        var interrupted = new InMemoryFingerprintStore(options);
        var clean = new InMemoryFingerprintStore(options);
        var leftRevisions = new InMemoryRevisionStore();
        var rightRevisions = new InMemoryRevisionStore();
        var seeded = await Seed(options, interrupted, leftRevisions, "docs");
        await Seed(options, clean, rightRevisions, "docs");

        await StopAfter(interrupted, leftRevisions, keys, options, "fingerprint", 1);
        await StopAfter(interrupted, leftRevisions, keys, options, "fingerprint", 1);
        await new KeyedIdMigrator(interrupted, keys, options, leftRevisions).RunAsync("docs");
        await new KeyedIdMigrator(clean, keys, options, rightRevisions).RunAsync("docs");

        foreach (var hash in seeded.Hashes)
        {
            var left = await interrupted.Exact.FindAsync("docs", hash);
            var right = await clean.Exact.FindAsync("docs", hash);
            Assert.Equal(right, left);
            Assert.True(ChunkIds.IsKeyed(left!.Value));
        }
    }

    [Fact]
    public async Task Dry_run_does_not_block_or_change_rows()
    {
        var options = Options();
        using var keys = Keys();
        await keys.CreateAsync("docs");
        var store = new InMemoryFingerprintStore(options);
        var revisions = new InMemoryRevisionStore();
        var seeded = await Seed(options, store, revisions, "docs");
        var report = await new KeyedIdMigrator(store, keys, options, revisions).RunAsync("docs", new MigrationRun { DryRun = true });
        Assert.True(report.DryRun);
        Assert.False(report.Completed);
        Assert.True(report.FingerprintRows >= 2);
        Assert.False(ChunkIds.IsKeyed((await store.Exact.FindAsync("docs", seeded.Hashes[0]))!.Value));
        var more = await Pipeline(options, store).DeduplicateAsync("docs", new[] { new ChunkInput(9, "a brand new sentence for the dry run") }, new DedupClaimOptions("h"));
        Assert.Equal(DedupOutcome.Unique, more.Decisions[0].Outcome);
    }

    [Fact]
    public async Task Writes_are_refused_until_the_scope_version_flips()
    {
        var options = Options();
        using var keys = Keys();
        await keys.CreateAsync("docs");
        var store = new InMemoryFingerprintStore(options);
        var revisions = new InMemoryRevisionStore();
        await Seed(options, store, revisions, "docs");
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

        var legacy = Pipeline(options, store);
        await Assert.ThrowsAsync<MigrationInProgressException>(() =>
            legacy.DeduplicateAsync("docs", new[] { new ChunkInput(3, TestText.Paragraph(9, 40)) }, new DedupClaimOptions("h")).AsTask());
        var keyedOptions = Options();
        keyedOptions.KeyProvider = keys;
        var keyed = new RechunkPipeline(keyedOptions, new ChunkFingerprinter(keyedOptions), store.Exact, null, store.SimHash, store.MinHash, store);
        await Assert.ThrowsAsync<MigrationInProgressException>(() =>
            keyed.DeduplicateAsync("docs", new[] { new ChunkInput(4, TestText.Paragraph(10, 40)) }, new DedupClaimOptions("h2")).AsTask());

        await new KeyedIdMigrator(store, keys, options, revisions).RunAsync("docs");
        await Assert.ThrowsAsync<MixedHashVersionException>(() =>
            legacy.DeduplicateAsync("docs", new[] { new ChunkInput(5, TestText.Paragraph(11, 40)) }, new DedupClaimOptions("h3")).AsTask());
        var ok = await keyed.DeduplicateAsync("docs", new[] { new ChunkInput(6, TestText.Paragraph(12, 40)) }, new DedupClaimOptions("h4"));
        Assert.True(ChunkIds.IsKeyed(ok.Decisions[0].Id));
    }

    [Fact]
    public async Task Sqlite_migration_matches_a_clean_run_after_interrupt_and_a_wrong_key_does_not_flip()
    {
        var options = Options();
        using var leftFile = new TempSqlite();
        using var rightFile = new TempSqlite();
        using var keys = new LocalWrappedKeyProvider(WrappingKey, new SqliteWrappedKeyStore(leftFile.ConnectionString));
        await keys.CreateAsync("docs");
        var left = Backend(leftFile.ConnectionString, options);
        var right = Backend(rightFile.ConnectionString, options);
        var leftRevisions = new SqliteRevisionStore(leftFile.ConnectionString);
        var rightRevisions = new SqliteRevisionStore(rightFile.ConnectionString);
        var seeded = await Seed(options, left.Store, leftRevisions, "docs");
        await Seed(options, right.Store, rightRevisions, "docs");
        await StopAfter(left.Store, leftRevisions, keys, options, "fingerprint", 1);
        await new KeyedIdMigrator(left.Store, keys, options, leftRevisions).RunAsync("docs");
        await new KeyedIdMigrator(right.Store, keys, options, rightRevisions).RunAsync("docs");
        foreach (var hash in seeded.Hashes)
        {
            Assert.Equal(await right.Store.Exact.FindAsync("docs", hash), await left.Store.Exact.FindAsync("docs", hash));
        }

        var reading = new BackedFingerprintStore(new SqliteFingerprintBackend(leftFile.ConnectionString), Options(keys));
        await AssertKeyed(options, keys, reading, leftRevisions, "docs", seeded);

        using var badFile = new TempSqlite();
        var badStore = Backend(badFile.ConnectionString, options).Store;
        await Seed(options, badStore, new SqliteRevisionStore(badFile.ConnectionString), "docs");
        using (var created = new LocalWrappedKeyProvider(WrappingKey, new SqliteWrappedKeyStore(badFile.ConnectionString)))
        {
            await created.CreateAsync("docs");
        }

        await Flip(badFile.ConnectionString);
        var wrong = new LocalWrappedKeyProvider(WrappingKey, new SqliteWrappedKeyStore(badFile.ConnectionString));
        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            new KeyedIdMigrator(badStore, wrong, options).RunAsync("docs").AsTask());
        await using var connection = new SqliteConnection(badFile.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM rechunk_exact WHERE hash_version = 1";
        Assert.Equal(seeded.Hashes.Count, (long)(await command.ExecuteScalarAsync())!);
        await using var busy = connection.CreateCommand();
        busy.CommandText = "SELECT COUNT(*) FROM rechunk_scope_hash WHERE migrating = 1";
        Assert.Equal(0L, (long)(await busy.ExecuteScalarAsync())!);
    }

    [SkippableFact]
    public async Task Redis_migration_rewrites_the_scope_and_a_rerun_is_a_no_op()
    {
        using var redis = new RedisTestEnvironment();
        var options = Options();
        var redisOptions = new RedisStorageOptions { KeyPrefix = redis.Prefix };
        using var keys = Keys();
        await keys.CreateAsync("docs");
        var store = new BackedFingerprintStore(new RedisFingerprintBackend(redis.Redis, redisOptions), options);
        var revisions = new RedisRevisionStore(redis.Redis, redisOptions);
        var seeded = await Seed(options, store, revisions, "docs");
        var report = await new KeyedIdMigrator(store, keys, options, revisions).RunAsync("docs");
        Assert.True(report.Completed);
        var reading = new BackedFingerprintStore(new RedisFingerprintBackend(redis.Redis, redisOptions), Options(keys));
        await AssertKeyed(options, keys, reading, revisions, "docs", seeded);
        var again = await new KeyedIdMigrator(store, keys, options, revisions).RunAsync("docs");
        Assert.True(again.AlreadyDone);
    }

    private static async Task StopAfter(IMigratableFingerprints store, IMigratableRevisions revisions, IKeyProvider keys, RechunkOptions options, string phase, int count)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new KeyedIdMigrator(store, keys, options, revisions).RunAsync("docs", new MigrationRun
            {
                OnStep = step =>
                {
                    if (step.Phase == phase && step.Count >= count)
                    {
                        throw new InvalidOperationException("stop");
                    }
                },
            }).AsTask());
    }

    private static async Task AssertKeyed(RechunkOptions options, IKeyProvider keys, IFingerprintStore store, IRevisionStore revisions, string scope, Seeded seeded)
    {
        var key = await keys.GetRequiredAsync(scope);
        for (var i = 0; i < seeded.Hashes.Count; i++)
        {
            var found = await store.Exact.FindAsync(scope, seeded.Hashes[i]);
            Assert.Equal(ChunkIds.Create(seeded.Hashes[i].Span, null, key), found);
        }

        var manifest = await revisions.GetAsync(scope, new DocumentId("doc"));
        Assert.NotNull(manifest);
        Assert.All(manifest!.Entries, entry => Assert.True(ChunkIds.IsKeyed(entry.Id)));
        var edited = TestText.WithWordChanged(21, 80, 10, "revised");
        var pipeline = new RechunkPipeline(Options(keys), new ChunkFingerprinter(Options(keys)), store.Exact, null, store.SimHash, store.MinHash, store);
        var near = await pipeline.DeduplicateAsync(scope, new[] { new ChunkInput(0, edited) });
        Assert.Equal(DedupOutcome.Duplicate, near.Decisions[0].Outcome);
        Assert.NotEqual(MatchStage.Exact, near.Decisions[0].Stage);
    }

    private static async Task<Seeded> Seed(RechunkOptions options, IFingerprintStore store, IRevisionStore revisions, string scope)
    {
        var pipeline = Pipeline(options, store);
        var texts = new[]
        {
            TestText.Paragraph(21, 80),
            TestText.Paragraph(22, 40),
            TestText.Paragraph(23, 40),
            TestText.Paragraph(24, 40),
        };
        var report = await pipeline.DeduplicateAsync(scope, texts.Select((t, i) => new ChunkInput(i, t)).ToList(), new DedupClaimOptions("seed"));
        foreach (var decision in report.Decisions)
        {
            if (decision.Claim is { } claim)
            {
                await store.CommitAsync(claim.ToCommit());
            }
        }

        var tracker = new RevisionTracker(pipeline, revisions, new InMemoryDeadLetterStore());
        await tracker.ReviseAsync(new DocumentId("doc"), scope, texts.Select((t, i) => new ChunkInput(i, t)).ToList(), new ScriptedEmbedder(), new RecordingSink());
        return new Seeded(texts.Select(t => (ReadOnlyMemory<byte>)ExactHasher.Hash(t)).ToList(), report.Decisions.Select(d => d.Id.Value).ToList());
    }

    private static RechunkOptions Options(IKeyProvider? keys = null)
    {
        var options = new RechunkOptions { KeyProvider = keys };
        options.SimHash.Enabled = true;
        options.SimHash.MaxHammingDistance = 6;
        options.MinHash.Enabled = true;
        return options;
    }

    private static LocalWrappedKeyProvider Keys() => new(WrappingKey, new InMemoryWrappedKeyStore());

    private static RechunkPipeline Pipeline(RechunkOptions options, IFingerprintStore store) =>
        new(options, new ChunkFingerprinter(options), store.Exact, null, store.SimHash, store.MinHash, store);

    private static (BackedFingerprintStore Store, SqliteFingerprintBackend Backend) Backend(string connection, RechunkOptions options)
    {
        var backend = new SqliteFingerprintBackend(connection);
        return (new BackedFingerprintStore(backend, options), backend);
    }

    private static async Task Flip(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT ciphertext FROM rechunk_wrapped_key WHERE scope = 'docs'";
        var cipher = (byte[])(await read.ExecuteScalarAsync())!;
        cipher[0] ^= 1;
        await using var write = connection.CreateCommand();
        write.CommandText = "UPDATE rechunk_wrapped_key SET ciphertext = $cipher WHERE scope = 'docs'";
        write.Parameters.AddWithValue("$cipher", cipher);
        await write.ExecuteNonQueryAsync();
    }

    private sealed record Seeded(List<ReadOnlyMemory<byte>> Hashes, List<string> OldIds);
}
