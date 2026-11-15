# Changelog

All notable changes to Rechunk are listed here.

## 0.2.0 (planned)

### Keyed chunk ids
Chunk ids and fingerprints are built with a secret key per scope, so a stored id can no longer be matched against a guessed text. Destroying a scope's key makes everything stored for that scope unreadable. This changes every chunk id, so existing stores need the migration step that ships with this release.

### Signature encryption
When a key provider is set, SimHash and MinHash payloads in SQLite and Redis are AES-GCM ciphertext. The data key is derived from the scope key with its own label. A tampered or swapped payload fails to open. The exact digest column is still stored, and the in memory indexes are not encrypted. See docs/threat-model.md.

### Keyed id migration
`KeyedIdMigrator` rewrites one scope from digest ids to keyed ids in memory, SQLite or Redis. It does not run unless you call it. A dry run only reports counts and the old id to new id map, which is what you use to update a vector store. Writes to that scope are refused while a run is in progress. The scope version becomes 2 only when the run finishes. A wrong wrapping key fails before anything is marked. The migration encrypts signature payloads. The exact digest stays as the lookup key. Audit rows and dead letters keep the old ids. See docs/keyed-ids.md.

### Keyed ids on the write path
When a key provider is set, chunk ids written by the pipeline are HMAC ids, and fingerprint rows in memory, SQLite and Redis record hash version 2 and the key version. Exact and near duplicate matches stay inside the scope. A scope that already has the other hash version is refused. With no provider, ids and rows stay as they were in 0.1. The exact digest is still stored. Band lookup keys are SipHash-2-4 under a key derived from the scope key. See docs/keyed-ids.md.

### Key provider groundwork
A caller can wrap a random key per scope, and a helper can build an HMAC chunk id from cleaned text.

### Wrapped scope keys in SQLite
The SQLite package can keep each scope's wrapped key in the same file as the other Rechunk tables. A later process that opens that file with the same wrapping key gets the same scope key. The wrapping key is not stored in the file. A backup of the file still contains the wrapped keys, so deleting a row is not secure erase. The caller has to keep the wrapping key.

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
