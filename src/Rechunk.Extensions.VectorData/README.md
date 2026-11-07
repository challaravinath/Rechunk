# Rechunk.VectorData

Writes embedded chunks to any Microsoft.Extensions.VectorData collection. Upserts and deletes run in batches. The key includes the scope, so a delete in one scope cannot remove another scope's vector.

```csharp
using Rechunk.Extensions.VectorData;

var mapping = new VectorDataMapping<string, Doc>
{
    ToKey = VectorDataMapping<string, Doc>.ScopedKey,
    ToRecord = chunk => new Doc(
        VectorDataMapping<string, Doc>.ScopedKey(chunk.Scope ?? "default", chunk.Id),
        chunk.Vector),
};

IVectorSink sink = new VectorDataSink<string, Doc>(collection, mapping);
```

The types are in the namespace Rechunk.Extensions.VectorData. The package id is Rechunk.VectorData. Depends on the Rechunk package.

https://github.com/challaravinath/Rechunk
