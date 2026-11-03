using Rechunk;

// Tiny demo of the exact hash stage. SimHash and MinHash are not written yet.
var pipeline = RechunkPipeline.CreateBuilder().UseExactHash().Build();

var handbook = new List<ChunkInput>
{
    new(0, "Employees get 30 days of leave per year."),
    new(1, "Confidential, Acme Corp"),
    new(2, "Expenses must be filed within two weeks."),
    new(3, "Confidential,   Acme Corp"),
};

Console.WriteLine("First run:");
Print(await pipeline.DeduplicateAsync("handbook", handbook));

Console.WriteLine();
Console.WriteLine("Second run, same chunks:");
Print(await pipeline.DeduplicateAsync("handbook", handbook));

static void Print(DedupReport report)
{
    foreach (var d in report.Decisions)
    {
        var note = d.DuplicateOf is { } of ? $" (same as {of.Value[..8]})" : string.Empty;
        Console.WriteLine($"  chunk {d.Input.Ordinal}: {d.Outcome}{note}");
    }

    Console.WriteLine($"  {report.UniqueCount} to embed, {report.DuplicateCount} skipped");
}
