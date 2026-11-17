-- Tracks which numbered migrations have been applied, so the installer runs each one exactly once.
-- The installer creates this table itself before applying migrations; this file keeps a manual run working too.
IF OBJECT_ID('dbo.SchemaVersion') IS NULL
CREATE TABLE dbo.SchemaVersion (
    Version    INT           NOT NULL PRIMARY KEY,
    Name       NVARCHAR(200) NOT NULL,
    AppliedUtc DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);
