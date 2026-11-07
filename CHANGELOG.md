# \# Changelog

# 

# All notable changes to Rechunk are listed here.

# 

# \## 0.2.0 (planned)

# 

# \### SQL Server and Azure SQL store

# A new `Rechunk.Storage.SqlServer` package. Teams that already run on SQL Server or Azure SQL can share dedup state across workers without adding Redis.

# 

# \### Lease renewal

# Workers keep renewing the lease on a chunk while a slow embed is still running. Long batches or a throttled embedding API will no longer cause the same chunk to be embedded twice.

# 

# \### Near copies across workers

# Clear behavior when two workers see near copies of the same text at the same moment, along with documentation of the trade-off. You will know exactly when a double embed can happen and how it gets cleaned up.

# 

# \### API documentation

# XML docs on the public API, so every type and method explains itself in IntelliSense.

# 

# \## 0.1.0 (2026-11-07)

# 

# First NuGet release.

# 

# \### Dedup before embedding

# Each chunk is checked by exact hash, then SimHash, then MinHash with LSH. Only new content reaches the embedder, which cut embedding calls by 21.6% on a dotnet/docs sample.

# 

# \### Revision tracking

# When a document changes, only the chunks whose text changed get embedded again, and stale vectors are removed.

# 

# \### Shared store for stateless workers

# SQLite or Redis keeps track of what has been seen, with leases, pending then commit writes and a dead letter queue. Azure Functions and other stateless workers never embed the same chunk twice.

# 

# \### PII scan, audit trail and erasure

# Chunks are scanned before fingerprinting, and you can flag, mask or block them. Every decision goes into an audit trail, and erasure removes chunks and vectors together and leaves a receipt.

# 

# \### Packages

# \- Rechunk: the core library

# \- Rechunk.Storage.Sqlite and Rechunk.Storage.Redis: the shared fingerprint store

# \- Rechunk.VectorData: a writer for Microsoft.Extensions.VectorData collections

# \- Rechunk.SemanticKernel: registers Rechunk on a kernel builder

# \- Symbol packages and SourceLink for debugging

# 

# Rechunk.Extensions.AI is not in this release. Its wrapper still forwards every input to the inner generator.

