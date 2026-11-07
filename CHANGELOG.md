# Changelog

## 0.1.0

First NuGet release.

- Rechunk: exact hash, SimHash and MinHash dedup, revisions, audit, PII scanning and erasure
- Rechunk.Storage.Sqlite and Rechunk.Storage.Redis for the shared fingerprint store
- Rechunk.VectorData writer for Microsoft.Extensions.VectorData collections
- Rechunk.SemanticKernel, which registers Rechunk on a kernel builder
- Symbol packages and SourceLink

Rechunk.Extensions.AI is not in this release. The wrapper still forwards every input to the inner generator.
