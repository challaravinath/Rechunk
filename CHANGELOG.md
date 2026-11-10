# Changelog

All notable changes to Rechunk are listed here.

## 0.2.0 (planned)

### Keyed chunk ids
Chunk ids and fingerprints are built with a secret key per scope, so a stored id can no longer be matched against a guessed text. Destroying a scope's key makes everything stored for that scope unreadable. This changes every chunk id, so existing stores need the migration step that ships with this release.

### Threat model
A short document on what Rechunk protects, what it does not, and what still leaks, such as embeddings in your vector store.

SQL Server, lease renewal and the other items that used to be listed here have moved to later versions. See ROADMAP.md.

## 0.1.0 (2026-11-07)

First NuGet release.

### Dedup before embedding
Each chunk is checked by exact hash, then SimHash, then MinHash with LSH. Only new content reaches the embedder, which cut embedding calls by 21.6% on a dotnet/docs sample.

### Revision tracking
When a document changes, only the chunks whose text changed get embedded again, and stale vectors are removed.

### Shared store for stateless workers
SQLite or Redis keeps track of what has been seen, with leases, pending then commit writes and a dead letter queue. Azure Functions and other stateless workers never embed the same chunk twice.

### PII scan, audit trail and erasure
Chunks are scanned before fingerprinting, and you can flag, mask or block them. Every decision goes into an audit trail, and erasure removes chunks and vectors together and leaves a receipt.

### Packages
- Rechunk: the core library
- Rechunk.Storage.Sqlite and Rechunk.Storage.Redis: the shared fingerprint store
- Rechunk.VectorData: a writer for Microsoft.Extensions.VectorData collections
- Rechunk.SemanticKernel: registers Rechunk on a kernel builder
- Symbol packages and SourceLink for debugging

Rechunk.Extensions.AI is not in this release. Its wrapper still forwards every input to the inner generator.
