# Changelog

All notable changes to Rechunk are listed here.

## 0.3.0 (unreleased)

Subject index. Chunks can be linked to the people they are about. The stored id is an HMAC under the scope key, and the value is not stored. A scope with no key provider does not build the index. See docs/how-it-works.md.

Access report for one subject. Counts, sources, documents, revisions and dates. The value and the chunk text are not in the report or in the audit line.

## 0.2.1 (2026-11-15)

### Key Vault key provider
`Rechunk.Keys.AzureKeyVault` wraps each scope key with a key that stays in Azure Key Vault. The wrapped bytes and the vault key id and version are stored in the existing wrapped key store. Unwrap uses the recorded version, so a later vault key still opens old records while that version can unwrap. See docs/keyed-ids.md.

Existing SQLite files gain three nullable columns on the wrapped key table the next time the database is opened.

## 0.2.0 (2026-11-15)

Keyed ids for a scope that has a key provider. Stores that do not set a provider keep the 0.1 behavior. See docs/keyed-ids.md and docs/threat-model.md.

### Keyed ids
When a key provider is set, chunk ids on the write path are HMAC-SHA256 of the cleaned-text digest, under a random key for that scope. The id starts with `k2.`. The same text in another scope gets another id. Exact and near matches stay inside the scope. Fingerprint rows in memory, SQLite and Redis record hash version 2 and the key version. A scope that already has the other version refuses the write. With no provider, ids stay the hex digest.

### Wrapped keys
`SqliteWrappedKeyStore` keeps each scope key as AES-GCM ciphertext in the SQLite file. The wrapping key is supplied by the caller and is not stored. A later process that opens the file with the same wrapping key gets the same scope key. A wrong wrapping key fails and does not replace the row. A backup of the file still has the wrapped keys.

### Migration
`KeyedIdMigrator` rewrites one scope from digest ids to keyed ids. It does not run unless you call it. A dry run reports counts and the old id to new id map. The real run saves one row at a time, so a stopped run can continue. Writes to that scope are refused until it finishes. The scope version becomes 2 only at the end. A wrong wrapping key fails before anything is marked. The id map is how you update a vector store. Rechunk does not own those vectors.

### Signature encryption
With a key provider, SimHash and MinHash payloads in SQLite and Redis are AES-GCM ciphertext. The data key is derived from the scope key with its own label. A tampered payload, or one moved to another row or field, does not open. Migration encrypts the payload as it rewrites a row. The in memory indexes are not encrypted.

### Threat model
docs/threat-model.md says what a database file, a backup, Redis, and process memory expose, and what they do not.

### Upgrading from 0.1

1. Keep a 32 byte wrapping key outside the database. Create the scope key with `LocalWrappedKeyProvider` and, if the key should survive a restart, `SqliteWrappedKeyStore`.
2. Pass that provider to the pipeline and to the fingerprint store. Do this for a scope that already has rows only when you are ready to migrate it.
3. Call `KeyedIdMigrator.RunAsync` with `MigrationRun.DryRun` set. Read the counts and the `OnMapped` pairs.
4. Run it again without the dry run. The scope refuses fingerprint writes and revision commits until the run finishes.
5. Update ids in your vector store from `OnMapped`. Rechunk does not write those vectors.
6. A store with no provider is unchanged. A scope that still has digest rows refuses keyed writes. A scope that has been migrated refuses digest writes.
7. If you implemented `IFingerprintStore` or `IFingerprintBackend` yourself, add `ListOwnedHashesAsync` and `FindHashByTextIdAsync`. Deletes of keyed ids use them, because the id is no longer the digest.

### Known limits

- The exact digest is still the lookup key. Someone with the database can test a guessed text against that column.
- In memory indexes keep signatures in process memory.
- There is no key rotation and no Key Vault provider. A Key Vault provider is planned for 0.2.1.
- A backup or a copy of the file still has the wrapped key and the ciphertext. Deleting a row is not secure erase.
- Audit rows and dead letters keep their old ids. A dead letter can still hold text if that option is on.
- A scope that is already on version 2 is not migrated again.

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
