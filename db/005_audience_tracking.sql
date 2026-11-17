-- Internal vs public forms, public-safe lists, and dispatch tracking on submissions. Idempotent.

-- A form is internal or public from the day it is created. (Converting later is a separate, deliberate step.)
IF COL_LENGTH('dbo.Forms', 'Audience') IS NULL
    ALTER TABLE dbo.Forms ADD Audience NVARCHAR(10) NOT NULL
        CONSTRAINT DF_Forms_Audience DEFAULT 'internal'
        CONSTRAINT CK_Forms_Audience CHECK (Audience IN ('internal', 'public'));

-- Only lists an admin has approved can feed dropdowns on public forms. Default: not approved.
IF COL_LENGTH('dbo.Lookups', 'IsPublicSafe') IS NULL
    ALTER TABLE dbo.Lookups ADD IsPublicSafe BIT NOT NULL CONSTRAINT DF_Lookups_IsPublicSafe DEFAULT 0;

-- Tracking: where each submission is in the dispatch workflow, and who has it.
IF COL_LENGTH('dbo.Submissions', 'Status') IS NULL
    ALTER TABLE dbo.Submissions ADD
        Status           NVARCHAR(20)  NOT NULL
            CONSTRAINT DF_Submissions_Status DEFAULT 'New'
            CONSTRAINT CK_Submissions_Status CHECK (Status IN ('New', 'In progress', 'Done')),
        AssignedTo       NVARCHAR(256) NULL,
        StatusUpdatedUtc DATETIME2     NULL,
        StatusUpdatedBy  NVARCHAR(256) NULL;
