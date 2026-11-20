# Threat model

This is the state of the library for the keyed-id work. It is not a promise that a database file is private.

## Assets

- The scope key, and the wrapping key that locks it.
- Chunk ids. With a key provider these are HMAC ids. Without one they are the hex digest.
- The exact digest column. This is SHA-256 of the cleaned text, and it is the lookup key.
- Band lookup keys, and the SimHash and MinHash payloads.
- Revision rows, audit rows, dead letters, and whatever text a caller chose to store in a dead letter.
- Subject links, when a key provider is set. Each row is a scope, a chunk id, a kind, and a keyed subject id.
- Vectors. Those live in the caller's vector store, not in Rechunk.

## Attackers

- Someone who can read the SQLite file, a backup, or a copy of that file.
- Someone who can read the Redis keys for a scope.
- Someone who can read the process memory of a worker.
- A caller who loses the wrapping key, uses the wrong one, or turns keyed ids on for a scope that already has digest rows.

## What holds

With a key provider, and after a migration for an old scope:

- A chunk id is not the digest. Guessing the text does not produce the stored id unless you have the scope key.
- The wrapping key is not written next to the wrapped scope key. A wrong wrapping key does not open the scope key and does not replace the stored row.
- Band lookup keys are SipHash-2-4 under a key derived from the scope key (`rechunk-band-siphash-v1`). The raw slice is not the stored bucket key. A collision only adds a candidate.
- SimHash and MinHash payloads in SQLite and Redis are AES-GCM. The data key is HMAC-SHA256 of a different label (`rechunk-payload-aes-v1`) under the scope key. Each value has its own 12 byte nonce and 16 byte tag. The associated data is the scope, the chunk id, and the field name. A changed byte, or a value moved to another row or field, fails to open. The read fails closed.
- A subject id is HMAC-SHA256 of the label `rechunk-subject-v1`, the kind, and the normalized value, under the scope key. The row stores that id and the kind. It does not store the value. An access report returns that id, chunk ids, counts, sources, document ids and revision times. It does not return the value or the chunk text. The audit line for a report is counts only.

Without a key provider, ids, band keys, and payloads stay in the 0.1 form.

## What does not hold

- The exact digest is still stored. Someone with the database can hash a guessed text and see if that column matches. Keyed ids hide that from a caller who only sees ids. They do not hide it from someone who can read the row.
- Subject rows do not hold the email, the phone number, or the caller id. They do show the kind, and which chunks share a subject. Without the scope key a guessed value does not produce the stored id. With the scope key, or with the chunk text from somewhere else, that guess works. The audit count names the kind and not the value.
- The in memory indexes keep signatures in process memory. Encryption is for the SQLite and Redis copies.
- A backup or a copy of the file still has the wrapped key and the ciphertext. Deleting a row is not secure erase.
- Audit rows and dead letters are not rewritten by the migration. A dead letter can still hold text if that option is on.
- Vectors, embeddings, and any store outside Rechunk are unchanged. The migration reports old id to new id so the caller can update them.
- There is no scope key rotation. A new scope key does not re-encrypt old payloads. Destroying a local wrapping key makes those scope keys unreadable. It does not wipe copies.
- `Rechunk.Keys.AzureKeyVault` can wrap scope keys with a key that stays in Key Vault. The process receives the unwrapped scope key, not the vault key. Each stored record keeps the vault key id and version that wrapped it, and unwrap uses that version. A newer vault key does not by itself open or break those records, as long as the recorded version can still unwrap. This package is not in the 0.2.0 NuGet release.

## Key handling

The caller can pass a 32 byte wrapping key. The scope key is 32 random bytes, wrapped with AES-GCM, and stored if you use `SqliteWrappedKeyStore`. It is not derived from the wrapping key or from another scope. The band key and the payload key are each an HMAC of a fixed label under the scope key. Losing the wrapping key means the scope key in that file cannot be opened. With the Key Vault package, the vault key stays in the vault and the store keeps the wrapped scope key plus the vault key version. The process keeps the unwrapped scope key, and the derived band and payload keys, until it exits.
