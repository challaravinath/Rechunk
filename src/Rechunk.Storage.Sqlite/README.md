# Rechunk.Storage.Sqlite

SQLite backend for the shared fingerprint store, revisions, the audit log and dead letters. The file is created on first use. Workers on one machine can share it.

```csharp
using Rechunk.Storage.Sqlite;

services.AddRechunk(options =>
    {
        options.SimHash.Enabled = true;
        options.MinHash.Enabled = true;
    })
    .AddSqliteStorage("Data Source=rechunk.db");
```

Depends on the Rechunk package.

https://github.com/challaravinath/Rechunk
