# Rechunk

Rechunk is a .NET library for RAG ingestion. It sits between your chunker and your embedding step and works out which chunks are new, which ones are repeats, and which old vectors should go when a document changes.

## Why

Every time a document is ingested again, most pipelines embed all of it again and often leave the old vectors behind. You pay for embeddings you already have, the same footer or disclaimer shows up in search results ten times, and an old version of a paragraph can still get quoted after it was changed. I want a small library that fixes this at chunk level and can also tell you later where a chunk came from and which embedding model made it.

## Status

Early, work in progress. Nothing is published to NuGet yet and the API will change.

What works today:

- cleaning up text and the exact hash stage (SHA-256), with duplicate detection per scope
- a small pipeline builder, DI setup, dry run mode
- the interfaces for the rest

What is still stubs (they throw NotImplementedException with a TODO in the code):

- SimHash and MinHash with LSH
- revision tracking and clean deletes
- the SQLite storage, the vector store writer, the Microsoft.Extensions.AI wrapper and the Semantic Kernel helper (these are just project shells for now)

## Planned features

- three stage dedup: exact hash, then a SimHash filter, then MinHash with LSH to confirm, with thresholds you can set
- revision aware updates: only re-embed chunks that changed and delete stale vectors without leaving orphans
- audit trail per chunk: version, source, time and embedding model
- deletion by document or source (right to erasure) and logs that never contain chunk text
- MMR style diversity when retrieving
- a benchmark on a public document set, embedding calls saved against answer quality
- Microsoft.Extensions.AI, Microsoft.Extensions.VectorData and Semantic Kernel integration

## Packages

| Package | What it is |
| --- | --- |
| Rechunk | the core library (project folder is src/Rechunk.Core) |
| Rechunk.Storage.Sqlite | SQLite storage |
| Rechunk.Extensions.VectorData | writer for Microsoft.Extensions.VectorData collections |
| Rechunk.Extensions.AI | wrapper for embedding generators |
| Rechunk.SemanticKernel | Semantic Kernel helpers |

## Build

You need the .NET 8 SDK (see global.json).

```
dotnet restore
dotnet build -c Release
dotnet test -c Release
```

To try the sample: `dotnet run --project samples/Sample.Console`

## License

MIT, see LICENSE.
