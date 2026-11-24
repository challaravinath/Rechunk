using Rechunk.Core.Tests.Store;
using Rechunk.Storage.Redis;
using Rechunk.Storage.Sqlite;

namespace Rechunk.Core.Tests;

public class EraseSubjectTests
{
    private const string Scope = "docs";
    private const string EmailAddress = "ada@example.com";
    private const string PhoneText = "+1 415 555 2671";
    private const string Marker = "zebrafence";

    private static readonly byte[] WrappingKey = Enumerable.Repeat((byte)6, 32).ToArray();

    [SkippableTheory]
    [MemberData(nameof(Stores))]
    public async Task Erase_removes_the_subject_and_signs_a_receipt(string name)
    {
        var clock = new ManualTimeProvider();
        var keys = Keys();
        await keys.CreateAsync(Scope);
        var options = new RechunkOptions { KeyProvider = keys };
        options.SimHash.Enabled = true;
        options.MinHash.Enabled = true;
        options.Pii.Enabled = true;
        await using var held = Open(name, options, clock);
        var log = new InMemoryAuditLog();
        var (pipeline, tracker) = Build(held, keys, options, log, clock);

        var words = TestText.Words(33, 300);
        var first = string.Join(' ', words);
        var near = Edited(words, 40);
        var customer = new SubjectRef(SubjectKinds.Customer, "cust-1");
        var other = new SubjectRef(SubjectKinds.Customer, "other-9");
        var embedder = new ScriptedEmbedder();
        var sink = new RecordingSink();

        await tracker.ReviseAsync(
            new DocumentId("handbook"),
            Scope,
            new[]
            {
                new ChunkInput(0, "Write to " + EmailAddress + " or " + PhoneText + " about " + Marker + ".")
                {
                    Subjects = new[] { customer },
                },
            },
            embedder,
            sink,
            source: "wiki");
        await tracker.ReviseAsync(
            new DocumentId("notes"),
            Scope,
            new[]
            {
                new ChunkInput(0, first) { Subjects = new[] { customer } },
                new ChunkInput(1, near) { Subjects = new[] { customer } },
            },
            embedder,
            sink,
            source: "inbox");
        await tracker.ReviseAsync(
            new DocumentId("notes"),
            Scope,
            new[]
            {
                new ChunkInput(0, first) { Subjects = new[] { customer } },
                new ChunkInput(1, near) { Subjects = new[] { customer } },
                new ChunkInput(2, "The office closes at six."),
            },
            embedder,
            sink,
            source: "inbox");
        await tracker.ReviseAsync(
            new DocumentId("warehouse"),
            Scope,
            new[] { new ChunkInput(0, "The warehouse door faces the yard.") { Subjects = new[] { other } } },
            embedder,
            sink,
            source: "yard");

        var beforeEmail = await pipeline.AccessReportAsync(Scope, new SubjectRef(SubjectKinds.Email, EmailAddress));
        var beforePhone = await pipeline.AccessReportAsync(Scope, new SubjectRef(SubjectKinds.Phone, PhoneText));
        var beforeCustomer = await pipeline.AccessReportAsync(Scope, customer);
        var beforeOther = await pipeline.AccessReportAsync(Scope, other);
        Assert.Equal(1, beforeEmail.ChunkCount);
        Assert.Equal(1, beforePhone.ChunkCount);
        Assert.Equal(2, beforeCustomer.ChunkCount);
        Assert.Equal(1, beforeCustomer.NearCopyCount);
        Assert.Equal(1, beforeOther.ChunkCount);

        var unknown = await pipeline.EraseSubjectAsync(Scope, new SubjectRef(SubjectKinds.Email, "nobody@example.com"));
        Assert.True(unknown.Available);
        Assert.Equal(0, unknown.ChunksRemoved);
        Assert.Equal(0, unknown.ReferencesRemoved);
        Assert.Equal(0, unknown.SubjectLinksRemoved);
        Assert.True(await pipeline.VerifyErasureReceiptAsync(Scope, unknown));
        Assert.Equal(2, (await pipeline.AccessReportAsync(Scope, customer)).ChunkCount);

        var receipt = await pipeline.EraseSubjectAsync(Scope, customer);
        Assert.True(receipt.Available);
        Assert.Equal(beforeCustomer.SubjectId, receipt.SubjectId);
        Assert.Equal(2, receipt.ChunksRemoved);
        Assert.True(receipt.ReferencesRemoved >= 1);
        Assert.Equal(2, receipt.RevisionsAffected);
        Assert.True(receipt.SubjectLinksRemoved >= 2);
        Assert.Equal(1, receipt.KeyVersion);
        Assert.Contains(beforeEmail.Chunks[0].Value, receipt.ChunkIds);
        Assert.True(await pipeline.VerifyErasureReceiptAsync(Scope, receipt));
        Assert.False(await pipeline.VerifyErasureReceiptAsync(Scope, receipt with { ChunksRemoved = receipt.ChunksRemoved + 1 }));
        Assert.False(await pipeline.VerifyErasureReceiptAsync(Scope, receipt with { Timestamp = receipt.Timestamp.AddMinutes(1) }));
        Assert.False(await pipeline.VerifyErasureReceiptAsync(Scope, receipt with { SubjectId = "s1." + new string('a', 64) }));
        Assert.False(await pipeline.VerifyErasureReceiptAsync(Scope, receipt with { Scope = "other" }));
        var flipped = receipt.ChunkIds.ToList();
        flipped[0] = flipped[0][..^1] + (flipped[0][^1] == 'a' ? "b" : "a");
        Assert.False(await pipeline.VerifyErasureReceiptAsync(Scope, receipt with { ChunkIds = flipped }));

        Assert.Equal(0, (await pipeline.AccessReportAsync(Scope, customer)).ChunkCount);
        Assert.Equal(0, (await pipeline.AccessReportAsync(Scope, new SubjectRef(SubjectKinds.Email, EmailAddress))).ChunkCount);
        Assert.Equal(0, (await pipeline.AccessReportAsync(Scope, new SubjectRef(SubjectKinds.Phone, PhoneText))).ChunkCount);
        Assert.Equal(1, (await pipeline.AccessReportAsync(Scope, other)).ChunkCount);

        var again = await pipeline.EraseSubjectAsync(Scope, customer);
        Assert.True(again.Available);
        Assert.Equal(0, again.ChunksRemoved);
        Assert.Equal(0, again.ReferencesRemoved);
        Assert.Equal(0, again.SubjectLinksRemoved);
        Assert.NotEqual(receipt.Id, again.Id);
        Assert.True(await pipeline.VerifyErasureReceiptAsync(Scope, again));
        Assert.True(await pipeline.VerifyErasureReceiptAsync(Scope, receipt));

        var fresh = await pipeline.DeduplicateAsync(Scope, new[] { new ChunkInput(0, "Write to " + EmailAddress + " or " + PhoneText + " about " + Marker + ".") });
        var freshNear = await pipeline.DeduplicateAsync(Scope, new[] { new ChunkInput(0, near) });
        Assert.Equal(DedupOutcome.Unique, fresh.Decisions[0].Outcome);
        Assert.Equal(DedupOutcome.Unique, freshNear.Decisions[0].Outcome);

        var dumped = receipt.ToString() + unknown + again;
        foreach (var audit in log.Events.Where(e => e.Kind == AuditKind.SubjectErased))
        {
            var json = AuditEventJson.Serialize(audit);
            dumped += json;
            Assert.False(json.Contains(EmailAddress, StringComparison.OrdinalIgnoreCase));
            Assert.False(json.Contains(PhoneText, StringComparison.Ordinal));
            Assert.False(json.Contains(Marker, StringComparison.OrdinalIgnoreCase));
            Assert.False(json.Contains("cust-1", StringComparison.Ordinal));
            Assert.False(json.Contains("other-9", StringComparison.Ordinal));
            Assert.False(json.Contains(first, StringComparison.Ordinal));
        }

        Assert.Contains(log.Events, e => e.Kind == AuditKind.SubjectErased && e.Id == receipt.Id && e.Counts["chunks"] == 2);
        Assert.False(dumped.Contains(EmailAddress, StringComparison.OrdinalIgnoreCase));
        Assert.False(dumped.Contains(PhoneText, StringComparison.Ordinal));
        Assert.False(dumped.Contains(Marker, StringComparison.OrdinalIgnoreCase));
        Assert.False(dumped.Contains("cust-1", StringComparison.Ordinal));
        Assert.False(dumped.Contains("other-9", StringComparison.Ordinal));
        Assert.False(dumped.Contains(first, StringComparison.Ordinal));
        Assert.False(dumped.Contains(near, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_scope_with_no_key_provider_cannot_erase_a_subject()
    {
        var pipeline = RechunkPipeline.CreateBuilder().Build();
        var receipt = await pipeline.EraseSubjectAsync(Scope, new SubjectRef(SubjectKinds.Customer, "cust-1"));

        Assert.False(receipt.Available);
        Assert.Null(receipt.Signature);
        Assert.Null(receipt.SubjectId);
        Assert.False(await pipeline.VerifyErasureReceiptAsync(Scope, receipt));

        var dedup = await pipeline.DeduplicateAsync(Scope, new[] { new ChunkInput(0, "Paid leave is 30 days.") });
        Assert.Equal(DedupOutcome.Unique, dedup.Decisions[0].Outcome);
    }

    public static IEnumerable<object[]> Stores() =>
        new[] { new object[] { "memory" }, new object[] { "sqlite" }, new object[] { "redis" } };

    private static LocalWrappedKeyProvider Keys() => new(WrappingKey.ToArray(), new InMemoryWrappedKeyStore());

    private static (RechunkPipeline Pipeline, RevisionTracker Tracker) Build(
        Held held, LocalWrappedKeyProvider keys, RechunkOptions options, InMemoryAuditLog log, ManualTimeProvider clock)
    {
        var pipeline = new RechunkPipeline(
            options,
            new ChunkFingerprinter(options),
            held.Fingerprints.Exact,
            null,
            held.Fingerprints.SimHash,
            held.Fingerprints.MinHash,
            held.Fingerprints,
            keys,
            held.Subjects,
            held.Revisions,
            new AuditWriter(log, time: clock));
        var tracker = new RevisionTracker(pipeline, held.Revisions, new InMemoryDeadLetterStore(), log, time: clock);
        return (pipeline, tracker);
    }

    private static Held Open(string name, RechunkOptions options, TimeProvider clock)
    {
        if (name == "memory")
        {
            return new Held(
                new InMemorySubjectIndex(),
                new InMemoryFingerprintStore(options, clock, options.KeyProvider),
                new InMemoryRevisionStore(),
                () => ValueTask.CompletedTask);
        }

        if (name == "sqlite")
        {
            var file = new TempSqlite();
            return new Held(
                new SqliteSubjectIndex(file.ConnectionString),
                new BackedFingerprintStore(new SqliteFingerprintBackend(file.ConnectionString), options, clock),
                new SqliteRevisionStore(file.ConnectionString),
                () =>
                {
                    file.Dispose();
                    return ValueTask.CompletedTask;
                });
        }

        var redis = new RedisTestEnvironment();
        var redisOptions = new RedisStorageOptions { KeyPrefix = redis.Prefix };
        return new Held(
            new RedisSubjectIndex(redis.Redis, redisOptions),
            new BackedFingerprintStore(new RedisFingerprintBackend(redis.Redis, redisOptions), options, clock),
            new RedisRevisionStore(redis.Redis, redisOptions),
            () =>
            {
                redis.Dispose();
                return ValueTask.CompletedTask;
            });
    }

    private static string Edited(string[] words, params int[] positions)
    {
        var copy = (string[])words.Clone();
        foreach (var p in positions)
        {
            copy[p] = "zq" + new string(p.ToString().Select(c => (char)('a' + c - '0')).ToArray());
        }

        return string.Join(' ', copy);
    }

    private sealed class Held : IAsyncDisposable
    {
        private readonly Func<ValueTask> _dispose;

        public Held(ISubjectIndex subjects, IFingerprintStore fingerprints, IRevisionStore revisions, Func<ValueTask> dispose)
        {
            Subjects = subjects;
            Fingerprints = fingerprints;
            Revisions = revisions;
            _dispose = dispose;
        }

        public ISubjectIndex Subjects { get; }

        public IFingerprintStore Fingerprints { get; }

        public IRevisionStore Revisions { get; }

        public ValueTask DisposeAsync() => _dispose();
    }
}
