namespace Rechunk.Core.Tests.Store;

/// <summary>What every dead letter store has to do. The in memory store, SQLite and Redis each get a subclass.</summary>
public abstract class DeadLetterStoreContract : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

    protected abstract IDeadLetterStore Create();

    public virtual void Dispose()
    {
    }

    private static DeadLetter Letter(
        string id,
        string scope = "tenant-a",
        DeadLetterStage stage = DeadLetterStage.Embed,
        int minutes = 0,
        string? document = "doc-1",
        string? text = null) =>
        new(id, scope, new ChunkId("chunk-" + id), document is null ? null : new DocumentId(document), 3, stage,
            "TimeoutException: the model did not answer", 3, Start.AddMinutes(minutes), text);

    private static readonly DeadLetterQuery All = new();

    [SkippableFact]
    public async Task A_saved_letter_comes_back_with_everything_on_it()
    {
        var store = Create();
        var letter = Letter("a", minutes: 5);
        await store.AddAsync(letter);

        var read = await store.GetAsync("a");

        Assert.NotNull(read);
        Assert.Equal(letter.Id, read!.Id);
        Assert.Equal(letter.Scope, read.Scope);
        Assert.Equal(letter.Chunk, read.Chunk);
        Assert.Equal(letter.Document, read.Document);
        Assert.Equal(letter.Ordinal, read.Ordinal);
        Assert.Equal(letter.Stage, read.Stage);
        Assert.Equal(letter.Reason, read.Reason);
        Assert.Equal(letter.Attempts, read.Attempts);
        Assert.Equal(letter.FailedAt, read.FailedAt);
        Assert.Null(read.Text);
    }

    [SkippableFact]
    public async Task A_missing_letter_is_null()
    {
        Assert.Null(await Create().GetAsync("nope"));
    }

    [SkippableFact]
    public async Task A_letter_without_a_document_is_kept_without_one()
    {
        var store = Create();
        await store.AddAsync(Letter("a", document: null));

        Assert.Null((await store.GetAsync("a"))!.Document);
    }

    [SkippableFact]
    public async Task Saving_the_same_id_again_replaces_the_letter()
    {
        var store = Create();
        await store.AddAsync(Letter("a", stage: DeadLetterStage.Embed));
        await store.AddAsync(Letter("a", stage: DeadLetterStage.Commit, minutes: 9) with { Attempts = 6 });

        var read = await store.GetAsync("a");
        Assert.Equal(DeadLetterStage.Commit, read!.Stage);
        Assert.Equal(6, read.Attempts);
        Assert.Equal(1, await store.CountAsync(All));
        Assert.Single(await store.ListAsync(All));
    }

    [SkippableFact]
    public async Task Letters_are_listed_oldest_first()
    {
        var store = Create();
        await store.AddAsync(Letter("late", minutes: 30));
        await store.AddAsync(Letter("early", minutes: 1));
        await store.AddAsync(Letter("middle", minutes: 10));

        var ids = (await store.ListAsync(All)).Select(l => l.Id).ToArray();

        Assert.Equal(new[] { "early", "middle", "late" }, ids);
    }

    [SkippableFact]
    public async Task The_list_can_be_filtered_by_scope_stage_chunk_and_document()
    {
        var store = Create();
        await store.AddAsync(Letter("a", scope: "tenant-a", stage: DeadLetterStage.Embed, minutes: 1, document: "d1"));
        await store.AddAsync(Letter("b", scope: "tenant-a", stage: DeadLetterStage.VectorWrite, minutes: 2, document: "d2"));
        await store.AddAsync(Letter("c", scope: "tenant-b", stage: DeadLetterStage.Embed, minutes: 3, document: "d1"));

        Assert.Equal(new[] { "a", "b" }, Ids(await store.ListAsync(new DeadLetterQuery(Scope: "tenant-a"))));
        Assert.Equal(new[] { "a", "c" }, Ids(await store.ListAsync(new DeadLetterQuery(Stage: DeadLetterStage.Embed))));
        Assert.Equal(new[] { "b" }, Ids(await store.ListAsync(new DeadLetterQuery(Chunk: new ChunkId("chunk-b")))));
        Assert.Equal(new[] { "a", "c" }, Ids(await store.ListAsync(new DeadLetterQuery(Document: new DocumentId("d1")))));
        Assert.Equal(new[] { "a" }, Ids(await store.ListAsync(new DeadLetterQuery("tenant-a", DeadLetterStage.Embed, null, new DocumentId("d1")))));
        Assert.Empty(await store.ListAsync(new DeadLetterQuery(Scope: "tenant-z")));
    }

    [SkippableFact]
    public async Task The_list_can_be_paged_and_the_count_ignores_the_page()
    {
        var store = Create();
        for (var i = 0; i < 7; i++)
        {
            await store.AddAsync(Letter("l" + i, minutes: i));
        }

        var page = await store.ListAsync(new DeadLetterQuery(Skip: 2, Take: 3));

        Assert.Equal(new[] { "l2", "l3", "l4" }, Ids(page));
        Assert.Equal(7, await store.CountAsync(new DeadLetterQuery(Skip: 2, Take: 3)));
        Assert.Equal(7, await store.CountAsync(All));
        Assert.Equal(0, await store.CountAsync(new DeadLetterQuery(Scope: "other")));
    }

    [SkippableFact]
    public async Task Removing_a_letter_says_whether_it_was_there()
    {
        var store = Create();
        await store.AddAsync(Letter("a"));

        Assert.True(await store.RemoveAsync("a"));
        Assert.False(await store.RemoveAsync("a"));
        Assert.Null(await store.GetAsync("a"));
        Assert.Empty(await store.ListAsync(All));
        Assert.Equal(0, await store.CountAsync(All));
    }

    [SkippableFact]
    public async Task Text_is_only_there_when_it_was_saved()
    {
        var store = Create();
        await store.AddAsync(Letter("plain"));
        await store.AddAsync(Letter("withtext", text: "Grüße, 你好, \"quotes\" and a new\nline"));

        Assert.Null((await store.GetAsync("plain"))!.Text);
        Assert.Equal("Grüße, 你好, \"quotes\" and a new\nline", (await store.GetAsync("withtext"))!.Text);
    }

    [SkippableFact]
    public async Task Many_parallel_writers_do_not_lose_letters()
    {
        var store = Create();

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(async () => await store.AddAsync(Letter("p" + i, minutes: i)))));

        Assert.Equal(40, await store.CountAsync(All));
        Assert.Equal(40, (await store.ListAsync(new DeadLetterQuery(Take: 1000))).Count);
    }

    private static string[] Ids(IReadOnlyList<DeadLetter> letters) => letters.Select(l => l.Id).ToArray();
}
