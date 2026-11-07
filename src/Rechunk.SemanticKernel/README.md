# Rechunk.SemanticKernel

Registers Rechunk on a Semantic Kernel builder. Vector stores are not wrapped here. Use Rechunk.VectorData for a Microsoft.Extensions.VectorData collection.

```csharp
using Microsoft.SemanticKernel;
using Rechunk.SemanticKernel;

var builder = Kernel.CreateBuilder();
builder.AddRechunk(options =>
{
    options.SimHash.Enabled = true;
    options.MinHash.Enabled = true;
});
```

Depends on the Rechunk package and Microsoft.SemanticKernel.Abstractions.

https://github.com/challaravinath/Rechunk
