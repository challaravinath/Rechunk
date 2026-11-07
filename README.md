# Rechunk

Rechunk is a .NET library for RAG ingestion. It sits between your chunker and your embedding step and works out which chunks are new, which ones are repeats, and which old vectors should go when a document changes.

## Why

Every time a document is ingested again, most pipelines embed all of it again and often leave the old vectors behind. You pay for embeddings you already have, the same footer or disclaimer shows up in search results ten times, and an old version of a paragraph can still get quoted after it was changed. I want a small library that fixes this at chunk level and can also tell you later where a chunk came from and which embedding model made it.

## Status

Early, work in progress. Nothing is published to NuGet yet and the API will change.

What works today:

- cleaning up text and the exact hash stage (SHA-256), with duplicate detection per scope
- the SimHash stage (off by default): a 64 bit fingerprint over word shingles and an in memory index that finds chunks within a few bits of each other, per scope and safe to use from several threads
- the MinHash stage (off by default): a signature of 128 numbers per chunk (word shingles, seeded stable hashing, same values on every platform) and an in memory LSH index that finds chunks whose estimated Jaccard similarity is at or above the threshold (0.85 by default), per scope and safe to use from several threads
- all three stages working together in one pipeline. A chunk that matches an earlier stage never reaches the later ones. When SimHash and MinHash are both on, MinHash confirms what SimHash finds (you can turn that off and trust SimHash), and it also catches looser near duplicates that SimHash misses
- a guard that keeps two chunks apart when their numbers or "not" style words differ, so "30 days" and "60 days" are not merged, in every stage
- every decision records the stage that matched, which chunk it is a duplicate of, the SimHash distance and the Jaccard estimate
- a small pipeline builder, DI setup, dry run mode
- a shared fingerprint store, so many stateless workers check against the same exact hash, SimHash and MinHash indexes. A chunk is claimed first, committed after its vector is written and released if the work fails, so a failed chunk is never left marked as seen. The in memory indexes are still the default, and there are SQLite and Redis backends (see [docs/shared-store.md](docs/shared-store.md))
- an ingestor that runs claim, embed, vector write and commit with retries, and writes a dead letter (no chunk text unless you ask for it) when a chunk keeps failing, with list and replay
- revision tracking and clean deletes: a new version of a document only embeds the chunks that changed, keeps moved paragraphs as they are, and removes the vectors and fingerprints of chunks that are gone, safely with many workers (see [docs/revisions.md](docs/revisions.md))
- a language tag per chunk, so chunks in different languages are never merged, and character shingles for Chinese, Japanese and Thai, so near duplicates are found in text without spaces (see [docs/languages.md](docs/languages.md))
- a persistent audit trail (in memory, SQLite and Redis, one contract test suite for all three): one event per chunk for new, skipped, aliased, flagged, revised, deleted, erased and dead lettered, with document, revision, stage, scope, language and time, never the chunk text. Queries by document, chunk, source and time range. A failed audit write is retried and then parked in an outbox, so it is not lost (see [docs/audit.md](docs/audit.md))
- PII and secret scanning before dedup and embedding: a built in detector for cards (Luhn), API keys, emails, phone numbers, Aadhaar (Verhoeff), PAN, SSN and IBAN, a policy per type (Flag, Mask or Block), masking before the chunk is fingerprinted, and an interface for Presidio or Azure AI Language (see [docs/pii.md](docs/pii.md))
- erasure by document, by source, by text, by chunk and by finding, which removes the vectors, the chunks and every fingerprint, and leaves a receipt that has counts and no content (see [docs/erasure.md](docs/erasure.md))
- a writer for any Microsoft.Extensions.VectorData collection, with batching, retries and the scoped delete that revisions use
- the interfaces for the rest

What is still stubs (they throw NotImplementedException with a TODO in the code):

- the Microsoft.Extensions.AI wrapper and the Semantic Kernel helper (these are just project shells for now)

## Using the stages

```csharp
var pipeline = RechunkPipeline.CreateBuilder()
    .UseSimHash()                                  // stage 2, options can be changed in a lambda
    .UseMinHash(o => o.Threshold = 0.85)           // stage 3
    .Build();

var report = await pipeline.DeduplicateAsync("handbook", chunks);
```

`UseSimHash` and `UseMinHash` switch a stage on and let you change its options. `UseSimHashIndex` and `UseMinHashIndex` are different: they hand the pipeline your own index (for example one backed by a database) instead of the in memory one. With dependency injection set `options.SimHash.Enabled` and `options.MinHash.Enabled` inside `AddRechunk`.

A few things worth knowing:

- SimHash is quick but rough. A one word edit in a long chunk can be 2 bits away, and in a short chunk it can be 5 or more. That is why MinHash confirms SimHash by default.
- MinHash compares the word triples of two chunks. Its estimate is the share of equal slots in the two signatures, so with 128 slots it is off by about 0.04 either way. It is a good fit for "is this about 90 percent the same", not for hairline calls around the threshold.
- LSH only looks at chunks that share a band. Bands and rows are worked out from the threshold when you do not set them (9 bands of 14 rows for 128 slots and 0.85). A pair at similarity s becomes a candidate with probability 1 - (1 - s^14)^9, and the curve is steepest near (1/9)^(1/14) = 0.855. Every candidate is then checked with the real estimate, so the index can miss a pair but does not add false ones.
- A chunk that is skipped as a near duplicate is not added to the near indexes. The chunk it matched stands for it. So a long chain of small edits (A, then B close to A, then C close to B but not to A) is not followed.
- Chunks with fewer than 8 words (`SimHash.MinWords`, `MinHash.MinWords`) are only matched exactly. Case is not folded, so "Notice" and "notice" differ for the exact stage.
- Two callers sending nearly the same chunk at the same moment can both be treated as new, because a chunk only becomes visible to the near duplicate stages once it is committed. Exact repeats are always settled correctly, also with many workers, when you use a shared store.

## Many workers and failures

With the default in memory indexes one process knows what it has seen and nothing else does. For queue triggered workers (Azure Functions, containers that scale out) use a shared store, so every instance checks against the same indexes:

```csharp
services.AddRechunk(o =>
    {
        o.SimHash.Enabled = true;
        o.MinHash.Enabled = true;
        o.Store.LeaseDuration = TimeSpan.FromMinutes(5);
        o.Failures.MaxAttempts = 3;
    })
    .AddRedisStorage("localhost:6379");     // or .AddSqliteStorage("Data Source=rechunk.db")

// in the worker, with the queue message id as the holder
var result = await ingestor.IngestAsync(new IngestRequest(tenant, documentId, chunks, Holder: message.Id));
```

`ChunkIngestor` claims the chunks, embeds them, writes the vectors and commits the claims. If a chunk fails more than `Failures.MaxAttempts` times it goes to the dead letter store with its id, scope, the stage that failed, a short reason, the attempt count and the time. `ReplayAsync` tries those again. The details, the guarantees and the limits are in [docs/shared-store.md](docs/shared-store.md), and `samples/Sample.AzureFunction` shows it with a queue trigger.

## Revisions and languages

When a document changes, send the whole new version through the revision tracker:

```csharp
var result = await tracker.ReviseAsync(
    new DocumentId("handbook"), "tenant-1", chunks, embedder, sink, holder: message.Id);

// result.Embedded: chunks that were new or edited
// result.Kept and result.Moved: chunks that were already there, no embedding call
// result.Deleted: vectors removed because no document uses them any more
```

Unchanged chunks keep their vectors, even when a paragraph moved. A chunk is deleted from the vector store and from all three dedup stages once no document uses it, and a chunk that two documents share stays until both let go. The steps are ordered so a crash leaves nothing that a later run can not finish, and `VerifyAsync` lists vectors that have no owner. `DeleteDocumentAsync` and `EraseAsync` remove a whole document. More in [docs/revisions.md](docs/revisions.md).

A sink you write has to implement `DeleteAsync(scope, ids)`. A chunk id comes from the text, so the same paragraph in two scopes has the same id, and the vector has to be stored under both. `VectorDataMapping.ScopedKey` is a string key that does this, and the VectorData sink refuses a key that leaves the scope out. Vectors already stored under the bare chunk id need a new key or a fresh ingest.

Chunks can carry a language, and chunks of different languages are never duplicates of each other:

```csharp
new ChunkInput(0, text, Language: "fr")                   // you say it
services.AddRechunk(o => o.Language.AutoDetect = true);   // or the built in detector says it
```

Text in scripts without spaces is cut into characters for SimHash and MinHash, so a one character edit in a Chinese, Japanese or Thai paragraph is still a near duplicate. Details, options and limits are in [docs/languages.md](docs/languages.md).

## Audit, PII and erasure

The audit log is on by default and holds no chunk text. PII scanning is off until you turn it on:

```csharp
services.AddRechunk(o =>
{
    o.Pii.Enabled = true;
    o.Pii.Set(PiiType.ApiKey, PiiPolicy.Block);   // never stored or embedded
    o.Pii.Set(PiiType.Email, PiiPolicy.Mask);     // [EMAIL] goes into the dedup and the embedding
});                                               // every other type is flagged

await tracker.ReviseAsync(doc, "tenant-1", chunks, embedder, sink, holder: id, source: "wiki");

// what happened to a document, and what a source contributed this month
var events = audit.QueryAsync(new AuditQuery(Document: doc));
var month = audit.QueryAsync(new AuditQuery(From: start, To: end) { Source = "wiki" });

// right to erasure: everything that came from a source, with a receipt
var result = await tracker.EraseAsync(new EraseRequest("tenant-1", null, "wiki", "ticket 4711", "support"), sink);
```

A PII finding is stored as a type and a span, never the value. Masking happens before the fingerprints are made, so two chunks that differ only in an email address become one. The built in detector is a set of rules with checksums and context words, and it will miss things, so use it as one layer. Details and limits are in [docs/audit.md](docs/audit.md), [docs/pii.md](docs/pii.md) and [docs/erasure.md](docs/erasure.md).

## Synthetic measurement

This is made up text, not real documents, so read it as a check that the stages do what they should and not as a result you can expect on your data. The corpus is 4000 chunks of 60 to 150 pseudo words, plus 1500 planted variants (5500 chunks in all), 300 of each kind: the same text with extra whitespace, one word changed, about 2 percent of the words changed, about 5 percent changed, and a changed number or an added "not". A planted variant should be merged when its exact Jaccard to the original is at least 0.85 and the guard does not apply, which is 900 of them. Fixed seed, so the numbers repeat.

| setting | embedding calls | saved | exact / SimHash / MinHash | precision | recall | wrong merges |
| --- | --- | --- | --- | --- | --- | --- |
| exact only | 5200 | 300 | 300 / 0 / 0 | 100.0% | 33.3% | 0 |
| exact + SimHash (3 bits) | 5098 | 402 | 300 / 102 / 0 | 99.8% | 44.6% | 1 |
| exact + SimHash (6 bits) | 4814 | 686 | 300 / 386 / 0 | 97.5% | 74.3% | 17 |
| exact + MinHash | 4666 | 834 | 300 / 0 / 534 | 99.5% | 92.2% | 4 |
| all three, SimHash trusted | 4660 | 840 | 300 / 102 / 438 | 99.4% | 92.8% | 5 |
| all three, MinHash confirms SimHash | 4663 | 837 | 300 / 99 / 438 | 99.5% | 92.6% | 4 |

What I take from it:

- SimHash with a strict distance catches few of the planted edits (80 of 300 single word edits, 21 of 300 two percent edits). Loosening it to 6 bits catches more but starts to merge things it should not.
- MinHash does most of the work: 295 of 300 single word edits and about 235 to 240 of 300 two percent edits. The misses are mostly pairs whose true similarity sits close to 0.85.
- The guard held: none of the 300 changed numbers or added "not" variants were merged in any setting.
- The wrong merges for the MinHash settings are loose variants (about 5 percent edited) whose true Jaccard is a little under 0.85 and whose estimate came out above it.
- Adding SimHash on top of MinHash saves very little here (3 more skipped chunks) and its confirmation step costs a bit of time. On this corpus MinHash alone is nearly as good. It may be different on long documents with many chunks, which this does not test.
- Timing for the whole run on the machine I used was well under a second per setting (roughly 15 to 80 microseconds per chunk including the pipeline). That is a rough figure from one run, not a benchmark.

To repeat it: `dotnet run -c Release --project benchmarks/Rechunk.Benchmarks -- --synthetic`

## Benchmarks

The synthetic numbers are made up text. I also ran the three stages on real markdown from the public [dotnet/docs](https://github.com/dotnet/docs) repo, on a sample of 2500 files (commit `82937f4`). With SimHash distance 3 and MinHash 0.85 the sample was 54085 chunks, and the pipeline skipped 11873 of them before embedding (22.0%): 11610 exact, 117 SimHash, 146 MinHash. That pass took 1310 ms, 41286 chunks per second. I read 10 near duplicate pairs and called 4 of them true duplicates. The thresholds stay at the defaults. The other settings, the pair notes, and the steps to repeat it are in [docs/benchmarks/dotnet-docs.md](docs/benchmarks/dotnet-docs.md).

## Planned features

- MMR style diversity when retrieving
- a benchmark on a public document set that checks embedding calls saved against answer quality
- Microsoft.Extensions.AI and Semantic Kernel integration

## Packages

| Package | What it is |
| --- | --- |
| Rechunk | the core library (project folder is src/Rechunk.Core) |
| Rechunk.Storage.Sqlite | SQLite storage for the shared fingerprint store, dead letters and revisions |
| Rechunk.Storage.Redis | Redis storage for the shared fingerprint store, dead letters and revisions |
| Rechunk.Extensions.VectorData | writer and delete path for Microsoft.Extensions.VectorData collections |
| Rechunk.Extensions.AI | wrapper for embedding generators |
| Rechunk.SemanticKernel | Semantic Kernel helpers |

## Build

You need the .NET 8 SDK (see global.json).

```
dotnet restore
dotnet build -c Release
dotnet test -c Release
```

To time the SimHash and MinHash code: `dotnet run -c Release --project benchmarks/Rechunk.Benchmarks -- --filter "*SimHash*"` (or `"*MinHash*"`). Add `--job dry` for a quick check.

To try the sample: `dotnet run --project samples/Sample.Console`

The Redis tests only run when `RECHUNK_REDIS` holds a connection string, for example `RECHUNK_REDIS=localhost:6379 dotnet test -c Release`. Without it they are reported as skipped. They use their own key prefix per test and remove their keys afterwards. The Azure Functions sample only needs to build, it does not need Azure: `dotnet build samples/Sample.AzureFunction`.

## License

MIT, see LICENSE.
