# Roadmap

This is the list of what I am planning to build as part of Rechunk, in order, by version. It has no specific dates. Each version is a set of small iteration, and each has its own pull request with tests, docs and a changelog entry. Plans can change, and when they do this file changes with them.

## The gap

AI systems now keep copies of company and customer data as chunks and vectors. Very few teams can say what their AI holds, where each piece came from, or show that something has really been removed.

- Vectors are treated as a by-product, but they often carry personal data. A deletion request reaches the database, the files and the caches, and the vector store gets forgotten.
- Deleted does not always mean gone. Many vector stores delete softly, and snapshots or copied dev databases are often outside the deletion process.
- Copies spread. A quoted or slightly edited copy of the same text gets a different id, so deleting the original misses it.
- Agents make it worse. They save their own memories from conversations, so personal data gets stored without anyone deciding to store it.

There are good tools to load data into AI memory, and good tools to run deletion requests across business systems. What is missing is the piece in between: something that knows what each chunk is, whose data is in it and where its copies went, and can show it was removed. That is the space Rechunk is built for. Skipping work on unchanged and near duplicate chunks is a useful benefit along the way.

## Principles

- If in doubt, Rechunk embeds twice rather than merge two texts that are different.
- Nothing Rechunk stores or emits lets someone confirm a guessed text without the scope key.
- The audit trail never stores chunk text or the values it finds.
- Every benchmark says what it does not measure.
- Erasure removes what Rechunk stored. Whether that meets a legal obligation is for the user's own legal advice.
- No usage data is sent anywhere. Metrics stay in the user's environment.
- The core stays open source under MIT.

## Shipped

### 0.1.0
Exact, SimHash and MinHash dedup before embedding, revision tracking, a shared store on SQLite or Redis with leases and dead letters, PII scanning with flag, mask and block, an audit trail and erasure with receipts. See CHANGELOG.md.

## Planned

### 0.2.0 Keyed ids
- Chunk ids, fingerprints and band keys become keyed hashes with one key per scope, so the store, the vector keys and the audit log cannot confirm a guessed text.
- Near copy signatures are encrypted at rest.
- Pluggable key providers, with the shredding guarantee stated for each one.
- Opt in, resumable migration for 0.1 stores and vectors.
- A threat model page.

This changes every chunk id, so it is a breaking change with migration steps.

### 0.3.0 Erase by person
- A subject index that links chunks to the people they are about, stored as keyed ids.
- An access report: what we hold about this person, as counts, sources and dates, never text.
- Erase by subject, including near copies.

### 0.4.0 SQL Server and Azure SQL
- Rechunk.Storage.SqlServer for every store: fingerprints, revisions, audit and dead letters.
- Erasure that deletes the vector, its fingerprints and its lineage and writes the receipt in one transaction.
- Lease renewal for long embedding jobs.
- Clear, documented behavior when two workers see near copies at the same time.

### 0.5.0 Proof and quality
- Fault injection and property tests for claims, leases and erasure, with published results.
- An answer quality benchmark, so dedup and masking never quietly hurt retrieval.
- Design notes on the main trade-offs.
- XML docs on the whole public API.

### 0.6.0 More stores and retention
- Sinks for Azure AI Search, pgvector and Qdrant, each with a test that checks a deleted vector is really gone.
- Retention by purpose: every chunk carries a purpose and an expiry, and a sweeper erases expired chunks with receipts.

### 0.7.0 Beyond .NET
- A service mode with HTTP in front of the same core.
- A Python SDK, and adapters for LangChain and LlamaIndex ingestion.
- A small webhook API so privacy request tools can call Rechunk for the AI memory part of a request.

### 1.0.0 Agent memory
- The same write path for memories that agents save, starting with Semantic Kernel and Microsoft Agent Framework, then an MCP server.
- A stable public API and stored formats.
- A security review of the store.

## Running through every version

These are part of every step, not a later phase.

- Responsible AI: PII detection tested across languages, including Indian scripts; benchmarks that show both savings and harm.
- Security: least privilege, no secrets in logs or dead letters, the threat model kept current.
- Analytics: decision metrics in the user's own environment, such as dedup rate, PII findings by type, erase duration and receipts issued, with OpenTelemetry once the service mode lands.
- Evidence: reproducible benchmarks in the repo, and a test for every claim.

## Not planned

A vector database, a chunker, a consent or request portal, or a general PII vault. Rechunk works with those rather than replacing them.

## Versions

Rechunk follows semantic versioning. Below 1.0, a minor release can change stored data or ids, and the changelog lists the migration steps. Releases are tagged on main and published to NuGet.
