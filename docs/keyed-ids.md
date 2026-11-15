# Keyed chunk ids

With a key provider on the pipeline, a chunk id is `k2.` plus the HMAC-SHA256 of the SHA-256 of the cleaned text, under that scope's key. A language tag is still added after the id when the chunk has one. The same text in two scopes gets two ids, because each scope has its own random key. The keys are not derived from each other.

With no provider, ids stay the hex digest from 0.1. A scope that already has one kind of row refuses the other. Nothing is migrated. Create the scope key before the first write. A missing key, a wrong wrapping key, or a wrapped record that does not authenticate fails the write. It does not fall back to an unkeyed id, and it does not replace the stored row.

```csharp
var store = new SqliteWrappedKeyStore("Data Source=rechunk.db");
using var keys = new LocalWrappedKeyProvider(wrappingKey, store);
await keys.CreateAsync("tenant-a");

var pipeline = RechunkPipeline.CreateBuilder()
    .UseKeyProvider(keys)
    .UseSimHash()
    .UseMinHash()
    .UseFingerprintStore(new BackedFingerprintStore(new SqliteFingerprintBackend("Data Source=rechunk.db"), new RechunkOptions { KeyProvider = keys }))
    .Build();
```

The fingerprint store has to be built with the same provider. The wrapping key stays with the caller. It is not a column. A new process that opens the SQLite file with that same wrapping key gets the same scope key. A backup of the file still has the wrapped keys, so deleting a row is not secure erase.

Fingerprint rows record the hash version and the key version. Version 1 is the unkeyed digest id. Version 2 is the HMAC id. The exact lookup is still the SHA-256 of the cleaned text, so a reader of the table can still match a guessed text against that column until a later migration. SimHash and MinHash signatures are stored in the clear until a later encryption change. Keyed ids alone do not make the database private.

## Band lookup keys

SimHash and MinHash are unchanged. What changes is the bucket key written for each band.

The bucket key is SipHash-2-4 of the band kind, the band number, and the raw slice. SipHash-2-4 is a keyed pseudorandom function for short messages. Its output is 64 bits, so two different slices can share a bucket. That only adds a candidate. The real SimHash distance or the MinHash estimate still decides whether the chunks match. SipHash is not used for the chunk id. The id stays HMAC-SHA256.

The 128 bit SipHash key is HMAC-SHA256 of the label `rechunk-band-siphash-v1` under the scope key. It is not a copy of the scope key's bytes. The same raw slice in two scopes is stored under two different bucket keys. Someone who can read the index still cannot look up a new text without the scope key. Someone who can read the signature payload can see the SimHash and the MinHash. Hiding that payload is a separate change.
