-- Email notifications: per-form settings plus an outbox the background sender drains.
-- Idempotent: safe to re-run.

IF COL_LENGTH('dbo.Forms', 'NotifyTo') IS NULL
    ALTER TABLE dbo.Forms ADD
        NotifyTo      NVARCHAR(1000) NULL,   -- comma or semicolon separated addresses
        NotifySubject NVARCHAR(300)  NULL;   -- template, e.g. 'New dispatch: {{customer}}'

IF OBJECT_ID('dbo.Notifications') IS NULL
CREATE TABLE dbo.Notifications (
    Id              BIGINT IDENTITY PRIMARY KEY,
    SubmissionId    BIGINT         NOT NULL REFERENCES dbo.Submissions(Id),
    ToAddresses     NVARCHAR(1000) NOT NULL,
    Subject         NVARCHAR(500)  NOT NULL,
    Body            NVARCHAR(MAX)  NOT NULL,
    Status          NVARCHAR(10)   NOT NULL DEFAULT 'pending',   -- pending | sent | failed
    Attempts        INT            NOT NULL DEFAULT 0,
    LastError       NVARCHAR(2000) NULL,
    CreatedUtc      DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME(),
    NextAttemptUtc  DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME(),
    SentUtc         DATETIME2      NULL
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Notifications_Pending')
    CREATE INDEX IX_Notifications_Pending ON dbo.Notifications (Status, NextAttemptUtc);
