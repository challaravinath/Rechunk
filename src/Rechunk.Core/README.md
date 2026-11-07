# Rechunk

Rechunk sits between your chunker and your embedding step. It works out which chunks are new, which ones are repeats, and which old vectors should go when a document changes.

```csharp
var pipeline = RechunkPipeline.CreateBuilder()
    .UseSimHash()
    .UseMinHash()
    .Build();

var report = await pipeline.DeduplicateAsync("handbook", chunks);
```

SimHash and MinHash are off until you turn them on. The same pipeline also tracks document revisions, writes an audit log, and can scan for personal data before anything is embedded.

Storage and vector stores are separate packages: Rechunk.Storage.Sqlite, Rechunk.Storage.Redis, Rechunk.VectorData and Rechunk.SemanticKernel.

https://github.com/challaravinath/Rechunk
