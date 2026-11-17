-- Optional sample forms and lists, applied only with: FormRelay-Setup.ps1 install -WithSamples
-- Idempotent: creates only what is missing and never overwrites edits. Notification mailboxes are left blank
-- on purpose; set one per form in the app (Email box in the form builder).

------------------------------------------------------------------ lists
DECLARE @lists TABLE (Name NVARCHAR(100), IsPublicSafe BIT);
INSERT @lists VALUES
    (N'visit-purposes', 1),
    (N'priorities',     0),
    (N'departments',    0);

INSERT dbo.Lookups (Name, Source, IsPublicSafe)
SELECT l.Name, 'manual', l.IsPublicSafe FROM @lists l
WHERE NOT EXISTS (SELECT 1 FROM dbo.Lookups x WHERE x.Name = l.Name);

DECLARE @items TABLE (LookupName NVARCHAR(100), Value NVARCHAR(200), SortOrder INT);
INSERT @items VALUES
    (N'visit-purposes', N'Meeting', 1), (N'visit-purposes', N'Delivery', 2),
    (N'visit-purposes', N'Interview', 3), (N'visit-purposes', N'Other', 4),
    (N'priorities', N'Low', 1), (N'priorities', N'Normal', 2), (N'priorities', N'High', 3), (N'priorities', N'Urgent', 4),
    (N'departments', N'Operations', 1), (N'departments', N'Finance', 2), (N'departments', N'IT', 3), (N'departments', N'Facilities', 4);

INSERT dbo.LookupItems (LookupId, Value, Label, SortOrder)
SELECT l.Id, i.Value, i.Value, i.SortOrder
FROM @items i
JOIN dbo.Lookups l ON l.Name = i.LookupName
WHERE NOT EXISTS (SELECT 1 FROM dbo.LookupItems x WHERE x.LookupId = l.Id);   -- only fill lists that are empty

------------------------------------------------------------------ Visitor Sign-in (public)
IF NOT EXISTS (SELECT 1 FROM dbo.Forms WHERE Slug = 'visitor-sign-in')
BEGIN
    INSERT dbo.Forms (Slug, Name, Description, Audience, NotifySubject)
    VALUES ('visitor-sign-in', N'Visitor Sign-in',
            N'Guests sign in on arrival. Public: no Windows sign-in needed.',
            'public', N'Visitor: {{visitor_name}} to see {{host}}');
    DECLARE @v INT = SCOPE_IDENTITY();
    INSERT dbo.FormVersions (FormId, Version, Definition) VALUES (@v, 1, N'{
  "listColumns": ["visitor_name", "company", "host", "purpose"],
  "fields": [
    { "key": "visitor_name", "type": "text",     "label": "Your name",       "required": true },
    { "key": "company",      "type": "text",     "label": "Company",         "required": false },
    { "key": "host",         "type": "text",     "label": "Who are you here to see?", "required": true },
    { "key": "purpose",      "type": "dropdown", "label": "Reason for visit", "required": true, "lookup": "visit-purposes" },
    { "key": "purpose_other","type": "text",     "label": "Please explain",  "required": false,
      "showIf": { "field": "purpose", "equals": "Other" } }
  ]
}');
END

------------------------------------------------------------------ Service Request (internal)
IF NOT EXISTS (SELECT 1 FROM dbo.Forms WHERE Slug = 'service-request')
BEGIN
    INSERT dbo.Forms (Slug, Name, Description, Audience, NotifySubject)
    VALUES ('service-request', N'Service Request',
            N'Staff ask a department for help. Internal: Windows sign-in required.',
            'internal', N'[{{priority}}] {{summary}}');
    DECLARE @s INT = SCOPE_IDENTITY();
    INSERT dbo.FormVersions (FormId, Version, Definition) VALUES (@s, 1, N'{
  "listColumns": ["summary", "department", "priority"],
  "fields": [
    { "key": "department", "type": "dropdown", "label": "Department", "required": true, "lookup": "departments" },
    { "key": "priority",   "type": "dropdown", "label": "Priority",   "required": true, "lookup": "priorities" },
    { "key": "summary",    "type": "text",     "label": "Short summary", "required": true, "maxLength": 120 },
    { "key": "details",    "type": "textarea", "label": "Details",    "required": false, "help": "What do you need, and by when?" },
    { "key": "needed_by",  "type": "date",     "label": "Needed by",  "required": false }
  ]
}');
END

------------------------------------------------------------------ Equipment Check-out (internal)
IF NOT EXISTS (SELECT 1 FROM dbo.Forms WHERE Slug = 'equipment-checkout')
BEGIN
    INSERT dbo.Forms (Slug, Name, Description, Audience, NotifySubject)
    VALUES ('equipment-checkout', N'Equipment Check-out',
            N'Borrow shared equipment. Internal: Windows sign-in required.',
            'internal', N'Check-out: {{item}}');
    DECLARE @e INT = SCOPE_IDENTITY();
    INSERT dbo.FormVersions (FormId, Version, Definition) VALUES (@e, 1, N'{
  "listColumns": ["item", "quantity", "return_date"],
  "fields": [
    { "key": "item",        "type": "text",     "label": "Item",          "required": true },
    { "key": "quantity",    "type": "number",   "label": "Quantity",      "required": true, "min": 1, "max": 100 },
    { "key": "return_date", "type": "date",     "label": "Return date",   "required": true },
    { "key": "accessories", "type": "checkbox", "label": "Includes accessories (cables, case, charger)", "required": false },
    { "key": "notes",       "type": "textarea", "label": "Notes",         "required": false }
  ]
}');
END
