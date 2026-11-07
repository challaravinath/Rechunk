# Rechunk

Rechunk is a .NET library that sits between your chunker and your embedder and decides which chunks are new, which are repeats, and which old vectors to drop.

[![NuGet](https://img.shields.io/nuget/v/Rechunk.svg)](https://www.nuget.org/packages/Rechunk) [![CI](https://github.com/challaravinath/Rechunk/actions/workflows/ci.yml/badge.svg)](https://github.com/challaravinath/Rechunk/actions/workflows/ci.yml) [![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

## Why Rechunk

Chunkers split text. Vector stores store vectors. Neither checks a chunk before you pay to embed it, so most pipelines embed every chunk on every ingest, keep near copies as separate vectors, have no safe shared state for stateless workers, and leave PII, audit and erasure to the team.

- A later ingest embeds only chunks whose text changed. A paragraph that moved keeps its vector. Chunks that no document still uses are deleted.
- SHA-256 drops exact repeats. SimHash and MinHash, off until you enable them, drop boilerplate and small edits before the embedder.
- SQLite or Redis holds the fingerprints for every worker. A chunk is claimed, then committed only after the vector write. A lease covers a dead worker. A chunk that keeps failing is a dead letter and can be replayed.
- PII is scanned before fingerprinting and can be flagged, masked or blocked. The audit log records the decision and never the text or the secret. Erasure removes the vectors and the fingerprints and leaves a receipt of counts.

How a chunk moves through that is in [docs/how-it-works.md](docs/how-it-works.md).

## Install

```
dotnet add package Rechunk
dotnet add package Rechunk.Storage.Sqlite
dotnet add package Rechunk.Storage.Redis
dotnet add package Rechunk.VectorData
dotnet add package Rechunk.SemanticKernel
```

Version 0.1.0 is the first set of packages. They are not on NuGet until a release tag is published, and the API can still change. Rechunk.Extensions.AI is not published in 0.1.0.

## Quick start

```csharp
using Rechunk;
using Rechunk.Storage.Sqlite;

var store = new BackedFingerprintStore(new SqliteFingerprintBackend("Data Source=rechunk.db"));
var pipeline = RechunkPipeline.CreateBuilder()
    .UseSimHash()
    .UseFingerprintStore(store)
    .Build();

var chunks = new[] { new ChunkInput(0, "Paid leave is 30 days.") };
var report = await pipeline.DeduplicateAsync("handbook", chunks);
Console.WriteLine(report.UniqueCount);
```

## Packages

| Package | When to use it |
| --- | --- |
| Rechunk | The pipeline, the in memory store, revisions, PII and audit. |
| Rechunk.Storage.Sqlite | One machine, and the store should survive a restart. |
| Rechunk.Storage.Redis | Many workers share the fingerprints, dead letters, revisions and audit log. |
| Rechunk.VectorData | Batched upserts and scoped deletes for a VectorData collection. |
| Rechunk.SemanticKernel | Register Rechunk on a Semantic Kernel builder. |

## Benchmarks

On 2500 of 13555 files from [dotnet/docs](https://github.com/dotnet/docs) at commit `82937f4`, the defaults saw 54085 chunks and skipped 11675 embeds (21.6%) in 1136 ms, 47610 chunks per second. 11610 of those skips were exact repeats. A hand check of 20 near pairs called 16 true duplicates. That is 20 pairs, not every near pair. Notes are in [docs/benchmarks/dotnet-docs.md](docs/benchmarks/dotnet-docs.md).

On 8000 labeled PAWS wiki pairs, the same defaults merged 41. Precision 82.9% is 34 of those 41, the pairs PAWS calls paraphrases. Recall 1.0% is 34 of 3536 paraphrases. It is low because those paraphrases are rewordings, not near copies. 4 of the 7 wrong merges are identical strings that PAWS labels as different. See [docs/benchmarks/paws.md](docs/benchmarks/paws.md).

None of these runs scores answer quality. The docs figure is one corpus, and both sets are English. Made up text is in [docs/benchmarks/synthetic.md](docs/benchmarks/synthetic.md).

## Docs

[How it works](docs/how-it-works.md), [stages](docs/stages.md), [shared store](docs/shared-store.md), [revisions](docs/revisions.md), [languages](docs/languages.md), [audit, PII and erasure](docs/audit-pii-erasure.md).

## Roadmap

0.2 is planned and not built yet. The items are a SQL Server and Azure SQL store, lease renewal while an embed runs, safe handling when two workers see near duplicates at the same time, and NuGet trusted publishing.

The list is in [ROADMAP.md](ROADMAP.md).

## Build

You need the .NET 8 SDK (see global.json).

```
dotnet restore
dotnet build -c Release
dotnet test -c Release
```

`dotnet run --project samples/Sample.Console` runs the sample. To time the SimHash and MinHash code: `dotnet run -c Release --project benchmarks/Rechunk.Benchmarks -- --filter "*SimHash*"` (or `"*MinHash*"`). Add `--job dry` for a quick check. Redis tests run when `RECHUNK_REDIS` holds a connection string, for example `RECHUNK_REDIS=localhost:6379 dotnet test -c Release`. Without it they are skipped. They use their own key prefix per test and remove their keys afterwards. The Azure Functions sample only needs to build, it does not need Azure: `dotnet build samples/Sample.AzureFunction`.

## License

MIT, see LICENSE.
