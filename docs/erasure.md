# Erasure

Rechunk can remove what it stored about a person, a document or a source: the vector, the chunk in the revision store and every fingerprint (exact, SimHash, MinHash) that points at it. It then leaves a receipt in the audit log that says what was removed, in counts, and not what the content was.

## By document or by source

```csharp
var result = await tracker.EraseAsync(
    new EraseRequest("tenant-1", new DocumentId("handbook"), null, "ticket 4711", "support-agent"),
    sink);

var all = await tracker.EraseAsync(
    new EraseRequest("tenant-1", null, "wiki", "customer left", "support-agent"), sink);
```

- A document is erased with its vectors, fingerprints and dead letters. It keeps a manifest with no entries and no text, so a later revision of the same id starts clean.
- A source erases every document that was revised with that source (`ReviseAsync(..., source: "wiki")`). Documents are found in pages, so there is no limit on how many.
- With both a document and a source, the document has to belong to the source. If it does not, nothing is erased and the call throws.
- With neither, it throws an `ArgumentException`.
- A chunk that a document outside the erase still uses is not deleted. It stays as long as that document does. Erasing a source does not remove another source's data.
- Erasing is scoped. A different scope is not touched.

The source is kept with the document by the revision store (SQLite and Redis keep it too), and it can be a connector name, a site, a bucket or anything else that fits in a string. A revision that does not name a source keeps the one the document has. Documents revised before sources were tracked have none and can only be erased by document.

## By text, by chunk and by finding

```csharp
// every chunk that holds this text, in any document
var found = await tracker.FindByTextAsync("tenant-1", text);
await tracker.EraseTextAsync("tenant-1", text, sink, "ticket 4712", "support-agent");

// near copies as well (the text was edited slightly before it was stored)
await tracker.EraseTextAsync("tenant-1", text, sink, "ticket 4712", "support-agent", mode: TextMatch.Near);

// chunks by id, for example from a query of the audit log
await tracker.EraseChunksAsync("tenant-1", ids, sink, "ticket 4713", "support-agent");

// every chunk that had a card number, according to the audit log
await tracker.EraseFindingsAsync("tenant-1", PiiType.Card, sink, "card leak", "security");
```

`EraseTextAsync` hashes the text the way ingest does, so it finds a chunk even when the original had other whitespace, and when PII masking is on it looks for the masked form. `Near` also uses the SimHash and MinHash indexes. A near search can match more than you expect: look with `FindByTextAsync(mode: Near)` first.

`EraseFindingsAsync` needs the audit log, because the findings are recorded there. It only finds chunks that were scanned while the scan and the audit log were on, and it is incomplete when the audit log is the in memory one that dropped old events, or when events still wait in the outbox. It can be limited with `from` and `to`.

In every case each document that used a removed chunk is revised without it, and it is a normal revision (the revision number goes up). The vector is deleted by the same ordered sweep as any other delete: take the chunk, delete the vector, forget its fingerprints including the ones of duplicates that were stored as aliases, then remove the chunk. A chunk that no revision ever knew about is deleted directly.

## The Skip policy edge case

With the Skip duplicate policy, a chunk that is a near copy of a stored one is not stored. Its text still gets an exact fingerprint row of its own, pointing at the chunk it matched, so the same text is recognised at once the next time. An erase has to remove that row too, or the hash of a text that someone asked to be forgotten stays in the index. Rechunk removes it:

- when the chunk it points at is deleted, the rows of its skipped copies are forgotten with it,
- when the document that had the skipped copy is erased or revised, the row goes with the document, and the original chunk stays if another document still uses it,
- a row that two documents share goes when the last of them lets go.

`EraseTextAsync` with a text that no chunk and no row holds does nothing.

After erasing, ingesting the same text again brings it back as new text. That is a choice of the caller. If the source keeps sending it, block it at the source.

## The receipt

Every erase writes one `ErasureReceipt` event, and one `Erased` event per chunk that was removed, with the document and source. The receipt holds:

- the counts: `documents`, `chunks`, `vectors` and `state_rows` (fingerprint rows and dead letters removed),
- who asked (`RequestedBy`) and the reason you gave,
- the time, the scope, and the document or source when you named one.

It does not hold text. The Reason is kept as you gave it (cut at 200 characters, line breaks removed), so do not put personal data in it. Use a ticket number. `EraseResult.ReceiptId` is the id of the event, and the result also counts `Documents`, `Chunks`, `VectorsDeleted` and `StateRowsDeleted`. A tracker without an audit log still erases, and returns no receipt.

If the erase fails half way, for example because the vector store is down, it writes a receipt with the count `incomplete` and throws. The documents that were changed stay changed, and the vectors that could not be deleted stay marked. Run the same erase again, it finishes the delete.

## What erasure does not do

- It does not touch your source documents, your chunker's output or your embedding provider.
- It does not remove the chunk id from the audit log. The id is a hash of the text. Events keep the document, time and kind. Remove audit events through your audit store if your rules require it.
- It cannot find a vector that is in the sink but unknown to everything else. `VerifyAsync` lists those, and `EraseChunksAsync` can delete them by id.
- Vector ids are global in a vector sink and are not split by scope. Two scopes with the same text share the same chunk id in the sink, so erasing in one scope deletes the vector for both. Use one sink per tenant if that matters.
- On Redis, data written before this version has no source and chunk indexes. Those documents are found by source or by chunk after their next revision. Erase them by document until then. On SQLite the index is added on first use, and old documents have no source.
- It is not legal advice. It removes what Rechunk stored. Whether that meets your obligations is for you and your lawyer.
