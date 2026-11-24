-- Fingerprint and revision tables for Rechunk.Storage.SqlServer.
-- {{SCHEMA}} is replaced with the schema name. The default is rechunk.
-- The script is safe to run more than once. It does not create foreign keys.
-- Long ids are stored in full and indexed by a SHA-256 of the text, because a SQL index key cannot hold them.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'{{SCHEMA}}')
    EXEC(N'CREATE SCHEMA [{{SCHEMA}}]');

IF OBJECT_ID(N'{{SCHEMA}}.exact', N'U') IS NULL
BEGIN
    CREATE TABLE [{{SCHEMA}}].[exact] (
        scope         NVARCHAR(400)  NOT NULL,
        hash          VARBINARY(32)  NOT NULL,
        chunk         NVARCHAR(MAX)  NOT NULL,
        chunk_key     AS CONVERT(VARBINARY(32), HASHBYTES('SHA2_256', chunk)) PERSISTED NOT NULL,
        committed     BIT            NOT NULL,
        holder        NVARCHAR(400)  NOT NULL,
        lease_until   BIGINT         NOT NULL,
        hash_version  INT            NOT NULL,
        key_version   INT            NOT NULL,
        text_id       NVARCHAR(MAX)  NOT NULL,
        text_key      AS CONVERT(VARBINARY(32), HASHBYTES('SHA2_256', text_id)) PERSISTED NOT NULL,
        CONSTRAINT pk_exact PRIMARY KEY (scope, hash)
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_exact_owner' AND object_id = OBJECT_ID(N'{{SCHEMA}}.exact'))
    CREATE INDEX ix_exact_owner ON [{{SCHEMA}}].[exact] (scope, chunk_key) INCLUDE (hash) WHERE committed = 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_exact_text' AND object_id = OBJECT_ID(N'{{SCHEMA}}.exact'))
    CREATE INDEX ix_exact_text ON [{{SCHEMA}}].[exact] (scope, text_key) WHERE text_id <> N'';

IF OBJECT_ID(N'{{SCHEMA}}.scope_version', N'U') IS NULL
BEGIN
    CREATE TABLE [{{SCHEMA}}].[scope_version] (
        scope         NVARCHAR(400) NOT NULL,
        hash_version  INT           NOT NULL,
        key_version   INT           NOT NULL,
        CONSTRAINT pk_scope_version PRIMARY KEY (scope)
    );
END

IF OBJECT_ID(N'{{SCHEMA}}.band_entry', N'U') IS NULL
BEGIN
    CREATE TABLE [{{SCHEMA}}].[band_entry] (
        scope    NVARCHAR(400)  NOT NULL,
        kind     TINYINT        NOT NULL,
        id       NVARCHAR(MAX)  NOT NULL,
        id_key   AS CONVERT(VARBINARY(32), HASHBYTES('SHA2_256', id)) PERSISTED NOT NULL,
        payload  VARBINARY(MAX) NOT NULL,
        CONSTRAINT pk_band_entry PRIMARY KEY (scope, kind, id_key)
    );
END

IF OBJECT_ID(N'{{SCHEMA}}.band_key', N'U') IS NULL
BEGIN
    CREATE TABLE [{{SCHEMA}}].[band_key] (
        scope   NVARCHAR(400) NOT NULL,
        kind    TINYINT       NOT NULL,
        band    INT           NOT NULL,
        [key]   BIGINT        NOT NULL,
        id_key  VARBINARY(32) NOT NULL,
        CONSTRAINT pk_band_key PRIMARY KEY (scope, kind, band, [key], id_key)
    );
END

IF OBJECT_ID(N'{{SCHEMA}}.doc', N'U') IS NULL
BEGIN
    CREATE TABLE [{{SCHEMA}}].[doc] (
        scope          NVARCHAR(400) NOT NULL,
        document       NVARCHAR(MAX) NOT NULL,
        document_key   AS CONVERT(VARBINARY(32), HASHBYTES('SHA2_256', document)) PERSISTED NOT NULL,
        revision       INT           NOT NULL,
        updated_at     BIGINT        NOT NULL,
        source         NVARCHAR(400) NULL,
        CONSTRAINT pk_doc PRIMARY KEY (scope, document_key)
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_doc_source' AND object_id = OBJECT_ID(N'{{SCHEMA}}.doc'))
    CREATE INDEX ix_doc_source ON [{{SCHEMA}}].[doc] (scope, source, document_key);

IF OBJECT_ID(N'{{SCHEMA}}.doc_chunk', N'U') IS NULL
BEGIN
    CREATE TABLE [{{SCHEMA}}].[doc_chunk] (
        scope             NVARCHAR(400) NOT NULL,
        document          NVARCHAR(MAX) NOT NULL,
        document_key      AS CONVERT(VARBINARY(32), HASHBYTES('SHA2_256', document)) PERSISTED NOT NULL,
        seq               INT           NOT NULL,
        ordinal           INT           NOT NULL,
        id                NVARCHAR(MAX) NOT NULL,
        id_key            AS CONVERT(VARBINARY(32), HASHBYTES('SHA2_256', id)) PERSISTED NOT NULL,
        target            NVARCHAR(MAX) NOT NULL,
        target_key        AS CONVERT(VARBINARY(32), HASHBYTES('SHA2_256', target)) PERSISTED NOT NULL,
        language          NVARCHAR(64)  NULL,
        first_revision    INT           NOT NULL,
        previous_ordinal  INT           NULL,
        replaces          NVARCHAR(MAX) NULL,
        CONSTRAINT pk_doc_chunk PRIMARY KEY (scope, document_key, seq)
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_doc_chunk_id' AND object_id = OBJECT_ID(N'{{SCHEMA}}.doc_chunk'))
    CREATE INDEX ix_doc_chunk_id ON [{{SCHEMA}}].[doc_chunk] (scope, id_key);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_doc_chunk_target' AND object_id = OBJECT_ID(N'{{SCHEMA}}.doc_chunk'))
    CREATE INDEX ix_doc_chunk_target ON [{{SCHEMA}}].[doc_chunk] (scope, target_key);

IF OBJECT_ID(N'{{SCHEMA}}.ref', N'U') IS NULL
BEGIN
    CREATE TABLE [{{SCHEMA}}].[ref] (
        scope        NVARCHAR(400) NOT NULL,
        chunk        NVARCHAR(MAX) NOT NULL,
        chunk_key    AS CONVERT(VARBINARY(32), HASHBYTES('SHA2_256', chunk)) PERSISTED NOT NULL,
        count        INT           NOT NULL,
        state        TINYINT       NOT NULL,
        holder       NVARCHAR(400) NOT NULL,
        lease_until  BIGINT        NOT NULL,
        CONSTRAINT pk_ref PRIMARY KEY (scope, chunk_key)
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_ref_pending' AND object_id = OBJECT_ID(N'{{SCHEMA}}.ref'))
    CREATE INDEX ix_ref_pending ON [{{SCHEMA}}].[ref] (scope, chunk_key) WHERE count = 0;

IF OBJECT_ID(N'{{SCHEMA}}.alias', N'U') IS NULL
BEGIN
    CREATE TABLE [{{SCHEMA}}].[alias] (
        scope       NVARCHAR(400) NOT NULL,
        target      NVARCHAR(MAX) NOT NULL,
        target_key  AS CONVERT(VARBINARY(32), HASHBYTES('SHA2_256', target)) PERSISTED NOT NULL,
        alias       NVARCHAR(MAX) NOT NULL,
        alias_key   AS CONVERT(VARBINARY(32), HASHBYTES('SHA2_256', alias)) PERSISTED NOT NULL,
        CONSTRAINT pk_alias PRIMARY KEY (scope, target_key, alias_key)
    );
END
