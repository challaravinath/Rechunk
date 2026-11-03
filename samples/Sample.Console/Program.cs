using Rechunk;

// A small demo of all three stages. Each chunk below is labelled with what we expect to happen to it.
var pipeline = RechunkPipeline.CreateBuilder()
    .UseSimHash()
    .UseMinHash()
    .Build();

var leave = string.Join(' ',
    "Employees get 30 days of paid leave per year, counted from the first day of work.",
    "Leave that is not used by the end of the year can be carried over for up to three months.",
    "Requests for more than five days in a row need approval from the team lead at least two weeks ahead.",
    "Short requests of one or two days can be agreed with the team lead on the same day.",
    "Public holidays are not counted as leave, and neither are days of approved training.",
    "Sick leave is separate and does not reduce the paid leave balance.",
    "A doctor's note is needed for sick leave of more than three days.",
    "New joiners earn leave in proportion to the months they have worked in their first year.",
    "Unpaid leave can be requested after the paid balance is used, subject to approval from the department head.",
    "Parental leave follows its own policy, which is kept in the benefits handbook.",
    "Leave records are kept by the people team and can be checked by the employee at any time.",
    "If a request is declined, the team lead has to give a reason in writing within two working days.",
    "Employees who leave the company are paid for unused leave up to a limit of ten days.",
    "Managers should plan team cover before approving overlapping requests from the same team.",
    "Questions about this policy can be sent to the people team through the usual helpdesk.");

string Swap(string text, params (string From, string To)[] changes) =>
    changes.Aggregate(text, (current, c) => current.Replace(c.From, c.To));

var chunks = new (string Label, string Text)[]
{
    ("original leave policy", leave),
    ("same text, extra spaces (exact)", leave.Replace(". ", ".   ") + "\n"),
    ("one word changed (SimHash)", Swap(leave, ("usual", "normal"))),
    ("a few words changed (MinHash)", Swap(leave, ("usual", "normal"), ("approval", "sign off"), ("ahead", "beforehand"))),
    ("30 days became 60 (guard keeps it)", Swap(leave, ("30 days", "60 days"))),
    ("an unrelated chunk", "Expenses must be filed within two weeks of the purchase date, with the receipt attached to the claim form."),
};

var report = await pipeline.DeduplicateAsync("handbook", chunks.Select((c, i) => new ChunkInput(i, c.Text)).ToList());

Console.WriteLine("First run:");
for (var i = 0; i < chunks.Length; i++)
{
    var d = report.Decisions[i];
    var detail = d.Outcome == DedupOutcome.Unique
        ? "new, goes to embedding"
        : $"skipped, {d.Stage} match with {d.DuplicateOf!.Value.Value[..8]}" +
          (d.HammingDistance is { } bits ? $", {bits} bits apart" : string.Empty) +
          (d.EstimatedJaccard is { } j ? $", Jaccard about {j:F2}" : string.Empty);
    Console.WriteLine($"  {chunks[i].Label,-38} {detail}");
}

Console.WriteLine();
Console.WriteLine("Second run, same chunks (everything is already known):");
var again = await pipeline.DeduplicateAsync("handbook", chunks.Select((c, i) => new ChunkInput(i, c.Text)).ToList());
Console.WriteLine($"  {again.UniqueCount} to embed, {again.DuplicateCount} skipped");

var skipped = report.Decisions.Where(d => d.Outcome == DedupOutcome.Duplicate).ToList();
Console.WriteLine();
Console.WriteLine("Summary of the first run:");
Console.WriteLine($"  {chunks.Length} chunks in, {report.UniqueCount} sent to embedding, {skipped.Count} skipped");
foreach (var stage in new[] { MatchStage.Exact, MatchStage.SimHash, MatchStage.MinHash })
{
    Console.WriteLine($"    {stage,-8} {skipped.Count(d => d.Stage == stage)}");
}
