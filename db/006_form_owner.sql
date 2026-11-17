-- Who made each form and who owns it. The owner (and admins) can edit the form and see its submissions.
-- Idempotent: safe to re-run. Forms made before this existed have no owner until an admin sets one.
IF COL_LENGTH('dbo.Forms', 'CreatedBy') IS NULL
    ALTER TABLE dbo.Forms ADD CreatedBy NVARCHAR(256) NULL;
GO
IF COL_LENGTH('dbo.Forms', 'OwnerAccount') IS NULL
    ALTER TABLE dbo.Forms ADD OwnerAccount NVARCHAR(256) NULL;
