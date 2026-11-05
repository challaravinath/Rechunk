namespace Rechunk.Core.Tests.Store;

/// <summary>
/// What every revision store has to do. The in memory store, SQLite and Redis each get a subclass that says how to build
/// one. Create can be called more than once in a test, and what comes back has to see the same data, like two workers
/// that use the same database.
/// </summary>
public abstract class RevisionStoreContract : IDisposable
{
    protected const string Scope = "tenant-a";
    private static readonly DocumentId Doc = new("doc-1");
    private static readonly DateTimeOffset Start = new(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);

    protected abstract IRevisionStore Create();

    public virtual void Dispose()
    {
    }

    private static ManifestEntry Entry(int ordinal, string id, string? target = null, int first = 1, string? language = null) =>
        new(ordinal, new ChunkId(id), new ChunkId(target ?? id), language, first);

    private static CommitRevisionRequest Commit(
        int expected, params ManifestEntry[] entries) => new(Scope, Doc, expected, entries, Start);

    private static CommitRevisionRequest CommitDoc(string doc, int expected, params ManifestEntry[] entries) =>
        new(Scope, new DocumentId(doc), expected, entries, Start);

    private static async Task<int> CountOf(IRevisionStore store, string chunk, string scope = Scope) =>
        (await store.GetRefAsync(scope, new ChunkId(chunk)))?.Count ?? -1;

    [SkippableFact]
    public async Task A_document_that_was_never_committed_has_no_manifest()
    {
        Assert.Null(await Create().GetAsync(Scope, Doc));
    }

    [SkippableFact]
    public async Task The_first_commit_makes_revision_one_and_counts_each_chunk()
    {
        var store = Create();

        var result = await store.CommitAsync(Commit(0, Entry(0, "a"), Entry(1, "b")));

        Assert.Equal(CommitRevisionStatus.Committed, result.Status);
        Assert.Equal(1, result.Revision);
        Assert.Empty(result.Orphaned);
        Assert.Equal(1, await CountOf(store, "a"));
        Assert.Equal(1, await CountOf(store, "b"));
        var manifest = await store.GetAsync(Scope, Doc);
        Assert.NotNull(manifest);
        Assert.Equal(1, manifest!.Revision);
        Assert.Equal(new[] { "a", "b" }, manifest.Entries.Select(e => e.Id.Value));
        Assert.Equal(Start, manifest.UpdatedAt);
    }

    [SkippableFact]
    public async Task The_manifest_keeps_the_lineage_of_every_entry()
    {
        var store = Create();
        var entries = new[]
        {
            new ManifestEntry(0, new ChunkId("a"), new ChunkId("a"), "en", 1),
            new ManifestEntry(1, new ChunkId("b"), new ChunkId("a"), null, 2, PreviousOrdinal: 4, Replaces: new ChunkId("old")),
        };

        await store.CommitAsync(Commit(0, entries));
        var read = (await store.GetAsync(Scope, Doc))!.Entries;

        Assert.Equal(entries[0], read[0]);
        Assert.Equal(entries[1], read[1]);
    }

    [SkippableFact]
    public async Task A_commit_against_an_old_revision_is_a_conflict_and_changes_nothing()
    {
        var store = Create();
        await store.CommitAsync(Commit(0, Entry(0, "a")));

        var result = await store.CommitAsync(Commit(0, Entry(0, "z")));

        Assert.Equal(CommitRevisionStatus.Conflict, result.Status);
        Assert.Equal(1, result.Revision);
        Assert.Equal(-1, await CountOf(store, "z"));
        Assert.Equal("a", (await store.GetAsync(Scope, Doc))!.Entries.Single().Id.Value);
    }

    [SkippableFact]
    public async Task Two_workers_that_commit_the_same_revision_get_one_winner()
    {
        var one = Create();
        var two = Create();

        var results = await Task.WhenAll(
            Task.Run(async () => await one.CommitAsync(Commit(0, Entry(0, "a")))),
            Task.Run(async () => await two.CommitAsync(Commit(0, Entry(0, "b")))));

        Assert.Equal(1, results.Count(r => r.Status == CommitRevisionStatus.Committed));
        Assert.Equal(1, results.Count(r => r.Status == CommitRevisionStatus.Conflict));
    }

    [SkippableFact]
    public async Task Many_workers_racing_for_one_revision_leave_consistent_counts()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(async () => await Create().CommitAsync(Commit(0, Entry(0, "c" + i))))));

        Assert.Equal(1, results.Count(r => r.Status == CommitRevisionStatus.Committed));
        var refs = await Create().ListRefsAsync(Scope);
        Assert.Single(refs);
        Assert.Equal(1, refs[0].Count);
    }

    [SkippableFact]
    public async Task A_chunk_that_drops_out_of_the_only_document_that_uses_it_is_orphaned()
    {
        var store = Create();
        await store.CommitAsync(Commit(0, Entry(0, "a"), Entry(1, "b")));

        var result = await store.CommitAsync(Commit(1, Entry(0, "a")));

        Assert.Equal(new[] { "b" }, result.Orphaned.Select(c => c.Value));
        var b = await store.GetRefAsync(Scope, new ChunkId("b"));
        Assert.Equal(0, b!.Count);
        Assert.Equal(ChunkRefState.Orphaned, b.State);
        Assert.Equal(1, await CountOf(store, "a"));
    }

    [SkippableFact]
    public async Task A_chunk_used_by_two_documents_is_not_orphaned_until_both_let_go()
    {
        var store = Create();
        await store.CommitAsync(CommitDoc("one", 0, Entry(0, "shared")));
        await store.CommitAsync(CommitDoc("two", 0, Entry(0, "shared")));
        Assert.Equal(2, await CountOf(store, "shared"));

        var first = await store.CommitAsync(CommitDoc("one", 1));
        Assert.Empty(first.Orphaned);
        Assert.Equal(1, await CountOf(store, "shared"));

        var second = await store.CommitAsync(CommitDoc("two", 1));
        Assert.Equal(new[] { "shared" }, second.Orphaned.Select(c => c.Value));
    }

    [SkippableFact]
    public async Task A_chunk_that_repeats_inside_one_document_counts_once()
    {
        var store = Create();

        await store.CommitAsync(Commit(0, Entry(0, "a"), Entry(1, "a"), Entry(2, "b")));

        Assert.Equal(1, await CountOf(store, "a"));
        var result = await store.CommitAsync(Commit(1, Entry(0, "b")));
        Assert.Equal(new[] { "a" }, result.Orphaned.Select(c => c.Value));
    }

    [SkippableFact]
    public async Task A_chunk_that_only_moves_keeps_its_count()
    {
        var store = Create();
        await store.CommitAsync(Commit(0, Entry(0, "a"), Entry(1, "b")));

        var result = await store.CommitAsync(Commit(1, Entry(0, "b"), Entry(1, "a")));

        Assert.Empty(result.Orphaned);
        Assert.Equal(1, await CountOf(store, "a"));
        Assert.Equal(1, await CountOf(store, "b"));
    }

    [SkippableFact]
    public async Task Deleting_a_document_leaves_a_manifest_without_entries_and_orphans_its_chunks()
    {
        var store = Create();
        await store.CommitAsync(Commit(0, Entry(0, "a")));

        var result = await store.CommitAsync(Commit(1));

        Assert.Equal(CommitRevisionStatus.Committed, result.Status);
        Assert.Equal(2, result.Revision);
        Assert.Equal(new[] { "a" }, result.Orphaned.Select(c => c.Value));
        var manifest = await store.GetAsync(Scope, Doc);
        Assert.True(manifest!.IsDeleted);
        Assert.Equal(2, manifest.Revision);
    }

    [SkippableFact]
    public async Task A_deleted_document_can_be_written_again()
    {
        var store = Create();
        await store.CommitAsync(Commit(0, Entry(0, "a")));
        await store.CommitAsync(Commit(1));

        var result = await store.CommitAsync(Commit(2, Entry(0, "a", first: 3)));

        Assert.Equal(CommitRevisionStatus.Committed, result.Status);
        var a = await store.GetRefAsync(Scope, new ChunkId("a"));
        Assert.Equal(1, a!.Count);
        Assert.Equal(ChunkRefState.Active, a.State);
        Assert.Empty(await store.ListPendingAsync(Scope, Start, 10));
    }

    [SkippableFact]
    public async Task Scopes_do_not_share_documents_or_counts()
    {
        var store = Create();
        await store.CommitAsync(new CommitRevisionRequest("tenant-a", Doc, 0, new[] { Entry(0, "a") }, Start));
        await store.CommitAsync(new CommitRevisionRequest("tenant-b", Doc, 0, new[] { Entry(0, "a") }, Start));

        await store.CommitAsync(new CommitRevisionRequest("tenant-a", Doc, 1, Array.Empty<ManifestEntry>(), Start));

        Assert.Equal(0, await CountOf(store, "a", "tenant-a"));
        Assert.Equal(1, await CountOf(store, "a", "tenant-b"));
        Assert.Equal(1, (await store.GetAsync("tenant-b", Doc))!.Revision);
    }

    // ---- retiring ----

    [SkippableFact]
    public async Task Pending_lists_orphaned_chunks_only()
    {
        var store = Create();
        await store.CommitAsync(Commit(0, Entry(0, "a"), Entry(1, "b")));
        await store.CommitAsync(Commit(1, Entry(0, "a")));

        var pending = await store.ListPendingAsync(Scope, Start, 10);

        Assert.Equal(new[] { "b" }, pending.Select(c => c.Value));
    }

    [SkippableFact]
    public async Task Pending_respects_the_take_limit()
    {
        var store = Create();
        await store.CommitAsync(Commit(0, Entry(0, "a"), Entry(1, "b"), Entry(2, "c")));
        await store.CommitAsync(Commit(1));

        Assert.Equal(2, (await store.ListPendingAsync(Scope, Start, 2)).Count);
    }

    [SkippableFact]
    public async Task A_sweeper_can_take_an_orphan_and_finish_it()
    {
        var store = Create();
        await store.CommitAsync(Commit(0, Entry(0, "a")));
        await store.CommitAsync(Commit(1));

        var claim = await store.BeginRetireAsync(Scope, new ChunkId("a"), "s1", TimeSpan.FromMinutes(2), Start);

        Assert.NotNull(claim);
        Assert.Equal(ChunkRefState.Retiring, (await store.GetRefAsync(Scope, new ChunkId("a")))!.State);
        Assert.True(await store.FinishRetireAsync(Scope, new ChunkId("a"), "s1"));
        Assert.Null(await store.GetRefAsync(Scope, new ChunkId("a")));
        Assert.Empty(await store.ListRefsAsync(Scope));
    }

    [SkippableFact]
    public async Task A_chunk_that_a_document_still_uses_can_not_be_taken()
    {
        var store = Create();
        await store.CommitAsync(Commit(0, Entry(0, "a")));

        Assert.Null(await store.BeginRetireAsync(Scope, new ChunkId("a"), "s1", TimeSpan.FromMinutes(2), Start));
        Assert.Null(await store.BeginRetireAsync(Scope, new ChunkId("nothing"), "s1", TimeSpan.FromMinutes(2), Start));
    }

    [SkippableFact]
    public async Task Only_one_sweeper_gets_a_chunk()
    {
        var store = Create();
        await store.CommitAsync(Commit(0, Entry(0, "a")));
        await store.CommitAsync(Commit(1));

        var first = await store.BeginRetireAsync(Scope, new ChunkId("a"), "s1", TimeSpan.FromMinutes(2), Start);
        var second = await store.BeginRetireAsync(Scope, new ChunkId("a"), "s2", TimeSpan.FromMinutes(2), Start.AddSeconds(30));

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.False(await store.FinishRetireAsync(Scope, new ChunkId("a"), "s2"));
        Assert.Empty(await store.ListPendingAsync(Scope, Start.AddSeconds(30), 10));
    }

    [SkippableFact]
    public async Task Parallel_sweepers_get_exactly_one_winner()
    {
        var store = Create();
        await store.CommitAsync(Commit(0, Entry(0, "a")));
        await store.CommitAsync(Commit(1));

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(async () => await Create().BeginRetireAsync(Scope, new ChunkId("a"), "s" + i, TimeSpan.FromMinutes(2), Start))));

        Assert.Equal(1, claims.Count(c => c is not null));
    }

    [SkippableFact]
    public async Task The_same_sweeper_asking_again_keeps_the_chunk()
    {
        var store = Create();
        await store.CommitAsync(Commit(0, Entry(0, "a")));
        await store.CommitAsync(Commit(1));
        await store.BeginRetireAsync(Scope, new ChunkId("a"), "s1", TimeSpan.FromMinutes(2), Start);

        Assert.NotNull(await store.BeginRetireAsync(Scope, new ChunkId("a"), "s1", TimeSpan.FromMinutes(2), Start.AddSeconds(10)));
    }

    [SkippableFact]
    public async Task A_chunk_whose_sweeper_died_is_pending_again_when_the_lease_ends_and_another_sweeper_can_take_it()
    {
        var store = Create();
        await store.CommitAsync(Commit(0, Entry(0, "a")));
        await store.CommitAsync(Commit(1));
        await store.BeginRetireAsync(Scope, new ChunkId("a"), "s1", TimeSpan.FromMinutes(2), Start);

        Assert.Empty(await store.ListPendingAsync(Scope, Start.AddMinutes(1), 10));
        var later = Start.AddMinutes(3);
        Assert.Equal(new[] { "a" }, (await store.ListPendingAsync(Scope, later, 10)).Select(c => c.Value));

        var taken = await store.BeginRetireAsync(Scope, new ChunkId("a"), "s2", TimeSpan.FromMinutes(2), later);
        Assert.NotNull(taken);
        Assert.False(await store.FinishRetireAsync(Scope, new ChunkId("a"), "s1"));
        Assert.True(await store.FinishRetireAsync(Scope, new ChunkId("a"), "s2"));
    }

    [SkippableFact]
    public async Task A_commit_that_wants_a_chunk_being_retired_is_blocked_and_changes_nothing()
    {
        var store = Create();
        await store.CommitAsync(CommitDoc("one", 0, Entry(0, "a")));
        await store.CommitAsync(CommitDoc("one", 1));
        await store.BeginRetireAsync(Scope, new ChunkId("a"), "s1", TimeSpan.FromMinutes(2), Start);

        var result = await store.CommitAsync(CommitDoc("two", 0, Entry(0, "keep"), Entry(1, "a")));

        Assert.Equal(CommitRevisionStatus.Blocked, result.Status);
        Assert.Equal(new[] { "a" }, result.Blocking.Select(c => c.Value));
        Assert.Null(await store.GetAsync(Scope, new DocumentId("two")));
        Assert.Null(await store.GetRefAsync(Scope, new ChunkId("keep")));
        Assert.Equal(ChunkRefState.Retiring, (await store.GetRefAsync(Scope, new ChunkId("a")))!.State);
    }

    [SkippableFact]
    public async Task Once_the_chunk_is_gone_a_commit_can_use_it_again_from_scratch()
    {
        var store = Create();
        await store.CommitAsync(CommitDoc("one", 0, Entry(0, "a")));
        await store.CommitAsync(CommitDoc("one", 1));
        await store.BeginRetireAsync(Scope, new ChunkId("a"), "s1", TimeSpan.FromMinutes(2), Start);
        await store.FinishRetireAsync(Scope, new ChunkId("a"), "s1");

        var result = await store.CommitAsync(CommitDoc("two", 0, Entry(0, "a")));

        Assert.Equal(CommitRevisionStatus.Committed, result.Status);
        Assert.Equal(1, await CountOf(store, "a"));
    }

    [SkippableFact]
    public async Task An_orphan_that_a_document_uses_again_before_a_sweep_is_active_and_not_pending()
    {
        var store = Create();
        await store.CommitAsync(CommitDoc("one", 0, Entry(0, "a")));
        await store.CommitAsync(CommitDoc("one", 1));

        await store.CommitAsync(CommitDoc("two", 0, Entry(0, "a")));

        Assert.Empty(await store.ListPendingAsync(Scope, Start, 10));
        Assert.Null(await store.BeginRetireAsync(Scope, new ChunkId("a"), "s1", TimeSpan.FromMinutes(2), Start));
        Assert.Equal(ChunkRefState.Active, (await store.GetRefAsync(Scope, new ChunkId("a")))!.State);
    }

    [SkippableFact]
    public async Task Aliases_come_back_with_the_chunk_they_point_to_and_go_with_it()
    {
        var store = Create();
        await store.CommitAsync(Commit(0, Entry(0, "owner"), Entry(1, "dup1", "owner"), Entry(2, "dup2", "owner")));
        Assert.Equal(1, await CountOf(store, "owner"));
        Assert.Equal(-1, await CountOf(store, "dup1"));
        await store.CommitAsync(Commit(1));

        var claim = await store.BeginRetireAsync(Scope, new ChunkId("owner"), "s1", TimeSpan.FromMinutes(2), Start);

        Assert.Equal(new[] { "dup1", "dup2" }, claim!.Aliases.Select(a => a.Value).OrderBy(a => a, StringComparer.Ordinal));
        await store.FinishRetireAsync(Scope, new ChunkId("owner"), "s1");

        // the alias list is gone with the chunk: a new owner with the same id starts clean
        await store.CommitAsync(Commit(2, Entry(0, "owner", first: 3)));
        await store.CommitAsync(Commit(3));
        var again = await store.BeginRetireAsync(Scope, new ChunkId("owner"), "s2", TimeSpan.FromMinutes(2), Start);
        Assert.Empty(again!.Aliases);
    }

    [SkippableFact]
    public async Task List_refs_shows_every_state()
    {
        var store = Create();
        await store.CommitAsync(CommitDoc("one", 0, Entry(0, "active"), Entry(1, "orphan"), Entry(2, "retiring")));
        await store.CommitAsync(CommitDoc("one", 1, Entry(0, "active")));
        await store.BeginRetireAsync(Scope, new ChunkId("retiring"), "s1", TimeSpan.FromMinutes(2), Start);

        var refs = (await store.ListRefsAsync(Scope)).ToDictionary(r => r.Chunk.Value);

        Assert.Equal(ChunkRefState.Active, refs["active"].State);
        Assert.Equal(ChunkRefState.Orphaned, refs["orphan"].State);
        Assert.Equal(ChunkRefState.Retiring, refs["retiring"].State);
        Assert.Equal(3, refs.Count);
        Assert.Empty(await store.ListRefsAsync("other-scope"));
    }

    [SkippableFact]
    public async Task Scopes_with_odd_characters_work()
    {
        var store = Create();
        var scope = "te{na}nt%1";
        await store.CommitAsync(new CommitRevisionRequest(scope, Doc, 0, new[] { Entry(0, "a") }, Start));

        Assert.Equal(1, (await store.GetAsync(scope, Doc))!.Revision);
        Assert.Equal(1, (await store.GetRefAsync(scope, new ChunkId("a")))!.Count);
    }
}
