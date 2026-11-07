# Rechunk.Storage.Redis

Redis backend for the shared fingerprint store, so many workers check the same exact hash, SimHash and MinHash indexes. Revisions, the audit log and dead letters go in the same place.

```csharp
using Rechunk.Storage.Redis;

services.AddRechunk(options =>
    {
        options.SimHash.Enabled = true;
        options.MinHash.Enabled = true;
    })
    .AddRedisStorage("localhost:6379");
```

Depends on the Rechunk package.

https://github.com/challaravinath/Rechunk
