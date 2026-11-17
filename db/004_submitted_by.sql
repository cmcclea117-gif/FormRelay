-- Record who submitted each form (the Windows account of the signed-in user). Idempotent.
IF COL_LENGTH('dbo.Submissions', 'SubmittedBy') IS NULL
    ALTER TABLE dbo.Submissions ADD SubmittedBy NVARCHAR(256) NULL;
