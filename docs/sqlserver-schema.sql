-- A T-SQL version of the tables Rechunk.Storage.Sqlite uses. Nothing in the repository runs this yet,
-- it is here as a starting point for a SQL Server backend.
--
-- Claiming a hash has to be one atomic step. A way to do it:
--
--   BEGIN TRANSACTION;
--   SELECT ... FROM rechunk_exact WITH (UPDLOCK, HOLDLOCK) WHERE scope = @scope AND hash = @hash;
--   -- then insert, update or leave the row, as the claim rules say
--   COMMIT;
--
-- HOLDLOCK keeps the key range locked, so two callers claiming a hash that does not exist yet can not both insert it.

CREATE TABLE dbo.rechunk_exact (
    scope        NVARCHAR(200)   NOT NULL,
    hash         BINARY(32)      NOT NULL,
    chunk        NVARCHAR(200)   NOT NULL,   -- the chunk id that owns the hash (the alias target for an alias)
    committed    BIT             NOT NULL,
    holder       NVARCHAR(200)   NOT NULL,   -- empty once committed
    lease_until  BIGINT          NOT NULL,   -- .NET ticks, UTC. Zero once committed
    CONSTRAINT pk_rechunk_exact PRIMARY KEY (scope, hash)
);

CREATE TABLE dbo.rechunk_band_entry (
    scope    NVARCHAR(200)   NOT NULL,
    kind     TINYINT         NOT NULL,       -- 1 SimHash, 2 MinHash
    id       NVARCHAR(200)   NOT NULL,
    payload  VARBINARY(MAX)  NOT NULL,       -- opaque, written and read by the core library
    CONSTRAINT pk_rechunk_band_entry PRIMARY KEY (scope, kind, id)
);

CREATE TABLE dbo.rechunk_band_key (
    scope  NVARCHAR(200)   NOT NULL,
    kind   TINYINT         NOT NULL,
    band   INT             NOT NULL,
    [key]  BIGINT          NOT NULL,
    id     NVARCHAR(200)   NOT NULL,
    CONSTRAINT pk_rechunk_band_key PRIMARY KEY (scope, kind, band, [key], id)
);

CREATE TABLE dbo.rechunk_dead_letter (
    id         NVARCHAR(64)    NOT NULL PRIMARY KEY,
    scope      NVARCHAR(200)   NOT NULL,
    chunk      NVARCHAR(200)   NOT NULL,
    document   NVARCHAR(200)   NULL,
    ordinal    INT             NOT NULL,
    stage      TINYINT         NOT NULL,     -- 0 Dedup, 1 Embed, 2 VectorWrite, 3 Commit
    reason     NVARCHAR(1000)  NOT NULL,
    attempts   INT             NOT NULL,
    failed_at  BIGINT          NOT NULL,     -- .NET ticks, UTC
    body       NVARCHAR(MAX)   NULL          -- the chunk text, only when StoreTextInDeadLetters is on
);

CREATE INDEX ix_rechunk_dead_letter_scope ON dbo.rechunk_dead_letter (scope, failed_at);
