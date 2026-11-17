-- App users (Windows accounts + role) and the audit log. Idempotent: safe to re-run.

IF OBJECT_ID('dbo.AppUsers') IS NULL
CREATE TABLE dbo.AppUsers (
    Id            INT IDENTITY PRIMARY KEY,
    Account       NVARCHAR(256) NOT NULL,                 -- DOMAIN\username, as Windows reports it
    DisplayName   NVARCHAR(200) NULL,
    Role          NVARCHAR(10)  NOT NULL CHECK (Role IN ('Admin', 'User')),
    IsActive      BIT           NOT NULL DEFAULT 1,
    CreatedUtc    DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    LastSignInUtc DATETIME2     NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_AppUsers_Account')
    CREATE UNIQUE INDEX UX_AppUsers_Account ON dbo.AppUsers (Account);

-- Append-only record of who did what. The app only ever INSERTs.
IF OBJECT_ID('dbo.AuditLog') IS NULL
CREATE TABLE dbo.AuditLog (
    Id          BIGINT IDENTITY PRIMARY KEY,
    OccurredUtc DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    Actor       NVARCHAR(256) NULL,        -- Windows account, or 'api-key' for machine callers
    Action      NVARCHAR(60)  NOT NULL,    -- e.g. FormCreated, UserChanged, SubmissionsExported
    EntityType  NVARCHAR(60)  NULL,
    EntityId    NVARCHAR(100) NULL,
    Detail      NVARCHAR(MAX) NULL,        -- JSON: before/after values, counts, reasons
    IpAddress   NVARCHAR(64)  NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AuditLog_Occurred')
    CREATE INDEX IX_AuditLog_Occurred ON dbo.AuditLog (OccurredUtc DESC);

-- In production, make the log tamper-resistant by denying the app's login UPDATE and DELETE on it:
--   DENY UPDATE, DELETE ON dbo.AuditLog TO [<app login>];
