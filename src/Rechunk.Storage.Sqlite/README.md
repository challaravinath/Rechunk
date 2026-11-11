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

The same file can also keep wrapped scope keys. Pass the store to `LocalWrappedKeyProvider` with a 32 byte wrapping key that you keep outside the file. The database stores ciphertext only. Opening it again with that wrapping key returns the same scope key. A wrong wrapping key fails and does not replace the row. A copy or backup of the file still has the wrapped keys.

```csharp
var keys = new LocalWrappedKeyProvider(wrappingKey, new SqliteWrappedKeyStore("Data Source=rechunk.db"));
var scopeKey = await keys.CreateAsync("tenant-a");
```

Depends on the Rechunk package.

https://github.com/challaravinath/Rechunk
