using System.Security.Cryptography;
using System.Text;

namespace Rechunk.Core.Tests.Store;

/// <summary>
/// What every fingerprint store has to do, whatever it keeps its data in. The in memory store, the SQLite backend and
/// the Redis backend each get a subclass that only says how to build one. Create can be called more than once in a
/// test, and what comes back has to see the same data, like two workers that use the same database.
/// </summary>
public abstract class FingerprintStoreContract : IDisposable
{
    protected readonly ManualTimeProvider Clock = new();
    protected const string Scope = "tenant-a";

    protected abstract IFingerprintStore Create(RechunkOptions options, TimeProvider time);

    public virtual void Dispose()
    {
    }

    private IFingerprintStore Store(RechunkOptions? options = null) => Create(options ?? Options(), Clock);

    private static RechunkOptions Options()
    {
        var options = new RechunkOptions();
        options.SimHash.Enabled = true;
        options.MinHash.Enabled = true;
        return options;
    }

    private static byte[] Hash(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    private static ClaimRequest Claim(string text, string holder, string scope = Scope, int lease = 60) =>
        new(scope, new ChunkId("id-" + text), Hash(text), holder, TimeSpan.FromSeconds(lease));

    private static CommitRequest Commit(ClaimRequest claim, ChunkId? alias = null) =>
        new(claim.Scope, claim.Id, claim.ExactHash, claim.Holder, alias);

    // ---- claim and commit lifecycle ----

    [SkippableFact]
    public async Task A_new_hash_is_claimed()
    {
        var result = await Store().ClaimAsync(Claim("a", "w1"));

        Assert.Equal(ClaimStatus.Claimed, result.Status);
        Assert.True(result.Won);
        Assert.Equal(new ChunkId("id-a"), result.Owner);
        Assert.Equal(Clock.GetUtcNow().AddSeconds(60), result.LeaseUntil);
    }

    [SkippableFact]
    public async Task A_pending_claim_is_not_seen_by_the_exact_index()
    {
        var store = Store();
        var claim = Claim("a", "w1");
        await store.ClaimAsync(claim);

        Assert.Null(await store.Exact.FindAsync(Scope, claim.ExactHash));
    }

    [SkippableFact]
    public async Task A_committed_hash_is_found_by_the_exact_index()
    {
        var store = Store();
        var claim = Claim("a", "w1");
        await store.ClaimAsync(claim);

        Assert.Equal(CommitStatus.Committed, await store.CommitAsync(Commit(claim)));
        Assert.Equal(claim.Id, await store.Exact.FindAsync(Scope, claim.ExactHash));
    }

    [SkippableFact]
    public async Task The_same_holder_asking_again_keeps_the_claim_and_extends_the_lease()
    {
        var store = Store();
        var first = await store.ClaimAsync(Claim("a", "w1", lease: 60));
        Clock.Advance(TimeSpan.FromSeconds(30));
        var again = await store.ClaimAsync(Claim("a", "w1", lease: 60));

        Assert.Equal(ClaimStatus.Renewed, again.Status);
        Assert.True(again.Won);
        Assert.Equal(first.LeaseUntil!.Value.AddSeconds(30), again.LeaseUntil);
    }

    [SkippableFact]
    public async Task Another_holder_is_told_the_chunk_is_in_flight_until_the_lease_ends()
    {
        var store = Store();
        var first = await store.ClaimAsync(Claim("a", "w1", lease: 60));

        var second = await store.ClaimAsync(Claim("a", "w2"));

        Assert.Equal(ClaimStatus.InFlight, second.Status);
        Assert.False(second.Won);
        Assert.Equal(new ChunkId("id-a"), second.Owner);
        Assert.Equal(first.LeaseUntil, second.LeaseUntil);
    }

    [SkippableFact]
    public async Task After_the_lease_ends_another_holder_takes_over_and_the_first_can_no_longer_commit()
    {
        var store = Store();
        await store.ClaimAsync(Claim("a", "w1", lease: 60));
        Clock.Advance(TimeSpan.FromSeconds(61));

        var taken = await store.ClaimAsync(Claim("a", "w2"));
        Assert.Equal(ClaimStatus.TakenOver, taken.Status);
        Assert.True(taken.Won);

        Assert.Equal(CommitStatus.NotHeld, await store.CommitAsync(Commit(Claim("a", "w1"))));
        Assert.Equal(CommitStatus.Committed, await store.CommitAsync(Commit(Claim("a", "w2"))));
    }

    [SkippableFact]
    public async Task An_expired_lease_that_nobody_took_over_can_still_be_committed()
    {
        var store = Store();
        await store.ClaimAsync(Claim("a", "w1", lease: 60));
        Clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(CommitStatus.Committed, await store.CommitAsync(Commit(Claim("a", "w1"))));
    }

    [SkippableFact]
    public async Task Claiming_a_committed_hash_says_who_owns_it()
    {
        var store = Store();
        var claim = Claim("a", "w1");
        await store.ClaimAsync(claim);
        await store.CommitAsync(Commit(claim));

        var late = await store.ClaimAsync(Claim("a", "w2"));

        Assert.Equal(ClaimStatus.AlreadyCommitted, late.Status);
        Assert.False(late.Won);
        Assert.Equal(claim.Id, late.Owner);
    }

    [SkippableFact]
    public async Task Committing_twice_is_harmless()
    {
        var store = Store();
        var claim = Claim("a", "w1");
        await store.ClaimAsync(claim);
        await store.CommitAsync(Commit(claim));

        Assert.Equal(CommitStatus.AlreadyCommitted, await store.CommitAsync(Commit(claim)));
    }

    [SkippableFact]
    public async Task Committing_without_a_claim_or_with_the_wrong_holder_does_nothing()
    {
        var store = Store();
        Assert.Equal(CommitStatus.NotHeld, await store.CommitAsync(Commit(Claim("a", "w1"))));

        await store.ClaimAsync(Claim("a", "w1"));
        Assert.Equal(CommitStatus.NotHeld, await store.CommitAsync(Commit(Claim("a", "w2"))));
        Assert.Null(await store.Exact.FindAsync(Scope, Hash("a")));
    }

    [SkippableFact]
    public async Task Releasing_drops_a_pending_claim_so_the_next_try_wins()
    {
        var store = Store();
        var claim = Claim("a", "w1");
        await store.ClaimAsync(claim);

        Assert.True(await store.ReleaseAsync(new ReleaseRequest(Scope, claim.Id, claim.ExactHash, "w1")));
        Assert.Null(await store.Exact.FindAsync(Scope, claim.ExactHash));

        var next = await store.ClaimAsync(Claim("a", "w2"));
        Assert.Equal(ClaimStatus.Claimed, next.Status);
    }

    [SkippableFact]
    public async Task Releasing_somebody_elses_claim_or_a_committed_one_does_nothing()
    {
        var store = Store();
        var claim = Claim("a", "w1");
        await store.ClaimAsync(claim);

        Assert.False(await store.ReleaseAsync(new ReleaseRequest(Scope, claim.Id, claim.ExactHash, "w2")));
        Assert.Equal(ClaimStatus.InFlight, (await store.ClaimAsync(Claim("a", "w2"))).Status);

        await store.CommitAsync(Commit(claim));
        Assert.False(await store.ReleaseAsync(new ReleaseRequest(Scope, claim.Id, claim.ExactHash, "w1")));
        Assert.Equal(claim.Id, await store.Exact.FindAsync(Scope, claim.ExactHash));
    }

    [SkippableFact]
    public async Task A_chunk_committed_as_an_alias_resolves_to_the_chunk_it_stands_for()
    {
        var store = Store();
        var claim = Claim("b", "w1");
        await store.ClaimAsync(claim);
        var canonical = new ChunkId("id-a");

        await store.CommitAsync(Commit(claim, canonical));

        Assert.Equal(canonical, await store.Exact.FindAsync(Scope, claim.ExactHash));
        Assert.Equal(canonical, (await store.ClaimAsync(Claim("b", "w2"))).Owner);
    }

    [SkippableFact]
    public async Task The_exact_index_can_remove_a_hash()
    {
        var store = Store();
        var claim = Claim("a", "w1");
        await store.ClaimAsync(claim);
        await store.CommitAsync(Commit(claim));

        Assert.True(await store.Exact.RemoveAsync(Scope, claim.ExactHash));
        Assert.Null(await store.Exact.FindAsync(Scope, claim.ExactHash));
        Assert.False(await store.Exact.RemoveAsync(Scope, claim.ExactHash));
        Assert.Equal(ClaimStatus.Claimed, (await store.ClaimAsync(Claim("a", "w2"))).Status);
    }

    [SkippableFact]
    public async Task Scopes_do_not_see_each_other()
    {
        var store = Store();
        var a = Claim("a", "w1", "tenant-a");
        var b = Claim("a", "w2", "tenant-b");

        Assert.Equal(ClaimStatus.Claimed, (await store.ClaimAsync(a)).Status);
        Assert.Equal(ClaimStatus.Claimed, (await store.ClaimAsync(b)).Status);
        await store.CommitAsync(Commit(a));

        Assert.NotNull(await store.Exact.FindAsync("tenant-a", a.ExactHash));
        Assert.Null(await store.Exact.FindAsync("tenant-b", a.ExactHash));
    }

    [SkippableFact]
    public async Task A_scope_with_braces_and_odd_characters_works_and_stays_separate()
    {
        var store = Store();
        var odd = Claim("a", "w1", "t{1}%x:y");
        var plain = Claim("a", "w1", "t");
        await store.ClaimAsync(odd);
        await store.CommitAsync(Commit(odd));

        Assert.NotNull(await store.Exact.FindAsync("t{1}%x:y", odd.ExactHash));
        Assert.Null(await store.Exact.FindAsync("t", plain.ExactHash));
    }

    [SkippableFact]
    public async Task A_second_store_on_the_same_data_sees_the_same_claims()
    {
        var one = Store();
        var two = Store();
        var claim = Claim("a", "w1");

        await one.ClaimAsync(claim);
        Assert.Equal(ClaimStatus.InFlight, (await two.ClaimAsync(Claim("a", "w2"))).Status);

        await one.CommitAsync(Commit(claim));
        Assert.Equal(claim.Id, await two.Exact.FindAsync(Scope, claim.ExactHash));
    }

    // ---- near duplicates through the pipeline ----

    private static readonly string[] Long = TestText.Words(33, 300);

    private static string Text(string[] words) => string.Join(' ', words);

    private static string Edited(string[] words, int position)
    {
        var copy = (string[])words.Clone();
        copy[position] = "zq" + new string(position.ToString().Select(c => (char)('a' + c - '0')).ToArray());
        return string.Join(' ', copy);
    }

    private static RechunkPipeline Worker(IFingerprintStore store, DuplicatePolicy policy = DuplicatePolicy.Skip) =>
        RechunkPipeline.CreateBuilder().UseSimHash().UseMinHash().OnDuplicate(policy).UseFingerprintStore(store).Build();

    private static IReadOnlyList<ChunkInput> Chunks(params string[] texts) => texts.Select((t, i) => new ChunkInput(i, t)).ToList();

    private static async Task CommitAll(IFingerprintStore store, DedupReport report)
    {
        foreach (var claim in report.Claims)
        {
            await store.CommitAsync(claim.ToCommit());
        }
    }

    [SkippableFact]
    public async Task A_second_worker_sees_a_committed_chunk_as_an_exact_duplicate()
    {
        var one = Store();
        var two = Store();

        var first = await Worker(one).DeduplicateAsync(Scope, Chunks(Text(Long)), new DedupClaimOptions("m1"));
        Assert.Equal(DedupOutcome.Unique, first.Decisions[0].Outcome);
        await CommitAll(one, first);

        var second = await Worker(two).DeduplicateAsync(Scope, Chunks("  " + Text(Long).Replace(" ", "  ")), new DedupClaimOptions("m2"));
        Assert.Equal(DedupOutcome.Duplicate, second.Decisions[0].Outcome);
        Assert.Equal(MatchStage.Exact, second.Decisions[0].Stage);
        Assert.Equal(first.Decisions[0].Id, second.Decisions[0].DuplicateOf);
    }

    [SkippableFact]
    public async Task A_second_worker_does_not_embed_a_chunk_that_is_pending_elsewhere()
    {
        var one = Store();
        var two = Store();

        var first = await Worker(one).DeduplicateAsync(Scope, Chunks(Text(Long)), new DedupClaimOptions("m1"));
        var second = await Worker(two).DeduplicateAsync(Scope, Chunks(Text(Long)), new DedupClaimOptions("m2"));

        Assert.Single(first.Claims);
        Assert.Equal(DedupOutcome.InFlight, second.Decisions[0].Outcome);
        Assert.Empty(second.Claims);
        Assert.Equal(1, second.InFlightCount);
    }

    [SkippableFact]
    public async Task A_near_duplicate_is_found_by_SimHash_and_MinHash_once_the_first_chunk_is_committed()
    {
        var one = Store();
        var two = Store();

        var first = await Worker(one).DeduplicateAsync(Scope, Chunks(Text(Long)), new DedupClaimOptions("m1"));
        await CommitAll(one, first);

        var second = await Worker(two).DeduplicateAsync(Scope, Chunks(Edited(Long, 100)), new DedupClaimOptions("m2"));

        var decision = second.Decisions[0];
        Assert.Equal(DedupOutcome.Duplicate, decision.Outcome);
        Assert.Equal(MatchStage.SimHash, decision.Stage);
        Assert.Equal(first.Decisions[0].Id, decision.DuplicateOf);
        Assert.NotNull(decision.EstimatedJaccard);
    }

    [SkippableFact]
    public async Task A_near_duplicate_is_not_matched_while_the_first_chunk_is_still_pending()
    {
        var one = Store();
        var two = Store();

        await Worker(one).DeduplicateAsync(Scope, Chunks(Text(Long)), new DedupClaimOptions("m1"));
        var second = await Worker(two).DeduplicateAsync(Scope, Chunks(Edited(Long, 100)), new DedupClaimOptions("m2"));

        Assert.Equal(DedupOutcome.Unique, second.Decisions[0].Outcome);
        Assert.Single(second.Claims);
    }

    [SkippableFact]
    public async Task A_released_chunk_leaves_nothing_behind_in_any_index()
    {
        var one = Store();
        var two = Store();

        var first = await Worker(one).DeduplicateAsync(Scope, Chunks(Text(Long)), new DedupClaimOptions("m1"));
        foreach (var claim in first.Claims)
        {
            await one.ReleaseAsync(claim.ToRelease());
        }

        var again = await Worker(two).DeduplicateAsync(Scope, Chunks(Text(Long), Edited(Long, 100)), new DedupClaimOptions("m2"));

        // the first text is new again, the edited one is only close to it inside this batch
        Assert.Equal(DedupOutcome.Unique, again.Decisions[0].Outcome);
        Assert.Equal(MatchStage.SimHash, again.Decisions[1].Stage);
        Assert.Equal(again.Decisions[0].Id, again.Decisions[1].DuplicateOf);
    }

    [SkippableFact]
    public async Task Committing_again_after_a_half_finished_commit_repairs_the_near_indexes()
    {
        var one = Store();
        var two = Store();

        var first = await Worker(one).DeduplicateAsync(Scope, Chunks(Text(Long)), new DedupClaimOptions("m1"));
        var claim = first.Claims.Single();

        // the hash row is final but the near entries were never written, as after a crash in the middle of a commit
        await one.CommitAsync(new CommitRequest(claim.Scope, claim.Id, claim.ExactHash, claim.Holder));
        var before = await Worker(two).DeduplicateAsync(Scope, Chunks(Edited(Long, 100)), new DedupClaimOptions("m2"));
        Assert.Equal(DedupOutcome.Unique, before.Decisions[0].Outcome);
        await two.ReleaseAsync(before.Claims.Single().ToRelease());

        Assert.Equal(CommitStatus.AlreadyCommitted, await one.CommitAsync(claim.ToCommit()));

        var after = await Worker(two).DeduplicateAsync(Scope, Chunks(Edited(Long, 100)), new DedupClaimOptions("m3"));
        Assert.Equal(DedupOutcome.Duplicate, after.Decisions[0].Outcome);
        Assert.Equal(claim.Id, after.Decisions[0].DuplicateOf);
    }

    [SkippableFact]
    public async Task Near_duplicates_of_other_scopes_are_not_matched()
    {
        var store = Store();
        var worker = Worker(store);

        var first = await worker.DeduplicateAsync("tenant-a", Chunks(Text(Long)), new DedupClaimOptions("m1"));
        await CommitAll(store, first);

        var other = await worker.DeduplicateAsync("tenant-b", Chunks(Edited(Long, 100)), new DedupClaimOptions("m2"));
        Assert.Equal(DedupOutcome.Unique, other.Decisions[0].Outcome);
    }

    [SkippableFact]
    public async Task A_near_duplicate_in_the_same_batch_waits_for_the_first_chunk_and_is_committed_with_it()
    {
        var store = Store();
        var worker = Worker(store);

        var report = await worker.DeduplicateAsync(Scope, Chunks(Text(Long), Edited(Long, 100)), new DedupClaimOptions("m1"));

        Assert.Equal(DedupOutcome.Unique, report.Decisions[0].Outcome);
        Assert.Equal(DedupOutcome.Duplicate, report.Decisions[1].Outcome);
        var alias = report.Decisions[1].Claim!;
        Assert.Equal(report.Decisions[0].Id, alias.AliasOf);

        // the alias is pending until the real chunk is, and then it points at it
        Assert.Null(await store.Exact.FindAsync(Scope, alias.ExactHash));
        await CommitAll(store, report);
        Assert.Equal(report.Decisions[0].Id, await store.Exact.FindAsync(Scope, alias.ExactHash));
    }

    [SkippableFact]
    public async Task The_same_text_twice_in_a_batch_is_embedded_once()
    {
        var store = Store();

        var report = await Worker(store).DeduplicateAsync(Scope, Chunks(Text(Long), Text(Long)), new DedupClaimOptions("m1"));

        Assert.Equal(DedupOutcome.Unique, report.Decisions[0].Outcome);
        Assert.Equal(DedupOutcome.Duplicate, report.Decisions[1].Outcome);
        Assert.Single(report.Claims);
    }

    [SkippableFact]
    public async Task Retrying_a_batch_with_the_same_holder_gets_the_same_claims_back()
    {
        var store = Store();
        var worker = Worker(store);

        var first = await worker.DeduplicateAsync(Scope, Chunks(Text(Long)), new DedupClaimOptions("m1"));
        var retry = await worker.DeduplicateAsync(Scope, Chunks(Text(Long)), new DedupClaimOptions("m1"));

        Assert.Equal(DedupOutcome.Unique, retry.Decisions[0].Outcome);
        Assert.Equal(first.Claims.Single().Id, retry.Claims.Single().Id);
    }

    // ---- many callers at once ----

    [SkippableFact]
    public async Task Many_parallel_claimers_of_one_hash_give_exactly_one_winner()
    {
        var stores = Enumerable.Range(0, 4).Select(_ => Store()).ToArray();
        var gate = new TaskCompletionSource();

        var tasks = Enumerable.Range(0, 64).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            return await stores[i % stores.Length].ClaimAsync(Claim("hot", "worker-" + i));
        })).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r.Won));
        Assert.Equal(1, results.Count(r => r.Status == ClaimStatus.Claimed));
        Assert.Equal(63, results.Count(r => r.Status == ClaimStatus.InFlight));
        Assert.All(results, r => Assert.Equal(new ChunkId("id-hot"), r.Owner));
    }

    [SkippableFact]
    public async Task Parallel_claimers_of_many_hashes_give_one_winner_for_each()
    {
        var stores = Enumerable.Range(0, 4).Select(_ => Store()).ToArray();
        var gate = new TaskCompletionSource();
        const int hashes = 20;
        const int claimersPerHash = 8;

        var tasks = Enumerable.Range(0, hashes * claimersPerHash).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            var hash = i % hashes;
            var result = await stores[i % stores.Length].ClaimAsync(Claim("h" + hash, "worker-" + i));
            return (hash, result);
        })).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        foreach (var group in results.GroupBy(r => r.hash))
        {
            Assert.Equal(1, group.Count(r => r.result.Won));
        }
    }

    [SkippableFact]
    public async Task Parallel_workers_with_the_same_batch_claim_each_chunk_once_in_total()
    {
        var stores = Enumerable.Range(0, 4).Select(_ => Store()).ToArray();
        var texts = Enumerable.Range(0, 10).Select(i => Text(TestText.Words(500 + i, 120))).ToArray();
        var gate = new TaskCompletionSource();

        var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            var worker = Worker(stores[i % stores.Length]);
            return await worker.DeduplicateAsync(Scope, Chunks(texts), new DedupClaimOptions("msg-" + i));
        })).ToArray();
        gate.SetResult();
        var reports = await Task.WhenAll(tasks);

        Assert.Equal(texts.Length, reports.Sum(r => r.Claims.Count()));
        var claimedIds = reports.SelectMany(r => r.Claims).Select(c => c.Id.Value).ToList();
        Assert.Equal(claimedIds.Count, claimedIds.Distinct().Count());
    }

    [SkippableFact]
    public async Task Parallel_commits_of_different_chunks_all_land_in_the_near_indexes()
    {
        var store = Store();
        var worker = Worker(store);
        var texts = Enumerable.Range(0, 12).Select(i => Text(TestText.Words(900 + i, 200))).ToArray();
        var report = await worker.DeduplicateAsync(Scope, Chunks(texts), new DedupClaimOptions("m1"));
        Assert.Equal(texts.Length, report.Claims.Count());

        await Task.WhenAll(report.Claims.Select(c => Task.Run(async () => await store.CommitAsync(c.ToCommit()))));

        var other = Worker(Store());
        var edited = texts.Select(t => t.Replace(t.Split(' ')[100], "zqa")).ToArray();
        var again = await other.DeduplicateAsync(Scope, Chunks(edited), new DedupClaimOptions("m2"));
        Assert.All(again.Decisions, d => Assert.Equal(DedupOutcome.Duplicate, d.Outcome));
    }

    // ---- the whole ingest on top of the store ----

    [SkippableFact]
    public async Task A_chunk_that_failed_to_embed_is_not_marked_as_seen()
    {
        var store = Store();
        var deadLetters = new InMemoryDeadLetterStore();
        var sink = new RecordingSink();
        var embedder = new ScriptedEmbedder { Hook = (_, _) => throw new InvalidOperationException("model is down") };
        var ingestor = Ingestor(store, embedder, sink, deadLetters);

        var failed = await ingestor.IngestAsync(Request("msg-1", Text(Long)));
        Assert.Equal(1, failed.DeadLetterCount);
        Assert.Empty(sink.StoredIds);

        embedder.Hook = null;
        var retried = await ingestor.IngestAsync(Request("msg-2", Text(Long)));

        Assert.Equal(1, retried.Embedded);
        Assert.Equal(0, retried.Duplicates);
        Assert.Single(sink.StoredIds);
    }

    [SkippableFact]
    public async Task A_chunk_that_failed_to_write_is_not_marked_as_seen_and_its_pending_claim_is_gone()
    {
        var store = Store();
        var sink = new RecordingSink { Hook = (_, _) => throw new TimeoutException("vector store timed out") };
        var ingestor = Ingestor(store, new ScriptedEmbedder(), sink, new InMemoryDeadLetterStore());

        var failed = await ingestor.IngestAsync(Request("msg-1", Text(Long)));
        Assert.Equal(DeadLetterStage.VectorWrite, failed.DeadLetters.Single().Stage);

        // nobody has to wait for a lease: the claim was released
        var claim = await store.ClaimAsync(Claim(Text(Long), "other"));
        Assert.Equal(ClaimStatus.Claimed, claim.Status);
    }

    [SkippableFact]
    public async Task Ingesting_the_same_message_twice_stores_each_chunk_once()
    {
        var store = Store();
        var sink = new RecordingSink();
        var embedder = new ScriptedEmbedder();
        var ingestor = Ingestor(store, embedder, sink, new InMemoryDeadLetterStore());

        var first = await ingestor.IngestAsync(Request("msg-1", Text(Long), Text(TestText.Words(7, 100))));
        var again = await ingestor.IngestAsync(Request("msg-1", Text(Long), Text(TestText.Words(7, 100))));

        Assert.Equal(2, first.Embedded);
        Assert.Equal(0, again.Embedded);
        Assert.Equal(2, again.Duplicates);
        Assert.Equal(2, sink.StoredIds.Count);
    }

    private ChunkIngestor Ingestor(IFingerprintStore store, ScriptedEmbedder embedder, RecordingSink sink, IDeadLetterStore deadLetters)
    {
        var pipeline = RechunkPipeline.CreateBuilder()
            .UseSimHash().UseMinHash()
            .UseFingerprintStore(store)
            .Configure(o => { o.Failures.MaxAttempts = 2; o.Failures.RetryDelay = TimeSpan.Zero; })
            .Build();
        return new ChunkIngestor(pipeline, embedder, sink, deadLetters, time: Clock);
    }

    private static IngestRequest Request(string holder, params string[] texts) =>
        new(Scope, new DocumentId("doc-1"), Chunks(texts), holder);
}
