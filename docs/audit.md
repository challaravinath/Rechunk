# Audit trail

Rechunk can keep a record of what it did with every chunk: which ones were embedded, which were left out as duplicates, which were replaced or deleted, which were kept out by the PII policy, and which erase requests ran. The record never holds chunk text.

## What is written

One `AuditEvent` per chunk and event. The kinds are:

| Kind | When |
| --- | --- |
| `New` | a chunk was embedded and stored for the first time |
| `Skipped` | a duplicate was left out under the Skip policy |
| `Aliased` | a duplicate was left out and remembered next to the chunk it matched (the default policy) |
| `Flagged` | a duplicate was embedded anyway and flagged (Flag policy) |
| `Revised` | a revision replaced a chunk with a new one |
| `Deleted` | a chunk was deleted because its document changed or was deleted |
| `Erased` | a chunk was erased on request |
| `DeadLettered` | a chunk could not be processed and went to the dead letters |
| `Blocked` | a chunk was kept out by a Block finding of the PII scan |
| `ErasureReceipt` | the record of one erase request, with counts and who asked |

Every event carries the chunk id, the document id, the revision, the source of the document, the scope, the language (when there is one), the stage that decided (`pii`, `exact`, `simhash`, `minhash`, `embed`, `commit`, `revision`, `erasure`), the embedding model, a short reason, who caused it (`Actor`), the time, and the chunk it duplicates or replaces (`Related`) when there is one. A PII finding is added as a type and a span, for example "card, characters 120 to 139". The value is not stored.

The chunk id is the SHA-256 of the cleaned up text, so it is not the text, but it is a fingerprint of it. Anyone who has the exact text can compute it and look for it. Treat the audit log with the same care as the fingerprint store.

## Reading it

```csharp
var log = provider.GetRequiredService<IAuditLog>();

await foreach (var e in log.QueryAsync(new AuditQuery(Document: new DocumentId("handbook"))))
{
    Console.WriteLine($"{e.At:u} {e.Kind} revision {e.Revision} {e.Reason}");
}
```

`AuditQuery` filters by document, chunk, source, scope, kind, the type of a PII finding and a time range. `From` and `To` are both inclusive, and time is compared to the millisecond. Combine filters freely. Results come oldest first with the event id as a tie break, so `Skip` and `Take` page in a stable order.

```csharp
new AuditQuery(From: monthStart, To: monthEnd) { Source = "wiki", Kind = AuditKind.Erased, Take = 100 }
```

## Where it lives

| Backend | How |
| --- | --- |
| in memory | the default. Keeps the newest `Audit.InMemoryLimit` events (50000), so it is for tests and demos |
| SQLite | `AddSqliteStorage(...)` switches the audit log and its outbox to the same file as the other state |
| Redis | `AddRedisStorage(...)` does the same. Events are JSON strings, with sorted sets as indexes by document, chunk, source, scope, finding type and time |

All three run the same contract tests, so a query gives the same answer on each. You can also implement `IAuditLog` (`AppendAsync`, `QueryAsync`, and optionally `AppendManyAsync`) for another store, and register it before `AddRechunk`. Appending an event with an id the log already has changes nothing, so a retry is safe.

The audit log is registered by default, so `RevisionTracker` and `ChunkIngestor` write to it without any setup.

## It is part of the reliable flow

An audit write happens after the work it describes was committed. It must not make a finished revision look failed, and it must not be silently dropped when the audit store has a bad moment. So:

1. A write that fails is tried again, `Audit.MaxAttempts` times (3), with a delay that starts at `Audit.RetryDelay` (50 ms) and grows by `Audit.RetryBackoff` (2).
2. When the tries run out, the events go to the audit outbox (`IAuditOutbox`). The outbox is in the same store as the rest of the state with SQLite and Redis, so it survives a restart. The in memory one does not.
3. Every revision, erase and sweep starts by sending what waits in the outbox, oldest first, `Audit.DrainBatchSize` at a time. It stops at the first failure and keeps the rest. `tracker.FlushAuditAsync()` does the same on demand and returns how many are still waiting. `RevisionResult.AuditPending` shows the same number after a revision.
4. Only when the audit log and the outbox are both down are events lost. That is logged and counted in `AuditWriter.LostCount`.

Limits:

- There is a window between the commit of a revision and the audit write. A process that dies in that window leaves a revision without its events. The data is right, the trail is missing those lines. Closing it fully needs the events written in the same transaction as the manifest, which is only possible when both are in the same database. It is not done here.
- With the in memory audit log, or events that still wait in the outbox, a query is incomplete.

## Using it for compliance questions

- What happened to a document: `AuditQuery(Document: id)`.
- Where did a chunk go: `AuditQuery(Chunk: id)`.
- What did a source contribute in a period: `Source`, `From`, `To`.
- What PII was seen: `FindingType`. This returns spans and chunk ids. To remove those chunks, see [erasure.md](erasure.md).
- Proof that an erase ran: `Kind = AuditKind.ErasureReceipt`.

There is no lookup by the value of a chunk's text, on purpose. The log has no digest of the text beyond the chunk id.
