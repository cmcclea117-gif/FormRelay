-- FormRelay schema. Idempotent: safe to re-run.
IF OBJECT_ID('dbo.Forms') IS NULL
CREATE TABLE dbo.Forms (
    Id          INT IDENTITY PRIMARY KEY,
    Slug        NVARCHAR(100)  NOT NULL UNIQUE,
    Name        NVARCHAR(200)  NOT NULL,
    Description NVARCHAR(1000) NULL,
    CreatedUtc  DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);

-- A form is a JSON definition, versioned. Submissions point at the version they were made against.
IF OBJECT_ID('dbo.FormVersions') IS NULL
CREATE TABLE dbo.FormVersions (
    Id         INT IDENTITY PRIMARY KEY,
    FormId     INT           NOT NULL REFERENCES dbo.Forms(Id),
    Version    INT           NOT NULL,
    Definition NVARCHAR(MAX) NOT NULL CHECK (ISJSON(Definition) = 1),
    CreatedUtc DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_FormVersions UNIQUE (FormId, Version)
);

-- Lookup lists feed dropdowns. Source: manual | csv | connector
IF OBJECT_ID('dbo.Lookups') IS NULL
CREATE TABLE dbo.Lookups (
    Id             INT IDENTITY PRIMARY KEY,
    Name           NVARCHAR(100) NOT NULL UNIQUE,
    Source         NVARCHAR(20)  NOT NULL DEFAULT 'manual',
    SyncQuery      NVARCHAR(MAX) NULL,   -- used when Source = 'connector'
    LastSyncedUtc  DATETIME2     NULL
);

IF OBJECT_ID('dbo.LookupItems') IS NULL
CREATE TABLE dbo.LookupItems (
    Id        INT IDENTITY PRIMARY KEY,
    LookupId  INT           NOT NULL REFERENCES dbo.Lookups(Id) ON DELETE CASCADE,
    Value     NVARCHAR(200) NOT NULL,
    Label     NVARCHAR(200) NOT NULL,
    SortOrder INT           NOT NULL DEFAULT 0
);

IF OBJECT_ID('dbo.Submissions') IS NULL
CREATE TABLE dbo.Submissions (
    Id            BIGINT IDENTITY PRIMARY KEY,
    FormVersionId INT           NOT NULL REFERENCES dbo.FormVersions(Id),
    Payload       NVARCHAR(MAX) NOT NULL CHECK (ISJSON(Payload) = 1),
    SubmittedUtc  DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);
