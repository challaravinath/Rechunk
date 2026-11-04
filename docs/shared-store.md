# Shared store and dead letters

This page is about running Rechunk in more than one process, for example queue triggered Azure Functions or containers that scale out, and about what happens to a chunk that keeps failing.

## The problem

The default indexes live in the memory of one process. Two workers that get the same chunk at the same time both think it is new, both embed it and both write a vector. And if a worker marks a chunk as seen and then fails before its vector is written, the chunk is never embedded, because the next attempt is told it is a duplicate.

The shared store fixes both. It is one place for the exact hashes, the SimHash band index and the MinHash LSH index, and a chunk goes through a short lifecycle in it.

## The lifecycle

1. **Claim.** The pipeline asks the store for the chunk's hash. The store records it as pending, with a lease, and says who owns it. This is atomic, so if 64 workers claim the same hash at once exactly one of them wins.
2. **Write.** The winner embeds the chunk and writes the vector.
3. **Commit.** After the vector write worked, the winner commits the claim. Now the hash is final and the chunk is in the near duplicate indexes.
4. **Release.** If the work failed, the winner releases the claim. The pending row goes away and the next attempt can take it.

What a caller sees when it claims a hash:

| Status | Meaning |
| --- | --- |
| Claimed | The hash was new. It is yours until the lease ends. |
| Renewed | You already held it (same holder), the lease was extended. This is what makes a retry safe. |
| TakenOver | Someone else held it but their lease had run out, so you have it now. |
| AlreadyCommitted | A vector for this text is stored already. The chunk is a duplicate. |
| InFlight | Someone else holds it and the lease has not ended. Come back later. |

The holder is a name you pass in for one piece of work. The queue message id is a good one: if the message is delivered again after a crash or a timeout, the retry has the same holder and gets its claims back right away instead of waiting for the lease.

Things that follow from this:

- A failed chunk is never marked as seen. A pending claim is invisible to the duplicate checks, and a released claim leaves nothing behind.
- If a worker dies without releasing, nothing is stuck for good. The lease runs out and another worker takes over.
- Commit is safe to call twice. A worker that crashed halfway through a commit (the hash is final but the near index entries were not written) repairs it when the same commit is repeated.
- A worker whose lease ran out and was taken over gets `NotHeld` when it tries to commit, so two workers can not both finish the same chunk. If nobody took the hash over, the original worker can still commit after its lease ended.

## Near duplicates

The near duplicate stages only know about committed chunks. That has two consequences:

- A chunk that is pending on another worker is not matched by SimHash or MinHash. Two near duplicates that are processed at the same moment on different workers can both be embedded. Exact repeats are always settled correctly.
- Inside one batch the pipeline does catch near duplicates of a chunk that is still pending. The second chunk gets a claim that waits for the first. The ingestor commits it after the first chunk is committed, and releases it if the first one failed, so it never points at a chunk that has no vector.

If this matters for you, make the batches bigger, or send the chunks of one document to one worker.

## Using it

With dependency injection:

```csharp
services.AddRechunk(o =>
    {
        o.SimHash.Enabled = true;
        o.MinHash.Enabled = true;
        o.Store.LeaseDuration = TimeSpan.FromMinutes(5);   // longer than your slowest embed plus write
        o.Failures.MaxAttempts = 3;
    })
    .AddRedisStorage("localhost:6379");
```

`AddRedisStorage` and `AddSqliteStorage` replace the in memory store and dead letter store that `AddRechunk` registers. `UseInMemoryFingerprintStore()` gives you the store interface with the in memory data, which is what you want in tests and in a single process.

Without dependency injection:

```csharp
var options = new RechunkOptions();
options.SimHash.Enabled = true;
options.MinHash.Enabled = true;

var store = new BackedFingerprintStore(new SqliteFingerprintBackend("Data Source=rechunk.db"), options);
var pipeline = RechunkPipeline.CreateBuilder()
    .UseSimHash().UseMinHash()
    .UseFingerprintStore(store)
    .Build();
```

The store and the pipeline have to use the same SimHash and MinHash settings, otherwise they build different bands. Here both use the defaults. If you change a setting, change it on both (`Configure` on the builder takes the same options object). With dependency injection this is automatic.

To claim chunks yourself, give the pipeline a holder:

```csharp
var report = await pipeline.DeduplicateAsync("tenant", chunks, new DedupClaimOptions("message-123"));
// every Unique decision has a Claim. After the vector write: store.CommitAsync(claim.ToCommit()).
// If the write failed: store.ReleaseAsync(claim.ToRelease()).
```

Most callers should use `ChunkIngestor` instead, which does all of that.

## ChunkIngestor

`ChunkIngestor.IngestAsync` takes an `IngestRequest` (scope, document, chunks and a holder), and runs claim, embed, vector write and commit. It returns how many chunks were stored, how many were duplicates, how many are in flight on another worker and which chunks were dead lettered.

Retries: the whole batch is tried first. If that fails, each chunk is tried alone, so one bad chunk does not hold back the rest. A chunk gets at most `Failures.MaxAttempts` tries in total (the batch try counts as the first), with `Failures.RetryDelay` before the second try, multiplied by `Failures.RetryBackoff` for each try after that. The default is three tries, one second, doubling.

If a chunk is out of tries it goes to the dead letter store and its claim is released.

Cancellation: if the token is cancelled the claims are given back before the exception leaves, so a cancelled run does not hold chunks until the lease ends.

## Dead letters

A dead letter has the chunk id, the scope, the document, the position of the chunk in the document, the stage that failed (`Dedup`, `Embed`, `VectorWrite` or `Commit`), a reason, the number of attempts and the time.

- **No chunk text by default.** Chunk text can be personal data, so it is left out. The reason is the exception type and a shortened message (300 characters by default, `Failures.MaxReasonLength`), and if the message repeats the chunk text, that part is replaced. Set `Failures.StoreTextInDeadLetters` if you want the text kept so replay does not need to go back to the source.
- **One record per chunk.** The record id comes from the scope and the chunk id. A message that is delivered again and fails again updates the record and does not add a second one. The same text in two documents of one scope is one record, and it shows the last document that failed.
- **List and replay.** `IDeadLetterStore.ListAsync` takes a `DeadLetterQuery` (scope, stage, chunk, document, skip and take) and returns the oldest first. `ChunkIngestor.ReplayAsync` runs the matching records through the ingest again. A record that works is removed. One that fails again is updated with the new stage and reason and the attempts added up. One whose chunk another worker is handling right now is left alone and counted as deferred. If the text was not stored, pass a `textSource` that loads the chunk from your own storage, and a record without text is skipped when there is none.

### Queues and poison messages

A queue has its own idea of a failed message: after a number of deliveries it moves the message to a poison queue. That is a different thing from a dead letter, and the sample keeps them apart:

- A chunk that failed for its own reasons (the embedding service rejects it, the vector store times out again and again) becomes a dead letter. The function still returns normally, because the rest of the message is fine and was stored. Nothing is lost, the record says what to look at.
- A message with chunks that are in flight on another worker throws, so the queue shows it again later. By then the other worker has usually finished and the chunks are duplicates.
- A function that keeps throwing for any other reason ends in the poison queue after `maxDequeueCount` deliveries, as usual.

`samples/Sample.AzureFunction` has this, and a second function that replays dead letters when a message arrives on a queue.

## Backends

| | In memory | SQLite | Redis |
| --- | --- | --- | --- |
| Package | Rechunk | Rechunk.Storage.Sqlite | Rechunk.Storage.Redis |
| Shared between processes | no | on one machine, through a file | yes |
| Atomic claim | a lock | `BEGIN IMMEDIATE` transaction | Lua script |
| Survives a restart | no | yes | depends on how Redis persists |
| Good for | tests, one process | one machine, local development | several instances |

All three get the same test suite (`FingerprintStoreContract` and `DeadLetterStoreContract` in the test project). The near duplicate logic is written once in the core library. A backend only stores the exact hash rows and some opaque rows under band keys, so the backends do not each need their own idea of what a near duplicate is.

### SQLite

Tables are created on first use: `rechunk_exact`, `rechunk_band_entry`, `rechunk_band_key` and `rechunk_dead_letter`. `SqliteFingerprintBackend.SchemaSql` has the statements if you prefer your own migrations. The database is put in WAL mode.

SQLite lets one writer at a time in, so many workers on one file wait for each other (the connection default is a 30 second busy timeout). That is fine for one machine and a modest rate. Use a file, not `:memory:`, because every call opens its own connection.

### Redis

Needs Redis 5 or newer (Lua scripts). Keys start with `KeyPrefix` (`rechunk` by default), so the database can be shared with other things. The scope of a key is in a hash tag, so all keys of one scope sit on the same slot in Redis Cluster and the scripts can touch several of them at once. Dead letters use one tag for all of them because they are listed across scopes.

Lease times are stored as unix milliseconds. Dead letters are sorted sets scored by failure time. Listing with a filter on stage, chunk or document reads the matching range and filters in the client, which is fine for the size a dead letter store normally has and slow for a very large one.

## SQL Server

There is no SQL Server backend yet. [docs/sqlserver-schema.sql](sqlserver-schema.sql) is a T-SQL version of the schema, so a backend (or your own implementation of `IFingerprintBackend` and `IDeadLetterStore`) has something to start from. It has not been run against a SQL Server.

## Limits and things to know

- **Clocks.** Leases use the clock of the worker that claims or checks them. Keep the lease much longer than the difference between your workers' clocks. Five minutes is a safe default for NTP synced machines.
- **The lease does not renew itself.** If one message can take longer than `Store.LeaseDuration` to embed and write, raise it. A worker whose lease ran out can still commit if nobody took the hash over, and gets `NotHeld` (counted in `IngestResult.LostClaims`) if somebody did. Writing the same vector twice is harmless because ids come from the text and the upsert replaces.
- **A vector can exist without a commit.** If the vector is written and the commit then fails for good, the chunk is dead lettered and its claim is released. The vector stays. Replay writes the same id again and commits it.
- **Cross worker near duplicate races** are described above. The exact stage has no such gap.
- **Dry run** with a shared store claims nothing and writes nothing, same as before.
- **Redis Cluster** is designed for through the hash tags but has only been tested on a single Redis.
- **Scripts** are sent with `EVAL`. `EVALSHA` caching would save some bandwidth and is not done.
