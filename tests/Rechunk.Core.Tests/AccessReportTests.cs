using Rechunk.Core.Tests.Store;
using Rechunk.Storage.Redis;
using Rechunk.Storage.Sqlite;

namespace Rechunk.Core.Tests;

public class AccessReportTests
{
    private const string Scope = "docs";
    private const string EmailAddress = "ada@example.com";
    private const string Marker = "zebrafence";

    private static readonly byte[] WrappingKey = Enumerable.Repeat((byte)5, 32).ToArray();

    [SkippableTheory]
    [MemberData(nameof(Stores))]
    public async Task The_report_counts_sources_revisions_and_near_copies(string name)
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
        var started = clock.GetUtcNow();

        await tracker.ReviseAsync(
            new DocumentId("handbook"),
            Scope,
            new[]
            {
                new ChunkInput(0, "Write to " + EmailAddress + " about " + Marker + ".")
                {
                    Subjects = new[] { customer },
                },
            },
            new ScriptedEmbedder(),
            new RecordingSink(),
            source: "wiki");

        clock.Advance(TimeSpan.FromDays(1));
        await tracker.ReviseAsync(
            new DocumentId("notes"),
            Scope,
            new[]
            {
                new ChunkInput(0, first) { Subjects = new[] { customer } },
                new ChunkInput(1, near) { Subjects = new[] { customer } },
            },
            new ScriptedEmbedder(),
            new RecordingSink(),
            source: "inbox");

        clock.Advance(TimeSpan.FromDays(1));
        var revised = await tracker.ReviseAsync(
            new DocumentId("notes"),
            Scope,
            new[]
            {
                new ChunkInput(0, first) { Subjects = new[] { customer } },
                new ChunkInput(1, near) { Subjects = new[] { customer } },
                new ChunkInput(2, "The office closes at six."),
            },
            new ScriptedEmbedder(),
            new RecordingSink(),
            source: "inbox");
        Assert.Equal(2, revised.Revision);

        var key = await keys.GetRequiredAsync(Scope);
        var email = SubjectIds.TryKey(SubjectKinds.Email, EmailAddress, key)!.Value;
        var person = SubjectIds.TryKey(SubjectKinds.Customer, "cust-1", key)!.Value;

        var byEmail = await pipeline.AccessReportAsync(Scope, new SubjectRef(SubjectKinds.Email, " Ada@Example.com "));
        Assert.True(byEmail.Available);
        Assert.Equal(email.Id, byEmail.SubjectId);
        Assert.Equal(1, byEmail.ChunkCount);
        Assert.Equal(new[] { SubjectKinds.Email }, byEmail.Kinds);
        Assert.Equal(new[] { "wiki" }, byEmail.Sources.Select(s => s.Source));
        Assert.Equal(1, byEmail.RevisionCount);
        Assert.Equal(0, byEmail.NearCopyCount);
        Assert.Equal(started, byEmail.Earliest);
        Assert.Equal(started, byEmail.Latest);

        var byCustomer = await pipeline.AccessReportAsync(Scope, customer);
        Assert.Equal(person.Id, byCustomer.SubjectId);
        Assert.Equal(2, byCustomer.ChunkCount);
        Assert.Equal(new[] { SubjectKinds.Customer }, byCustomer.Kinds);
        Assert.Equal(2, byCustomer.RevisionCount);
        Assert.Equal(1, byCustomer.NearCopyCount);
        Assert.Equal(new[] { "inbox", "wiki" }, byCustomer.Sources.Select(s => s.Source).OrderBy(s => s, StringComparer.Ordinal));
        Assert.Equal(new[] { "handbook", "notes" }, byCustomer.Documents.Select(d => d.Document));
        Assert.Equal(1, byCustomer.Documents.Single(d => d.Document == "handbook").Revision);
        Assert.Equal(2, byCustomer.Documents.Single(d => d.Document == "notes").Revision);
        Assert.Equal(started, byCustomer.Earliest);
        Assert.Equal(clock.GetUtcNow(), byCustomer.Latest);
        Assert.All(byCustomer.Chunks, chunk => Assert.StartsWith("k2.", chunk.Value));

        var missing = await pipeline.AccessReportAsync(Scope, new SubjectRef(SubjectKinds.Email, "bob@example.com"));
        Assert.True(missing.Available);
        Assert.Equal(0, missing.ChunkCount);
        Assert.Empty(missing.Chunks);
        Assert.Empty(missing.Kinds);
        Assert.NotNull(missing.SubjectId);

        var blank = await pipeline.AccessReportAsync(Scope, new SubjectRef(SubjectKinds.Email, "  "));
        Assert.True(blank.Available);
        Assert.Null(blank.SubjectId);
        Assert.Equal(0, blank.ChunkCount);

        var dumped = byEmail + byCustomer.ToString() + missing + blank.ToString();
        foreach (var audit in log.Events.Where(e => e.Kind == AuditKind.AccessReport))
        {
            var json = AuditEventJson.Serialize(audit);
            dumped += json;
            Assert.DoesNotContain(EmailAddress, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Marker, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cust-1", json, StringComparison.Ordinal);
        }

        Assert.Contains(log.Events, e => e.Kind == AuditKind.AccessReport && e.Counts["chunks"] == 1);
        Assert.Contains(log.Events, e => e.Kind == AuditKind.AccessReport && e.Counts["chunks"] == 2);
        Assert.Contains(email.Id, dumped, StringComparison.Ordinal);
        Assert.DoesNotContain(EmailAddress, dumped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bob@example.com", dumped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Marker, dumped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cust-1", dumped, StringComparison.Ordinal);
        Assert.DoesNotContain(first, dumped, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scope_with_no_key_provider_has_no_access_report()
    {
        var pipeline = RechunkPipeline.CreateBuilder().Build();
        var report = await pipeline.AccessReportAsync(Scope, new SubjectRef(SubjectKinds.Customer, "cust-1"));

        Assert.False(report.Available);
        Assert.Null(report.SubjectId);
        Assert.Equal(0, report.ChunkCount);

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
