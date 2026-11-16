using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Rechunk.Core.Tests.Store;
using Rechunk.Storage.Redis;
using Rechunk.Storage.Sqlite;

namespace Rechunk.Core.Tests;

public class SubjectIndexTests
{
    private static readonly byte[] WrappingKey = Enumerable.Repeat((byte)3, 32).ToArray();

    [Fact]
    public async Task The_same_value_gets_the_same_subject_id_and_another_scope_does_not()
    {
        var alpha = await Key("alpha");
        var beta = await Key("beta");

        var first = SubjectIds.TryKey(SubjectKinds.Email, " Ada@Example.com ", alpha);
        var second = SubjectIds.TryKey("email", "ada@example.com", alpha);
        var other = SubjectIds.TryKey(SubjectKinds.Email, "ada@example.com", beta);

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.StartsWith(SubjectIds.Prefix, first!.Value.Id);
        Assert.Equal(67, first.Value.Id.Length);
        Assert.DoesNotContain("ada@example.com", first.Value.Id, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(first, SubjectIds.TryKey(SubjectKinds.Email, "ada@example.com", alpha));
        Assert.Equal(Mac(alpha, SubjectKinds.Email, "ada@example.com"), first.Value.Id);
    }

    [Theory]
    [InlineData(SubjectKinds.Email, " Ada@Example.com ", "ada@example.com")]
    [InlineData(SubjectKinds.Phone, "+1 (415) 555-2671", "+14155552671")]
    [InlineData(SubjectKinds.Phone, "415-555-2671", "4155552671")]
    [InlineData(SubjectKinds.Customer, " AbC ", "AbC")]
    public void Normalization_follows_the_kind(string kind, string raw, string expected)
    {
        Assert.Equal(expected, SubjectIds.Normalize(kind, raw));
    }

    [Fact]
    public void An_empty_subject_is_dropped()
    {
        Assert.Null(SubjectIds.Normalize(SubjectKinds.Email, "   "));
        Assert.Null(SubjectIds.Normalize(SubjectKinds.Phone, "+ -"));
        Assert.Null(SubjectIds.Normalize(SubjectKinds.Customer, " "));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task The_store_replaces_adds_lists_and_counts(string name)
    {
        await using var held = Open(name);
        var index = held.Index;
        var subject = await Email("ada@example.com");
        var other = await Customer("AbC");
        var chunk = new ChunkId("k2." + new string('a', 64));
        var second = new ChunkId("k2." + new string('b', 64));

        await index.LinkAsync("docs", chunk, new[] { subject, other });
        await index.AddAsync("docs", second, new[] { subject });
        Assert.Equal(new[] { chunk, second }, await index.ListChunksAsync("docs", subject));
        Assert.Equal(new[] { chunk }, await index.ListChunksAsync("docs", other));
        Assert.Equal(3, await index.CountAsync("docs"));
        Assert.Equal(2, await index.CountAsync("docs", subject));

        await index.LinkAsync("docs", chunk, new[] { other });
        Assert.Equal(new[] { second }, await index.ListChunksAsync("docs", subject));
        Assert.Equal(new[] { chunk }, await index.ListChunksAsync("docs", other));
        Assert.Equal(2, await index.CountAsync("docs"));

        await index.RemoveAsync("docs", second);
        Assert.Empty(await index.ListChunksAsync("docs", subject));
        Assert.Equal(1, await index.CountAsync("docs"));
        Assert.Equal(0, await index.CountAsync("other", subject));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_stored_row_does_not_contain_the_raw_value(string name)
    {
        await using var held = Open(name);
        const string EmailAddress = "ada@example.com";
        var subject = await Email(EmailAddress);
        await held.Index.LinkAsync("docs", new ChunkId("k2." + new string('c', 64)), new[] { subject });

        var dumped = await held.DumpAsync();
        Assert.DoesNotContain(EmailAddress, dumped, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(subject.Id, dumped, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_0_2_1_sqlite_file_gains_the_subject_table()
    {
        using var file = new TempSqlite();
        await using (var connection = new SqliteConnection(file.ConnectionString))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE rechunk_exact (
                    scope TEXT NOT NULL, hash BLOB NOT NULL, chunk TEXT NOT NULL,
                    committed INTEGER NOT NULL, holder TEXT NOT NULL, lease_until INTEGER NOT NULL,
                    hash_version INTEGER NOT NULL DEFAULT 1, key_version INTEGER NOT NULL DEFAULT 0,
                    text_id TEXT NOT NULL DEFAULT '', PRIMARY KEY (scope, hash)
                ) WITHOUT ROWID;
                CREATE TABLE rechunk_doc (
                    scope TEXT NOT NULL, document TEXT NOT NULL, revision INTEGER NOT NULL,
                    updated_at INTEGER NOT NULL, source TEXT, PRIMARY KEY (scope, document)
                ) WITHOUT ROWID;
                CREATE TABLE rechunk_doc_chunk (
                    scope TEXT NOT NULL, document TEXT NOT NULL, seq INTEGER NOT NULL,
                    ordinal INTEGER NOT NULL, id TEXT NOT NULL, target TEXT NOT NULL,
                    language TEXT, first_revision INTEGER NOT NULL, previous_ordinal INTEGER, replaces TEXT,
                    PRIMARY KEY (scope, document, seq)
                ) WITHOUT ROWID;
                CREATE TABLE rechunk_dead_letter (
                    id TEXT NOT NULL PRIMARY KEY, scope TEXT NOT NULL, chunk TEXT NOT NULL,
                    document TEXT, ordinal INTEGER NOT NULL, stage INTEGER NOT NULL, reason TEXT NOT NULL,
                    attempts INTEGER NOT NULL, failed_at INTEGER NOT NULL, body TEXT, language TEXT
                );
                CREATE TABLE rechunk_scope_hash (
                    scope TEXT NOT NULL PRIMARY KEY, hash_version INTEGER NOT NULL,
                    key_version INTEGER NOT NULL, migrating INTEGER NOT NULL DEFAULT 0
                ) WITHOUT ROWID;
                CREATE TABLE rechunk_wrapped_key (
                    scope TEXT NOT NULL PRIMARY KEY, version INTEGER NOT NULL, nonce BLOB NOT NULL,
                    tag BLOB NOT NULL, ciphertext BLOB NOT NULL, protector_id TEXT,
                    protector_version TEXT, wrap_algorithm TEXT
                ) WITHOUT ROWID;
                INSERT INTO rechunk_exact (scope, hash, chunk, committed, holder, lease_until, hash_version, key_version, text_id)
                VALUES ('docs', X'abcd', 'k2.old', 1, 'holder', 0, 2, 1, 'k2.old');
                """;
            command.ExecuteNonQuery();
            using var tables = connection.CreateCommand();
            tables.CommandText = "SELECT name FROM sqlite_master WHERE name = 'rechunk_subject'";
            Assert.Null(tables.ExecuteScalar());
        }

        var index = new SqliteSubjectIndex(file.ConnectionString);
        var subject = await Email("ada@example.com");
        await index.LinkAsync("docs", new ChunkId("k2." + new string('d', 64)), new[] { subject });

        await using var check = new SqliteConnection(file.ConnectionString);
        await check.OpenAsync();
        using var read = check.CreateCommand();
        read.CommandText = "SELECT chunk FROM rechunk_exact WHERE scope = 'docs'";
        Assert.Equal("k2.old", read.ExecuteScalar());
        using var linked = check.CreateCommand();
        linked.CommandText = "SELECT subject FROM rechunk_subject";
        Assert.Equal(subject.Id, linked.ExecuteScalar());
    }

    [Fact]
    public async Task The_scanner_and_an_explicit_id_are_linked_to_the_stored_chunk()
    {
        var index = new InMemorySubjectIndex();
        var keys = Keys();
        await keys.CreateAsync("docs");
        var log = new InMemoryAuditLog();
        var ingestor = Ingestor(index, keys, log, pii: true);
        const string EmailAddress = "ada@example.com";

        var result = await ingestor.IngestAsync(new IngestRequest(
            "docs",
            new DocumentId("doc-1"),
            new[]
            {
                new ChunkInput(0, "Write to " + EmailAddress + " about the handbook.")
                {
                    Subjects = new[] { new SubjectRef(SubjectKinds.Customer, "cust-1") },
                },
            }));

        Assert.Equal(ChunkStatus.Stored, result.Outcomes[0].Status);
        var email = SubjectIds.TryKey(SubjectKinds.Email, EmailAddress, await keys.GetRequiredAsync("docs"))!.Value;
        var customer = SubjectIds.TryKey(SubjectKinds.Customer, "cust-1", await keys.GetRequiredAsync("docs"))!.Value;
        Assert.Equal(new[] { result.Outcomes[0].Id }, await index.ListChunksAsync("docs", email));
        Assert.Equal(new[] { result.Outcomes[0].Id }, await index.ListChunksAsync("docs", customer));
        Assert.Equal(1, result.Outcomes[0].SubjectCounts[SubjectKinds.Email]);
        Assert.Equal(1, result.Outcomes[0].SubjectCounts[SubjectKinds.Customer]);

        var audit = log.Events.Single(e => e.Counts.Count > 0);
        var json = AuditEventJson.Serialize(audit);
        Assert.DoesNotContain(EmailAddress, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cust-1", json, StringComparison.Ordinal);
        Assert.Equal(1, audit.Counts[SubjectKinds.Email]);
    }

    [Fact]
    public async Task Reingest_replaces_links_and_a_near_duplicate_keeps_both()
    {
        var index = new InMemorySubjectIndex();
        var keys = Keys();
        await keys.CreateAsync("docs");
        var ingestor = Ingestor(index, keys, log: null, pii: false);
        var words = TestText.Words(33, 300);
        var first = string.Join(' ', words);
        var near = Edited(words, 40);

        var stored = await ingestor.IngestAsync(Request(first, new SubjectRef(SubjectKinds.Customer, "cust-1")));
        var again = await ingestor.IngestAsync(Request(first, new SubjectRef(SubjectKinds.Customer, "cust-2")));
        var close = await ingestor.IngestAsync(Request(near, new SubjectRef(SubjectKinds.Email, "bob@example.com")));

        var key = await keys.GetRequiredAsync("docs");
        var oldCustomer = SubjectIds.TryKey(SubjectKinds.Customer, "cust-1", key)!.Value;
        var customer = SubjectIds.TryKey(SubjectKinds.Customer, "cust-2", key)!.Value;
        var email = SubjectIds.TryKey(SubjectKinds.Email, "bob@example.com", key)!.Value;
        Assert.Equal(ChunkStatus.Duplicate, again.Outcomes[0].Status);
        Assert.Equal(ChunkStatus.Duplicate, close.Outcomes[0].Status);
        Assert.Equal(stored.Outcomes[0].Id, close.Outcomes[0].Target);
        Assert.Empty(await index.ListChunksAsync("docs", oldCustomer));
        Assert.Equal(new[] { stored.Outcomes[0].Id }, await index.ListChunksAsync("docs", customer));
        Assert.Equal(new[] { stored.Outcomes[0].Id }, await index.ListChunksAsync("docs", email));
    }

    [Fact]
    public async Task A_subject_on_the_request_is_linked_to_each_stored_chunk()
    {
        var index = new InMemorySubjectIndex();
        var keys = Keys();
        await keys.CreateAsync("docs");
        var ingestor = Ingestor(index, keys, log: null, pii: false);

        var result = await ingestor.IngestAsync(new IngestRequest(
            "docs",
            new DocumentId("doc-1"),
            new[] { new ChunkInput(0, "Paid leave is 30 days."), new ChunkInput(1, "The office is closed on Friday.") })
        {
            Subjects = new[] { new SubjectRef(SubjectKinds.Customer, "cust-9") },
        });

        var customer = SubjectIds.TryKey(SubjectKinds.Customer, "cust-9", await keys.GetRequiredAsync("docs"))!.Value;
        var chunks = await index.ListChunksAsync("docs", customer);
        Assert.Equal(2, result.Embedded);
        Assert.Equal(result.Outcomes.Select(o => o.Id).OrderBy(id => id.Value, StringComparer.Ordinal), chunks);
        Assert.All(result.Outcomes, o => Assert.Equal(1, o.SubjectCounts[SubjectKinds.Customer]));
    }

    [Fact]
    public async Task A_scope_with_no_key_provider_builds_no_subject_index()
    {
        var index = new InMemorySubjectIndex();
        var pipeline = RechunkPipeline.CreateBuilder()
            .UseSubjectIndex(index)
            .Build();
        var ingestor = new ChunkIngestor(pipeline, new ScriptedEmbedder(), new RecordingSink(), new InMemoryDeadLetterStore());

        var result = await ingestor.IngestAsync(Request("Paid leave is 30 days.", new SubjectRef(SubjectKinds.Customer, "cust-1")));

        Assert.Equal(ChunkStatus.Stored, result.Outcomes[0].Status);
        Assert.Empty(result.Outcomes[0].SubjectCounts);
        Assert.Equal(0, await index.CountAsync("docs"));
    }

    public static IEnumerable<object[]> Stores() =>
        new[] { new object[] { "memory" }, new object[] { "sqlite" }, new object[] { "redis" } };

    private static async Task<SubjectKey> Email(string value) =>
        SubjectIds.TryKey(SubjectKinds.Email, value, await Key("docs"))!.Value;

    private static async Task<SubjectKey> Customer(string value) =>
        SubjectIds.TryKey(SubjectKinds.Customer, value, await Key("docs"))!.Value;

    private static async Task<ScopeKey> Key(string scope)
    {
        var provider = Keys();
        return await provider.CreateAsync(scope);
    }

    private static LocalWrappedKeyProvider Keys() => new(WrappingKey.ToArray(), new InMemoryWrappedKeyStore());

    private static string Mac(ScopeKey key, string kind, string normalized)
    {
        var message = new List<byte>();
        message.AddRange(Encoding.ASCII.GetBytes(SubjectIds.Label));
        message.Add(0);
        message.AddRange(Encoding.UTF8.GetBytes(kind));
        message.Add(0);
        message.AddRange(Encoding.UTF8.GetBytes(normalized));
        using var hmac = new HMACSHA256(key.Material.ToArray());
        return SubjectIds.Prefix + Convert.ToHexString(hmac.ComputeHash(message.ToArray())).ToLowerInvariant();
    }

    private static ChunkIngestor Ingestor(ISubjectIndex index, LocalWrappedKeyProvider keys, InMemoryAuditLog? log, bool pii)
    {
        var options = new RechunkOptions { KeyProvider = keys };
        options.SimHash.Enabled = true;
        options.MinHash.Enabled = true;
        options.Pii.Enabled = pii;
        var store = new InMemoryFingerprintStore(options, keys: keys);
        var pipeline = RechunkPipeline.CreateBuilder()
            .UseSimHash()
            .UseMinHash()
            .UseKeyProvider(keys)
            .UseSubjectIndex(index)
            .UseFingerprintStore(store)
            .Configure(o => o.Pii.Enabled = pii)
            .Build();
        AuditWriter? audit = log is null ? null : new AuditWriter(log);
        return new ChunkIngestor(pipeline, new ScriptedEmbedder(), new RecordingSink(), new InMemoryDeadLetterStore(), audit: audit);
    }

    private static IngestRequest Request(string text, SubjectRef subject) =>
        new("docs", new DocumentId("doc-1"), new[] { new ChunkInput(0, text) { Subjects = new[] { subject } } });

    private static string Edited(string[] words, params int[] positions)
    {
        var copy = (string[])words.Clone();
        foreach (var p in positions)
        {
            copy[p] = "zq" + new string(p.ToString().Select(c => (char)('a' + c - '0')).ToArray());
        }

        return string.Join(' ', copy);
    }

    private static HeldIndex Open(string name)
    {
        if (name == "memory")
        {
            var memory = new InMemorySubjectIndex();
            return new HeldIndex(memory, _ => Task.FromResult(memory.StoredText()), () => ValueTask.CompletedTask);
        }

        if (name == "sqlite")
        {
            var file = new TempSqlite();
            var index = new SqliteSubjectIndex(file.ConnectionString);
            return new HeldIndex(
                index,
                async _ =>
                {
                    await using var connection = new SqliteConnection(file.ConnectionString);
                    await connection.OpenAsync();
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT scope || ' ' || chunk || ' ' || kind || ' ' || subject FROM rechunk_subject";
                    using var reader = command.ExecuteReader();
                    var text = new StringBuilder();
                    while (reader.Read())
                    {
                        text.Append(reader.GetString(0));
                        text.Append('\n');
                    }

                    return text.ToString();
                },
                () =>
                {
                    file.Dispose();
                    return ValueTask.CompletedTask;
                });
        }

        var redis = new RedisTestEnvironment();
        var store = new RedisSubjectIndex(redis.Redis, new RedisStorageOptions { KeyPrefix = redis.Prefix });
        return new HeldIndex(
            store,
            async _ =>
            {
                var database = redis.Redis.GetDatabase();
                var text = new StringBuilder();
                foreach (var endpoint in redis.Redis.GetEndPoints())
                {
                    var server = redis.Redis.GetServer(endpoint);
                    if (server.IsReplica)
                    {
                        continue;
                    }

                    foreach (var key in server.Keys(pattern: redis.Prefix + ":*"))
                    {
                        text.Append(key).Append(' ');
                        if (await database.KeyTypeAsync(key) == StackExchange.Redis.RedisType.Set)
                        {
                            foreach (var member in await database.SetMembersAsync(key))
                            {
                                text.Append(member).Append(' ');
                            }
                        }
                        else
                        {
                            text.Append(await database.StringGetAsync(key));
                        }

                        text.Append('\n');
                    }
                }

                return text.ToString();
            },
            () =>
            {
                redis.Dispose();
                return ValueTask.CompletedTask;
            });
    }

    private sealed class HeldIndex : IAsyncDisposable
    {
        private readonly Func<ISubjectIndex, Task<string>> _dump;
        private readonly Func<ValueTask> _dispose;

        public HeldIndex(ISubjectIndex index, Func<ISubjectIndex, Task<string>> dump, Func<ValueTask> dispose)
        {
            Index = index;
            _dump = dump;
            _dispose = dispose;
        }

        public ISubjectIndex Index { get; }

        public Task<string> DumpAsync() => _dump(Index);

        public ValueTask DisposeAsync() => _dispose();
    }
}
