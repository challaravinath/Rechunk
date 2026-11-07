# Revisions and clean deletes

This page is about what happens when a document you already ingested comes in again with changes, and when a document is removed. The goal is simple: embed only what changed, and leave no old vectors behind.

## What it does

You give `RevisionTracker` the new version of a document, already split into chunks. It compares the chunks with the version it has on record and sorts them:

- **Keep.** The text is already in the document, in the same place or in a different one. The vector stays and nothing is embedded. A paragraph that moved is kept, and the new position is recorded.
- **Embed.** The text is new. It goes through the normal ingest (claim, embed, write, commit), so the three dedup stages still apply. A paragraph that is a copy of one in another document is not embedded either, it is skipped like any other duplicate.
- **Replace.** The text is new and it sits where an old chunk used to be. It is embedded like any new chunk, and the manifest remembers which chunk it replaced.
- **Delete.** The old chunk is not in the new version. Its vector is removed, and so are its fingerprints in all three dedup stages.

A chunk is only deleted when no document uses it. If two documents share a footer, editing one of them leaves the vector alone, and it goes when the last document lets go of it.

```csharp
var tracker = provider.GetRequiredService<RevisionTracker>();

var result = await tracker.ReviseAsync(
    new DocumentId("handbook"), "tenant-1", chunks, embedder, sink, holder: message.Id);

// result.Embedded, result.Kept, result.Moved, result.Deleted, result.EmbeddingCalls, result.Status
```

`RevisionTracker` is registered by `AddRechunk`. Without dependency injection, build it from a pipeline that has a fingerprint store: `new RevisionTracker(pipeline, revisionStore, deadLetterStore)`. It throws when the pipeline has no store, because forgetting a chunk means removing its shared fingerprints.

`samples/Sample.Console` runs a short example: an edit, a new paragraph, a move and a delete.

## The manifest

For every document the revision store keeps a manifest: the revision number and, for each chunk, its position, id, language, the revision in which it first appeared, the position it had before (when it moved) and the chunk it replaced (when it is an edit). Read it with `GetManifestAsync`.

The revision number goes up by one each time the chunks change. Sending the same chunks in the same order again gives `Unchanged` and writes nothing.

The manifest never holds chunk text. Ids are hashes of the cleaned up text.

## Plan and apply

`PlanAsync` only looks. It returns the action for every chunk, a reason (for example "moved from position 3") and the list of chunks that would be deleted. Use it for a dry run or a preview.

`ApplyAsync` carries out a plan. It does not trust the plan blindly: the document may have changed since, so it plans again against the current state. A plan that went stale gives a correct result, not a corrupted one.

`ReviseAsync` is both steps in one call, and is what most code wants.

## Many workers and crashes

The tracker works through the shared store from the previous page ([shared-store.md](shared-store.md)), so any number of stateless workers can revise documents at the same time. Three things make that safe.

**The order of the steps.** A revision writes the new vectors first, switches the document to the new revision second, and deletes the old vectors last. A crash in between never loses data:

- before the switch, the document is still on its old revision and the new vectors are only extra. Running the same revision again reuses them (same holder, same claims) and finishes the job.
- after the switch but before the delete, the old vectors are unreferenced. The next revision of anything, or a call to `SweepAsync`, deletes them.

**The switch is a compare and swap.** A revision says which revision it was based on. If another worker moved the document in the meantime, the switch is refused and the tracker plans again, up to `Revisions.MaxConflictRetries` times with `Revisions.ConflictDelay` between tries. If that runs out the result has status `Conflict` and nothing changed.

**Counting.** Each chunk has a count of the documents that use it. A chunk at zero is orphaned and waits for a sweep. A sweeper takes a chunk with a lease (`Revisions.RetireLease`) before it deletes anything, and while the chunk is taken no revision can start using it: that revision gets `Blocked`, waits, and then embeds the text again from scratch. If a sweeper dies, the lease runs out and another worker finishes the delete. Deleting a vector that is already gone is not an error, so repeating any of this is safe.

The order inside a sweep is: take the chunks, delete the vectors, forget the fingerprints, then remove the chunk from the store. If the vector delete fails the chunks stay taken, and a later sweep tries again.

### Statuses

| Status | Meaning |
| --- | --- |
| Applied | The new revision is current. |
| Unchanged | The chunks were the same as the current revision. Nothing was written. A sweep still ran, so a delete that a crashed worker left is finished. |
| Incomplete | Some chunks are with other workers or went to the dead letter store. The document is still on its old revision. Run the same revision again later. |
| Conflict | Other workers kept changing the same document and the retries ran out. |
| Blocked | A chunk that had to be reused was being deleted and the retries ran out. |

## Deleting a document

`DeleteDocumentAsync` is a revision with no chunks. The manifest stays, empty, and the chunks that nothing else uses are removed. A document can be written again afterwards and its chunks are embedded again.

`EraseAsync` is the right to erasure version. It does the same, and also removes the dead letters of the document. The result counts the vectors deleted and the fingerprint rows and dead letters removed. It names a document, a source, or both (see [erasure.md](erasure.md), which also covers erasing by text, by chunk and by finding), and throws an `ArgumentException` if it names neither. If the erase can not finish because the document keeps changing, it throws an `InvalidOperationException` and you call it again.

An audit event records each keep, embed, replace and delete with the revision, the embedding model and a reason, and never the text. Register an `IAuditLog` to get them (there is an `InMemoryAuditLog` for tests). If writing an audit event fails, the failure is logged and the revision is not undone.

## Checking for leftovers

`VerifyAsync(scope, sink)` compares the chunks the revision store says should exist with the ids the sink lists:

- **Orphan vectors** are in the sink and not in the store. They come from a crash between writing a vector and switching the document, or from vectors written outside the tracker. A revision running right now can show up here too, because its vectors are written before the switch.
- **Missing vectors** are in the store but not in the sink, which means something deleted them from the vector store by hand.

`SweepAsync(scope, sink)` deletes what no document uses. Call it on a timer if you want the delete to be finished soon after a crash, and not at the next revision.

## Policies and what they mean for revisions

With `OnRevision = ReEmbedChanged` (the default), an edited paragraph is embedded again and is never matched with the old text it replaces, so an old vector can not stand in for the new text.

With `ReuseForSmallEdits`, a small edit can be matched as a near duplicate of the old chunk. The new text is then stored as an alias of the old chunk, no embedding call is made, and the old vector stays. That is cheaper, but the vector describes the old wording. Aliases are tracked: they go when the chunk they point at goes.

## Vector stores

`IVectorSink` has a `DeleteAsync(scope, ids)` that revision tracking uses, and `ListIdsAsync` for the verify step.

`Rechunk.Extensions.VectorData` has a sink for any `Microsoft.Extensions.VectorData` collection. You tell it how an embedded chunk becomes your record, and how a scope and chunk id become your key:

```csharp
var sink = new VectorDataSink<string, Doc>(collection, new VectorDataMapping<string, Doc>
{
    ToKey = VectorDataMapping<string, Doc>.ScopedKey,
    ToRecord = c => new Doc(c.Scope + ":" + c.Id.Value, c.Scope, c.Id.Value, c.Text, c.Vector),
    ScopeFilter = scope => d => d.Scope == scope,   // only needed for VerifyAsync
    ToId = d => new ChunkId(d.ChunkId),             // only needed for VerifyAsync
});
```

Writes and deletes go in batches (100 by default) and a batch that fails is tried again as a whole (3 tries by default). That is safe because an upsert with a fixed key replaces the record, and deleting a key that is not there is ignored.

Chunk ids come from the text, so the same text in two scopes has the same id. `ScopedKey` puts the scope in a string key. The sink refuses a `ToKey` that leaves the scope out. If your vectors are already stored under the bare chunk id, give them a new key or ingest them again.

## Backends

All three revision stores (in memory, SQLite, Redis) run the same contract tests, including races between several workers. SQLite keeps the manifests in `rechunk_doc`, `rechunk_doc_chunk`, `rechunk_ref` and `rechunk_alias`. Redis does the switch in one Lua script, using the same scope hash tag as the fingerprint store, so all of a scope sits on one slot in a cluster.

## Limits

- Only documents you send through the tracker are tracked. Chunks that were ingested with `ChunkIngestor` on its own have no references, so a delete of a document does not know about them. `VerifyAsync` shows them as orphans.
- A revision that is in progress looks like it has orphans to `VerifyAsync`. Run verify when the workers are quiet if you need a strict answer.
- Erase by source needs the document to have been revised with a source, see [erasure.md](erasure.md).
- Vectors are matched to documents by chunk id within a scope. Key records by scope and id. Rows stored under the bare chunk id need a new key or a fresh ingest.
