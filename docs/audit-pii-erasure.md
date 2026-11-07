# Audit, PII and erasure

The audit log is on by default and holds no chunk text. PII scanning is off until you turn it on:

```csharp
services.AddRechunk(o =>
{
    o.Pii.Enabled = true;
    o.Pii.Set(PiiType.ApiKey, PiiPolicy.Block);   // never stored or embedded
    o.Pii.Set(PiiType.Email, PiiPolicy.Mask);     // [EMAIL] goes into the dedup and the embedding
});                                               // every other type is flagged

await tracker.ReviseAsync(doc, "tenant-1", chunks, embedder, sink, holder: id, source: "wiki");

// what happened to a document, and what a source contributed this month
var events = audit.QueryAsync(new AuditQuery(Document: doc));
var month = audit.QueryAsync(new AuditQuery(From: start, To: end) { Source = "wiki" });

// right to erasure: everything that came from a source, with a receipt
var result = await tracker.EraseAsync(new EraseRequest("tenant-1", null, "wiki", "ticket 4711", "support"), sink);
```

A PII finding is stored as a type and a span, never the value. Masking happens before the fingerprints are made, so two chunks that differ only in an email address become one. The built in detector is a set of rules with checksums and context words, and it will miss things, so use it as one layer. Details and limits are in [audit.md](audit.md), [pii.md](pii.md) and [erasure.md](erasure.md).
