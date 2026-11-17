-- Connectors: external data sources that feed picklists (dbo.Lookups). Idempotent.
-- Secrets are NOT stored here. SecretRef names a key in config.json or an environment variable.
IF OBJECT_ID('dbo.Connectors') IS NULL
CREATE TABLE dbo.Connectors (
    Id             INT IDENTITY PRIMARY KEY,
    Name           NVARCHAR(100)  NOT NULL UNIQUE,
    Kind           NVARCHAR(20)   NOT NULL CHECK (Kind IN ('sql', 'rest', 'csv')),
    Settings       NVARCHAR(MAX)  NOT NULL CHECK (ISJSON(Settings) = 1),   -- kind-specific, no secrets
    SecretRef      NVARCHAR(100)  NULL,
    LookupName     NVARCHAR(100)  NOT NULL,                                -- the list this connector fills
    RefreshMinutes INT            NULL,                                    -- NULL = manual refresh only
    IsEnabled      BIT            NOT NULL DEFAULT 1,
    LastRunUtc     DATETIME2      NULL,
    LastStatus     NVARCHAR(10)   NULL,                                    -- ok | failed
    LastMessage    NVARCHAR(500)  NULL,
    CreatedUtc     DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);
