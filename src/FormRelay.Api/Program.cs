using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.SqlClient;
using FormRelay.Api;
using FormRelay.Api.Auth;
using FormRelay.Api.Connectors;
using FormRelay.Api.Email;

var builder = WebApplication.CreateBuilder(args);

// Runs as a Windows service when installed as one; a normal console app otherwise (dotnet run).
builder.Host.UseWindowsService(o => o.ServiceName = "FormRelay");

// Machine settings live outside the app folder, so updating the app never touches them. The installer writes this file.
var dataDir = Environment.GetEnvironmentVariable("FORMRELAY_DATA")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FormRelay");
builder.Configuration.AddJsonFile(Path.Combine(dataDir, "config.json"), optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();
var connStr = builder.Configuration.GetConnectionString("Default")!;
var readKey = builder.Configuration["Api:ReadKey"];
const int AppViewDays = 90;   // the app shows the last 90 days; the database keeps everything

SqlConnection Db() => new(connStr);

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<UserDirectory>();
builder.Services.AddTransient<IClaimsTransformation, AppUserClaims>();
builder.Services.AddSingleton<SelfCheck>();
builder.Services.AddSingleton<ConnectorService>();
builder.Services.AddHostedService<ConnectorScheduler>();
builder.Services.AddHostedService<OutboxSender>();

// Email goes out through SMTP (smtp4dev while developing) or Microsoft Graph. Pick with Email:Provider.
if (string.Equals(builder.Configuration["Email:Provider"], "Graph", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IEmailSender, GraphEmailSender>();
else
    builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();

// Windows (Kerberos/NTLM) sign-in. Roles come from dbo.AppUsers, not from Windows.
builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("AnyUser", p => p.RequireRole("Admin", "User"));
    o.AddPolicy("Admin", p => p.RequireRole("Admin"));
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

// Anonymous public submissions are limited per IP address.
var publicLimit = builder.Configuration.GetValue("Public:SubmitsPerTenMinutes", 20);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("public-submit", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = publicLimit, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));
});

var app = builder.Build();
var audit = app.Services.GetRequiredService<AuditLog>();
var userDir = app.Services.GetRequiredService<UserDirectory>();

app.UseDefaultFiles();
app.UseStaticFiles();   // serves the built React app from wwwroot
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// ---------------------------------------------------------------- helpers

static IEnumerable<JsonObject> FieldsOf(JsonObject def) =>
    (def["fields"] as JsonArray)?.OfType<JsonObject>() ?? [];

// Admins can manage every form. Everyone else can manage only the forms they own.
// Returns Exists=false for an unknown slug.
async Task<(bool Exists, bool Allowed)> ManageAccessAsync(HttpContext ctx, string slug)
{
    await using var db = Db();
    var row = await db.QuerySingleOrDefaultAsync("SELECT OwnerAccount FROM dbo.Forms WHERE Slug = @slug", new { slug });
    if (row is null) return (false, false);
    var owner = (string?)row.OwnerAccount;
    return (true, ctx.User.IsInRole("Admin")
        || (owner is not null && string.Equals(owner, ctx.User.Identity?.Name, StringComparison.OrdinalIgnoreCase)));
}

// Loads a form's latest definition and fills each dropdown's "options" from its list.
// On public forms, only lists an admin approved as public-safe are filled; anything else comes back empty.
async Task<FormInfo?> LoadFormAsync(string slug, bool publicOnly)
{
    await using var db = Db();
    var row = await db.QuerySingleOrDefaultAsync("""
        SELECT TOP 1 f.Slug, f.Name, f.Description, f.Audience, f.NotifyTo, f.NotifySubject,
               v.Id AS VersionId, v.Version, v.Definition
        FROM dbo.Forms f JOIN dbo.FormVersions v ON v.FormId = f.Id
        WHERE f.Slug = @slug ORDER BY v.Version DESC
        """, new { slug });
    if (row is null) return null;

    var audience = (string)row.Audience;
    if (publicOnly && audience != "public") return null;

    var raw = (string)row.Definition;
    var def = JsonNode.Parse(raw)!.AsObject();
    foreach (var f in FieldsOf(def))
    {
        if (f["lookup"]?.GetValue<string>() is not { } lookup) continue;
        var items = await db.QueryAsync("""
            SELECT i.Value AS Value, i.Label AS Label
            FROM dbo.LookupItems i JOIN dbo.Lookups l ON l.Id = i.LookupId
            WHERE l.Name = @lookup AND (@publicOnly = 0 OR l.IsPublicSafe = 1)
            ORDER BY i.SortOrder
            """, new { lookup, publicOnly = publicOnly ? 1 : 0 });
        f["options"] = JsonSerializer.SerializeToNode(
            items.Select(i => new { value = (string)i.Value, label = (string)i.Label }).ToList());
    }
    return new FormInfo((int)row.VersionId, (int)row.Version, (string)row.Slug, (string)row.Name,
        (string?)row.Description, audience, (string?)row.NotifyTo, (string?)row.NotifySubject, raw, def);
}

// Checks a form definition before it is saved as a new version. Returns the first problem, or null if it is fine.
// Dropdown "options" are filled in from lists when a form loads, so they are removed here and never stored.
static string? CheckDefinition(JsonObject def, bool isPublic, HashSet<string> allLookups, HashSet<string> publicLookups)
{
    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    if (def["fields"] is not JsonArray fields) return "The definition needs a list of fields";
    if (fields.Count == 0) return "Add at least one field";
    if (fields.Count > 100) return "Too many fields (the limit is 100)";

    string[] basic = ["text", "textarea", "email", "number", "date", "dropdown", "checkbox"];
    var objects = fields.OfType<JsonObject>().ToList();
    if (objects.Count != fields.Count) return "Every field must be an object";

    var keys = new HashSet<string>();
    foreach (var f in objects)
    {
        var key = Str(f["key"]);
        if (key is null || !System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-z][a-z0-9_]{0,59}$"))
            return $"Field key '{key}' must start with a letter and use only lowercase letters, numbers and underscores";
        if (key == "website" || key.EndsWith("_details")) return $"'{key}' is reserved; choose another key";
        if (!keys.Add(key)) return $"Two fields use the key '{key}'";

        var label = Str(f["label"])?.Trim();
        if (string.IsNullOrEmpty(label) || label.Length > 200) return $"Field '{key}' needs a label (200 characters at most)";

        var type = Str(f["type"]);
        if (type is null || !(basic.Contains(type)))
            return $"Field '{key}' has an unknown type";
        if (isPublic && !basic.Contains(type)) return $"'{label}': that field type is not allowed on public forms";

        if (type == "dropdown")
        {
            var lookup = Str(f["lookup"]);
            if (lookup is null || !allLookups.Contains(lookup)) return $"'{label}' needs a list to choose from";
            if (isPublic && !publicLookups.Contains(lookup)) return $"'{label}': that list is not approved for public forms";
        }
        if (f["maxLength"] is { } ml && !(ml is JsonValue mv && mv.TryGetValue<int>(out var n) && n is >= 1 and <= 4000))
            return $"'{label}': the length limit must be between 1 and 4000";
        if (type == "number")
        {
            decimal? lo = null, hi = null;
            foreach (var bound in new[] { "min", "max" })
            {
                if (f[bound] is null) continue;
                if (!(f[bound] is JsonValue bv && bv.TryGetValue<decimal>(out var b))) return $"'{label}': {bound} must be a number";
                if (bound == "min") lo = b; else hi = b;
            }
            if (lo > hi) return $"'{label}': the minimum is higher than the maximum";
        }
        f.Remove("options");
    }

    // "Show only when" rules: they may look only at an earlier field, so there are no loops to untangle.
    for (var i = 0; i < objects.Count; i++)
    {
        var f = objects[i];
        if (f["showIf"] is null) continue;
        var label = Str(f["label"]);
        if (f["showIf"] is not JsonObject rule || Str(rule["field"]) is not { } target || Str(rule["equals"]) is not { Length: <= 200 })
            return $"'{label}': the show-only-when rule is incomplete";
        if (!objects.Take(i).Any(o => Str(o["key"]) == target))
            return $"'{label}': it can only depend on a field above it";
    }

    if (def["listColumns"] is JsonArray cols && cols.Any(c => Str(c) is not { } k || !keys.Contains(k)))
        return "The submissions table lists a field that does not exist";
    return null;
}

static object Shape(FormInfo f) => new { f.Slug, f.Name, f.Description, f.Audience, f.Version, Definition = f.Definition };

// Checks a submission against the form's definition and returns only the fields the form defines.
static (Dictionary<string, string> Errors, JsonObject Clean) Validate(JsonObject def, JsonElement payload)
{
    var errors = new Dictionary<string, string>();
    var clean = new JsonObject();
    if (payload.ValueKind != JsonValueKind.Object)
    {
        errors["_"] = "Expected a JSON object";
        return (errors, clean);
    }

    foreach (var f in FieldsOf(def))
    {
        var key = f["key"]?.GetValue<string>();
        if (key is null) continue;
        var label = f["label"]?.GetValue<string>() ?? key;
        var type = f["type"]?.GetValue<string>() ?? "text";
        var required = f["required"]?.GetValue<bool>() ?? false;

        // A field hidden by its "show only when" rule isn't asked, so it can't be required or invalid.
        if (f["showIf"] is JsonObject rule && rule["field"]?.GetValue<string>() is { } target
            && (clean[target]?.ToString() ?? "") != rule["equals"]?.GetValue<string>())
        {
            clean[key] = type switch { "checkbox" => false, "number" => null, _ => "" };
            continue;
        }

        payload.TryGetProperty(key, out var v);
        var text = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.True => "true",
            _ => ""
        };
        text = text.Trim();

        switch (type)
        {
            case "checkbox":
                var on = v.ValueKind == JsonValueKind.True;
                if (required && !on) errors[key] = $"{label} is required";
                clean[key] = on;
                break;

            case "number":
                if (text == "")
                {
                    if (required) errors[key] = $"{label} is required";
                    clean[key] = null;
                }
                else if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                    errors[key] = $"{label} must be a number";
                else if (f["min"] is JsonValue lo && lo.TryGetValue<decimal>(out var min) && num < min)
                    errors[key] = $"{label} must be at least {min}";
                else if (f["max"] is JsonValue hi && hi.TryGetValue<decimal>(out var maxN) && num > maxN)
                    errors[key] = $"{label} must be at most {maxN}";
                else clean[key] = num;
                break;

            case "email":
                if (text == "")
                {
                    if (required) errors[key] = $"{label} is required";
                    clean[key] = "";
                }
                else if (text.Length > 254 || !System.Text.RegularExpressions.Regex.IsMatch(text, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
                    errors[key] = $"{label} must be an email address";
                else clean[key] = text;
                break;

            case "date":
                if (text == "")
                {
                    if (required) errors[key] = $"{label} is required";
                    clean[key] = "";
                }
                else if (!DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    errors[key] = $"{label} must be a date";
                else clean[key] = text;
                break;

            case "dropdown":
                var allowed = (f["options"] as JsonArray)?.OfType<JsonObject>()
                    .Select(o => o["value"]?.GetValue<string>()).ToList() ?? [];
                if (text == "")
                {
                    if (required) errors[key] = $"{label} is required";
                    clean[key] = "";
                }
                else if (!allowed.Contains(text)) errors[key] = $"{label} has an invalid choice";
                else clean[key] = text;
                break;

            default:   // text, textarea
                var max = f["maxLength"]?.GetValue<int>() ?? (type == "textarea" ? 4000 : 300);
                if (text == "" && required) errors[key] = $"{label} is required";
                else if (text.Length > max) errors[key] = $"{label} is too long (max {max} characters)";
                else clean[key] = text;
                break;
        }
    }
    return (errors, clean);
}

// Shared by the signed-in submit and the anonymous public submit.
async Task<IResult> SubmitAsync(HttpContext ctx, string slug, JsonElement payload, bool publicOnly)
{
    var actor = ctx.User.Identity?.IsAuthenticated == true ? ctx.User.Identity.Name : null;
    var ip = ctx.Connection.RemoteIpAddress?.ToString();

    if (payload.GetRawText().Length > 32_000) return Results.BadRequest(new { error = "Submission is too large" });

    // Honeypot: the page has a hidden "website" box that only a bot fills in. Pretend it worked; store nothing.
    if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("website", out var trap)
        && trap.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(trap.GetString()))
    {
        await audit.WriteAsync(actor ?? "public", "HoneypotTripped", "Form", slug, null, ip);
        return Results.Ok(new { id = 0 });
    }

    var form = await LoadFormAsync(slug, publicOnly);
    if (form is null) return Results.NotFound();

    var (errors, clean) = Validate(form.Definition, payload);
    if (errors.Count > 0) return Results.BadRequest(new { errors });

    await using var db = Db();
    await db.OpenAsync();
    await using var tx = await db.BeginTransactionAsync();
    var cleanJson = clean.ToJsonString();
    var id = await db.ExecuteScalarAsync<long>(
        "INSERT dbo.Submissions (FormVersionId, Payload, SubmittedBy) OUTPUT INSERTED.Id VALUES (@versionId, @p, @by)",
        new { versionId = form.VersionId, p = cleanJson, by = actor }, tx);

    // Queue an email in the same transaction; the background sender delivers it, so mail trouble never blocks a submit.
    if (!string.IsNullOrWhiteSpace(form.NotifyTo))
    {
        var (subject, body) = NotificationTemplate.Build(
            form.Name, form.NotifySubject, form.DefinitionRaw, JsonDocument.Parse(cleanJson).RootElement, id,
            actor ?? "Public web form");
        await db.ExecuteAsync(
            "INSERT dbo.Notifications (SubmissionId, ToAddresses, Subject, Body) VALUES (@id, @to, @subject, @body)",
            new { id, to = form.NotifyTo, subject, body }, tx);
    }
    await tx.CommitAsync();
    await audit.WriteAsync(actor ?? "public", "SubmissionCreated", "Submission", id.ToString(), new { form = slug, publicForm = publicOnly }, ip);
    return Results.Created($"/api/forms/{slug}/submissions/{id}", new { id });
}

// ---------------------------------------------------------------- open endpoints

app.MapGet("/api/health", async () =>
{
    await using var db = Db();
    return Results.Ok(new { status = "ok", db = await db.ExecuteScalarAsync<string>("SELECT DB_NAME()"), version = SelfCheck.Version });
}).AllowAnonymous();

// Public forms: no sign-in. Only forms created as "public" are reachable here, and only public-safe lists feed them.
app.MapGet("/api/public/forms/{slug}", async (string slug) =>
{
    var form = await LoadFormAsync(slug, publicOnly: true);
    return form is null ? Results.NotFound() : Results.Ok(Shape(form));
}).AllowAnonymous();

app.MapPost("/api/public/forms/{slug}/submissions",
    (HttpContext ctx, string slug, JsonElement payload) => SubmitAsync(ctx, slug, payload, publicOnly: true))
    .AllowAnonymous()
    .RequireRateLimiting("public-submit");

// Any signed-in Windows user can ask who they are; hasAccess tells the UI whether to show the app or "request access".
app.MapGet("/api/me", (HttpContext ctx) =>
{
    var u = ctx.User;
    return Results.Ok(new
    {
        account = u.Identity?.Name,
        displayName = u.FindFirst("fr:name")?.Value,
        role = u.FindFirst(ClaimTypes.Role)?.Value,
        hasAccess = u.IsInRole("Admin") || u.IsInRole("User")
    });
});

// Everything else under /api needs a listed, active user.
var api = app.MapGroup("/api").RequireAuthorization("AnyUser");

// Read-only self-check for administrators: database, schema, outbox, email settings and certificate expiry.
api.MapGet("/diagnostics", async (SelfCheck d, CancellationToken ct) => Results.Ok(await d.RunAsync(ct))).RequireAuthorization("Admin");

// ---------------------------------------------------------------- forms

api.MapGet("/forms", async (HttpContext ctx) =>
{
    await using var db = Db();
    var rows = await db.QueryAsync("""
        SELECT f.Slug, f.Name, f.Description, f.Audience, f.CreatedUtc, f.OwnerAccount,
               (SELECT u.DisplayName FROM dbo.AppUsers u WHERE u.Account = f.OwnerAccount) AS OwnerName,
               (SELECT MAX(Version) FROM dbo.FormVersions v WHERE v.FormId = f.Id) AS LatestVersion,
               (SELECT COUNT(*) FROM dbo.Submissions s JOIN dbo.FormVersions v ON v.Id = s.FormVersionId
                 WHERE v.FormId = f.Id) AS TotalSubmissions,
               (SELECT COUNT(*) FROM dbo.Submissions s JOIN dbo.FormVersions v ON v.Id = s.FormVersionId
                 WHERE v.FormId = f.Id AND s.Status = 'New') AS NewSubmissions,
               (SELECT MAX(s.SubmittedUtc) FROM dbo.Submissions s JOIN dbo.FormVersions v ON v.Id = s.FormVersionId
                 WHERE v.FormId = f.Id) AS LastSubmittedUtc
        FROM dbo.Forms f ORDER BY f.Name
        """);
    // Submission counts are for admins and for the form's owner; nobody else sees anything about submissions.
    var me = ctx.User.Identity?.Name;
    var admin = ctx.User.IsInRole("Admin");
    return rows.Select(r =>
    {
        var mine = admin || (r.OwnerAccount is string o && string.Equals(o, me, StringComparison.OrdinalIgnoreCase));
        return mine
            ? (object)new { r.Slug, r.Name, r.Description, r.Audience, r.CreatedUtc, r.LatestVersion, r.OwnerAccount, r.OwnerName, canManage = true, r.TotalSubmissions, r.NewSubmissions, r.LastSubmittedUtc }
            : new { r.Slug, r.Name, r.Description, r.Audience, r.CreatedUtc, r.LatestVersion, r.OwnerAccount, r.OwnerName, canManage = false };
    });
});

api.MapPost("/forms", async (HttpContext ctx, NewForm body) =>
{
    var audience = body.Audience ?? "internal";
    if (audience is not ("internal" or "public"))
        return Results.BadRequest(new { error = "Audience must be internal or public" });
    if (audience == "public" && !ctx.User.IsInRole("Admin"))
        return Results.Json(new { error = "Only an administrator can create a public form. Create it as internal and ask an admin to make it public." }, statusCode: 403);
    var creator = ctx.User.Identity?.Name;
    if (!System.Text.RegularExpressions.Regex.IsMatch(body.Slug ?? "", "^[a-z0-9]+(-[a-z0-9]+)*$") || body.Slug!.Length > 100)
        return Results.BadRequest(new { error = "The web address can use lowercase letters, numbers and single dashes" });
    if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 200)
        return Results.BadRequest(new { error = "Give the form a name (200 characters at most)" });
    if (body.Description is { Length: > 1000 })
        return Results.BadRequest(new { error = "The description is too long (1000 characters at most)" });
    await using var db = Db();
    try
    {
        await db.ExecuteAsync(
            "INSERT dbo.Forms (Slug, Name, Description, Audience, CreatedBy, OwnerAccount) VALUES (@Slug, @Name, @Description, @audience, @creator, @creator)",
            new { body.Slug, body.Name, body.Description, audience, creator });
    }
    catch (SqlException e) when (e.Number == 2627) { return Results.Conflict(new { error = "Slug already exists" }); }
    await audit.WriteAsync(ctx, "FormCreated", "Form", body.Slug, new { body.Name, audience });
    return Results.Created($"/api/forms/{body.Slug}", body);
});   // anyone with access can start a form; they become its owner

api.MapGet("/forms/{slug}", async (HttpContext ctx, string slug) =>
{
    var form = await LoadFormAsync(slug, publicOnly: false);
    if (form is null) return Results.NotFound();
    var (_, canManage) = await ManageAccessAsync(ctx, slug);
    await using var db = Db();
    var owner = await db.QuerySingleOrDefaultAsync(
        "SELECT f.OwnerAccount, (SELECT u.DisplayName FROM dbo.AppUsers u WHERE u.Account = f.OwnerAccount) AS OwnerName FROM dbo.Forms f WHERE f.Slug = @slug", new { slug });
    // The mailbox settings are for people who can manage the form; the public endpoint never returns them.
    return Results.Ok(new
    {
        form.Slug, form.Name, form.Description, form.Audience, form.Version, Definition = form.Definition,
        NotifyTo = canManage ? form.NotifyTo : null, NotifySubject = canManage ? form.NotifySubject : null,
        canManage, ownerAccount = (string?)owner?.OwnerAccount, ownerName = (string?)owner?.OwnerName
    });
});

// Saving a definition always creates a new version; old submissions keep pointing at theirs.
api.MapPut("/forms/{slug}/definition", async (HttpContext ctx, string slug, JsonElement definition) =>
{
    var (exists, allowed) = await ManageAccessAsync(ctx, slug);
    if (!exists) return Results.NotFound();
    if (!allowed) return Results.Forbid();
    await using var db = Db();
    var form = await db.QuerySingleOrDefaultAsync("SELECT Id, Audience FROM dbo.Forms WHERE Slug = @slug", new { slug });
    if (form is null) return Results.NotFound();
    int formId = form.Id;

    JsonObject? def;
    try { def = JsonNode.Parse(definition.GetRawText()) as JsonObject; }
    catch (JsonException) { def = null; }
    if (def is null) return Results.BadRequest(new { error = "The definition must be a JSON object" });

    var lookupRows = (await db.QueryAsync("SELECT Name, IsPublicSafe FROM dbo.Lookups")).ToList();
    var problem = CheckDefinition(def, (string)form.Audience == "public",
        lookupRows.Select(r => (string)r.Name).ToHashSet(),
        lookupRows.Where(r => (bool)r.IsPublicSafe).Select(r => (string)r.Name).ToHashSet());
    if (problem is not null) return Results.BadRequest(new { error = problem });

    var version = await db.ExecuteScalarAsync<int>("""
        DECLARE @v INT = ISNULL((SELECT MAX(Version) FROM dbo.FormVersions WHERE FormId = @formId), 0) + 1;
        INSERT dbo.FormVersions (FormId, Version, Definition) VALUES (@formId, @v, @def);
        SELECT @v;
        """, new { formId, def = def.ToJsonString() });
    await audit.WriteAsync(ctx, "FormVersionSaved", "Form", slug, new { version });
    return Results.Ok(new { version });
});

// Changing who a form is for. Going public re-checks the current fields and lists, because anyone on the internet can open it.
api.MapPut("/forms/{slug}/audience", async (HttpContext ctx, string slug, AudienceBody body) =>
{
    if (body.Audience is not ("internal" or "public")) return Results.BadRequest(new { error = "Audience must be internal or public" });
    await using var db = Db();
    var form = await db.QuerySingleOrDefaultAsync("SELECT Id, Audience FROM dbo.Forms WHERE Slug = @slug", new { slug });
    if (form is null) return Results.NotFound();
    var before = (string)form.Audience;
    if (before == body.Audience) return Results.Ok(new { slug, audience = before });

    if (body.Audience == "public")
    {
        var current = await LoadFormAsync(slug, publicOnly: false);
        var lookupRows = (await db.QueryAsync("SELECT Name, IsPublicSafe FROM dbo.Lookups")).ToList();
        var def = JsonNode.Parse(current!.DefinitionRaw)!.AsObject();
        var problem = CheckDefinition(def, true,
            lookupRows.Select(r => (string)r.Name).ToHashSet(),
            lookupRows.Where(r => (bool)r.IsPublicSafe).Select(r => (string)r.Name).ToHashSet());
        if (problem is not null)
            return Results.BadRequest(new { error = "This form can't be made public yet. " + problem + ". Fix it in the builder first." });
    }

    await db.ExecuteAsync("UPDATE dbo.Forms SET Audience = @a WHERE Slug = @slug", new { a = body.Audience, slug });
    await audit.WriteAsync(ctx, "FormAudienceChanged", "Form", slug, new { before, after = body.Audience });
    return Results.Ok(new { slug, audience = body.Audience });
}).RequireAuthorization("Admin");

// Who gets emailed when this form is submitted. Empty "to" turns notifications off.
api.MapPut("/forms/{slug}/notifications", async (HttpContext ctx, string slug, NotifyBody body) =>
{
    var (exists, allowed) = await ManageAccessAsync(ctx, slug);
    if (!exists) return Results.NotFound();
    if (!allowed) return Results.Forbid();
    await using var db = Db();
    var before = await db.QuerySingleOrDefaultAsync(
        "SELECT NotifyTo, NotifySubject FROM dbo.Forms WHERE Slug = @slug", new { slug });
    if (before is null) return Results.NotFound();
    await db.ExecuteAsync(
        "UPDATE dbo.Forms SET NotifyTo = @To, NotifySubject = @Subject WHERE Slug = @slug",
        new { body.To, body.Subject, slug });
    await audit.WriteAsync(ctx, "NotificationSettingsChanged", "Form", slug, new
    {
        before = new { to = (string?)before.NotifyTo, subject = (string?)before.NotifySubject },
        after = new { to = body.To, subject = body.Subject }
    });
    return Results.Ok(new { slug, body.To, body.Subject });
});

// Admins hand a form to someone else. The owner can edit it and see its submissions.
api.MapPut("/forms/{slug}/owner", async (HttpContext ctx, string slug, OwnerBody body) =>
{
    await using var db = Db();
    var before = await db.QuerySingleOrDefaultAsync("SELECT OwnerAccount FROM dbo.Forms WHERE Slug = @slug", new { slug });
    if (before is null) return Results.NotFound();
    var owner = string.IsNullOrWhiteSpace(body.Owner) ? null : body.Owner.Trim();
    if (owner is not null)
    {
        var person = await db.QuerySingleOrDefaultAsync("SELECT Account FROM dbo.AppUsers WHERE Account = @owner AND IsActive = 1", new { owner });
        if (person is null) return Results.BadRequest(new { error = "That person isn't an active user of FormRelay" });
        owner = (string)person.Account;
    }
    await db.ExecuteAsync("UPDATE dbo.Forms SET OwnerAccount = @owner WHERE Slug = @slug", new { owner, slug });
    await audit.WriteAsync(ctx, "FormOwnerChanged", "Form", slug, new { before = (string?)before.OwnerAccount, after = owner });
    return Results.Ok(new { slug, owner });
}).RequireAuthorization("Admin");

// Active people, for pick lists (assigning a request, choosing an owner). Names only.
api.MapGet("/assignees", async () =>
{
    await using var db = Db();
    var rows = await db.QueryAsync("SELECT Account, DisplayName FROM dbo.AppUsers WHERE IsActive = 1 ORDER BY ISNULL(DisplayName, Account)");
    return rows.Select(r => new { account = (string)r.Account, displayName = (string?)r.DisplayName });
});

// ---------------------------------------------------------------- lookups

// Which forms (latest version of each) use which lists. Used to show "used by" and to block deleting a list in use.
async Task<Dictionary<string, List<(string Slug, string Name)>>> ListUsageAsync(SqlConnection db)
{
    var usage = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
    var defs = await db.QueryAsync("""
        SELECT f.Slug, f.Name, v.Definition
        FROM dbo.Forms f JOIN dbo.FormVersions v ON v.FormId = f.Id
        WHERE v.Version = (SELECT MAX(Version) FROM dbo.FormVersions WHERE FormId = f.Id)
        """);
    foreach (var d in defs)
        foreach (var f in FieldsOf(JsonNode.Parse((string)d.Definition)!.AsObject()))
            if (f["lookup"] is JsonValue lv && lv.TryGetValue<string>(out var lookup))
            {
                if (!usage.TryGetValue(lookup, out var forms)) usage[lookup] = forms = [];
                if (!forms.Any(x => x.Item1 == (string)d.Slug)) forms.Add(((string)d.Slug, (string)d.Name));
            }
    return usage;
}

api.MapGet("/lookups", async () =>
{
    await using var db = Db();
    var rows = (await db.QueryAsync("""
        SELECT l.Name, l.Source, l.IsPublicSafe, l.LastSyncedUtc,
               (SELECT COUNT(*) FROM dbo.LookupItems i WHERE i.LookupId = l.Id) AS ItemCount
        FROM dbo.Lookups l ORDER BY l.Name
        """)).ToList();
    var usage = await ListUsageAsync(db);
    return rows.Select(r => new
    {
        name = (string)r.Name, source = (string)r.Source, isPublicSafe = (bool)r.IsPublicSafe,
        lastSyncedUtc = (DateTime?)r.LastSyncedUtc, itemCount = (int)r.ItemCount,
        usedBy = usage.TryGetValue((string)r.Name, out var u) ? u.Select(x => new { slug = x.Slug, name = x.Name }).ToList() : []
    });
});

// Create a list or replace a list's choices (and, optionally, whether public forms may use it). Administrators only.
api.MapPut("/lookups/{name}", async (HttpContext ctx, string name, LookupBody body) =>
{
    if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9]+(-[a-z0-9]+)*$") || name.Length > 100)
        return Results.BadRequest(new { error = "A list's name can use lowercase letters, numbers and single dashes" });
    var items = (body.Items ?? []).Select(i => new { Value = (i.Value ?? "").Trim(), Label = (i.Label ?? i.Value ?? "").Trim() }).ToList();
    if (items.Count == 0) return Results.BadRequest(new { error = "A list needs at least one choice" });
    if (items.Count > 500) return Results.BadRequest(new { error = "A list can have 500 choices at most" });
    if (items.Any(i => i.Value.Length == 0 || i.Value.Length > 200 || i.Label.Length == 0 || i.Label.Length > 200))
        return Results.BadRequest(new { error = "Every choice needs some text (200 characters at most)" });
    var dup = items.GroupBy(i => i.Value, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
    if (dup is not null) return Results.BadRequest(new { error = $"'{dup.Key}' appears more than once" });

    await using var db = Db();
    await db.OpenAsync();
    await using var tx = await db.BeginTransactionAsync();
    var existing = await db.QuerySingleOrDefaultAsync("SELECT Id, Source, IsPublicSafe FROM dbo.Lookups WHERE Name = @name", new { name }, tx);
    int id;
    if (existing is null)
        id = await db.ExecuteScalarAsync<int>(
            "INSERT dbo.Lookups (Name, Source) OUTPUT INSERTED.Id VALUES (@name, 'manual')", new { name }, tx);
    else
    {
        // Lists fed by a connector are overwritten by the sync, so they can't be edited by hand.
        if ((string)existing.Source != "manual") return Results.BadRequest(new { error = "This list is filled by a connector and can't be edited by hand" });
        id = (int)existing.Id;
    }
    if (body.IsPublicSafe is not null)
        await db.ExecuteAsync("UPDATE dbo.Lookups SET IsPublicSafe = @p WHERE Id = @id", new { p = body.IsPublicSafe, id }, tx);
    await db.ExecuteAsync("DELETE dbo.LookupItems WHERE LookupId = @id", new { id }, tx);
    await db.ExecuteAsync("INSERT dbo.LookupItems (LookupId, Value, Label, SortOrder) VALUES (@id, @Value, @Label, @SortOrder)",
        items.Select((it, i) => new { id, it.Value, it.Label, SortOrder = i }), tx);
    await tx.CommitAsync();
    await audit.WriteAsync(ctx, existing is null ? "LookupCreated" : "LookupReplaced", "Lookup", name, new
    {
        count = items.Count, isPublicSafe = body.IsPublicSafe,
        publicChangedFrom = existing is null ? (bool?)null : (bool)existing.IsPublicSafe
    });
    return Results.Ok(new { name, count = items.Count });
}).RequireAuthorization("Admin");

// Delete a list nobody uses.
api.MapDelete("/lookups/{name}", async (HttpContext ctx, string name) =>
{
    await using var db = Db();
    var row = await db.QuerySingleOrDefaultAsync("SELECT Id, Source FROM dbo.Lookups WHERE Name = @name", new { name });
    if (row is null) return Results.NotFound();
    if ((string)row.Source != "manual") return Results.BadRequest(new { error = "Lists filled by a connector can't be deleted here" });
    var usage = await ListUsageAsync(db);
    if (usage.TryGetValue(name, out var forms) && forms.Count > 0)
        return Results.Conflict(new { error = $"Still used by: {string.Join(", ", forms.Select(f => f.Name))}" });

    await db.OpenAsync();
    await using var tx = await db.BeginTransactionAsync();
    await db.ExecuteAsync("DELETE dbo.LookupItems WHERE LookupId = @id", new { id = (int)row.Id }, tx);
    await db.ExecuteAsync("DELETE dbo.Lookups WHERE Id = @id", new { id = (int)row.Id }, tx);
    await tx.CommitAsync();
    await audit.WriteAsync(ctx, "LookupDeleted", "Lookup", name);
    return Results.Ok(new { name });
}).RequireAuthorization("Admin");

api.MapGet("/lookups/{name}/items", async (string name) =>
{
    await using var db = Db();
    var rows = await db.QueryAsync("""
        SELECT i.Value, i.Label FROM dbo.LookupItems i JOIN dbo.Lookups l ON l.Id = i.LookupId
        WHERE l.Name = @name ORDER BY i.SortOrder
        """, new { name });
    return rows.Select(r => new { value = (string)r.Value, label = (string)r.Label });
});

// ---------------------------------------------------------------- connectors (admins): pull picklist choices from elsewhere

object ConnectorShape(dynamic c) => new
{
    id = (int)c.Id, name = (string)c.Name, kind = (string)c.Kind, settings = JsonNode.Parse((string)c.Settings),
    secretRef = (string?)c.SecretRef, lookupName = (string)c.LookupName, refreshMinutes = (int?)c.RefreshMinutes,
    isEnabled = (bool)c.IsEnabled, lastRunUtc = (DateTime?)c.LastRunUtc, lastStatus = (string?)c.LastStatus, lastMessage = (string?)c.LastMessage
};

string? ValidateConnector(ConnectorBody b)
{
    if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Length > 100) return "Give the connector a name (100 characters at most)";
    if (b.Kind is not ("sql" or "rest" or "csv")) return "Kind must be sql, rest or csv";
    if (string.IsNullOrWhiteSpace(b.LookupName) || b.LookupName.Length > 100 || !System.Text.RegularExpressions.Regex.IsMatch(b.LookupName, "^[a-z0-9][a-z0-9-]*$"))
        return "The list name may use lowercase letters, numbers and dashes";
    if (b.Settings is not JsonObject) return "Settings must be an object";
    if (b.RefreshMinutes is < 5 or > 10080) return "Refresh must be between 5 minutes and 7 days (or empty for manual only)";
    if (b.SecretRef is { Length: > 0 } && !System.Text.RegularExpressions.Regex.IsMatch(b.SecretRef, "^[A-Za-z0-9_.-]{1,100}$")) return "Secret name may use letters, numbers, dots, dashes and underscores";
    return null;
}

api.MapGet("/connectors", async () =>
{
    await using var db = Db();
    return (await db.QueryAsync("SELECT * FROM dbo.Connectors ORDER BY Name")).Select(ConnectorShape);
}).RequireAuthorization("Admin");

api.MapPost("/connectors", async (HttpContext ctx, ConnectorBody b) =>
{
    if (ValidateConnector(b) is { } err) return Results.BadRequest(new { error = err });
    await using var db = Db();
    if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Connectors WHERE Name = @Name", new { b.Name }) > 0)
        return Results.Conflict(new { error = "A connector with that name already exists" });
    var id = await db.ExecuteScalarAsync<int>("""
        INSERT dbo.Connectors (Name, Kind, Settings, SecretRef, LookupName, RefreshMinutes, IsEnabled)
        OUTPUT INSERTED.Id VALUES (@Name, @Kind, @settings, @SecretRef, @LookupName, @RefreshMinutes, COALESCE(@IsEnabled, 1))
        """, new { b.Name, b.Kind, settings = b.Settings!.ToJsonString(), SecretRef = b.SecretRef?.Trim(), b.LookupName, b.RefreshMinutes, b.IsEnabled });
    await audit.WriteAsync(ctx, "ConnectorCreated", "Connector", b.Name, new { b.Kind, b.LookupName });
    return Results.Created($"/api/connectors/{id}", new { id });
}).RequireAuthorization("Admin");

api.MapPut("/connectors/{id:int}", async (HttpContext ctx, int id, ConnectorBody b) =>
{
    if (ValidateConnector(b) is { } err) return Results.BadRequest(new { error = err });
    await using var db = Db();
    var n = await db.ExecuteAsync("""
        UPDATE dbo.Connectors SET Name = @Name, Kind = @Kind, Settings = @settings, SecretRef = @SecretRef,
               LookupName = @LookupName, RefreshMinutes = @RefreshMinutes, IsEnabled = COALESCE(@IsEnabled, IsEnabled)
        WHERE Id = @id
        """, new { id, b.Name, b.Kind, settings = b.Settings!.ToJsonString(), SecretRef = b.SecretRef?.Trim(), b.LookupName, b.RefreshMinutes, b.IsEnabled });
    if (n == 0) return Results.NotFound();
    await audit.WriteAsync(ctx, "ConnectorChanged", "Connector", b.Name, new { b.Kind, b.LookupName, b.IsEnabled });
    return Results.Ok(new { id });
}).RequireAuthorization("Admin");

api.MapDelete("/connectors/{id:int}", async (HttpContext ctx, int id) =>
{
    await using var db = Db();
    var row = await db.QuerySingleOrDefaultAsync("SELECT Name, LookupName FROM dbo.Connectors WHERE Id = @id", new { id });
    if (row is null) return Results.NotFound();
    string name = row.Name;
    await db.ExecuteAsync("DELETE dbo.Connectors WHERE Id = @id", new { id });
    // The list and its current choices stay, and become hand-editable again.
    await db.ExecuteAsync("UPDATE dbo.Lookups SET Source = 'manual' WHERE Name = @n AND Source = 'connector'", new { n = (string)row.LookupName });
    await audit.WriteAsync(ctx, "ConnectorDeleted", "Connector", name, null);
    return Results.NoContent();
}).RequireAuthorization("Admin");

// Try a connector without saving anything: returns the first rows it would produce.
api.MapPost("/connectors/test", async (ConnectorBody b, ConnectorService svc, CancellationToken ct) =>
{
    if (b.Kind is not ("sql" or "rest" or "csv") || b.Settings is not JsonObject so) return Results.BadRequest(new { error = "Choose a kind and fill in its settings" });
    try
    {
        var rows = await svc.FetchAsync(b.Kind, so, b.SecretRef, ct);
        return Results.Ok(new { count = rows.Count, sample = rows.Take(15) });
    }
    catch (Exception e) when (e is not OperationCanceledException)
    {
        return Results.BadRequest(new { error = e.Message });
    }
}).RequireAuthorization("Admin");

// Refresh one connector now.
api.MapPost("/connectors/{id:int}/run", async (HttpContext ctx, int id, ConnectorService svc, CancellationToken ct) =>
{
    await using var db = Db();
    await db.OpenAsync(ct);
    try
    {
        var count = await svc.RunAsync(db, id, ct);
        await audit.WriteAsync(ctx, "ConnectorRun", "Connector", id.ToString(), new { count });
        return Results.Ok(new { count });
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (Exception e) when (e is not OperationCanceledException) { return Results.BadRequest(new { error = e.Message }); }
}).RequireAuthorization("Admin");

// ---------------------------------------------------------------- submitting (signed-in users, internal or public forms)

api.MapPost("/forms/{slug}/submissions",
    (HttpContext ctx, string slug, JsonElement payload) => SubmitAsync(ctx, slug, payload, publicOnly: false));

// ---------------------------------------------------------------- submissions: admins only

async Task<IResult> SubmissionsJson(string slug, long? afterId, int? days)
{
    await using var db = Db();
    var rows = await db.QueryAsync("""
        SELECT s.Id, v.Version AS FormVersion, s.SubmittedUtc, s.SubmittedBy, s.Status, s.AssignedTo,
               s.StatusUpdatedUtc, s.StatusUpdatedBy, s.Payload
        FROM dbo.Submissions s
        JOIN dbo.FormVersions v ON v.Id = s.FormVersionId
        JOIN dbo.Forms f ON f.Id = v.FormId
        WHERE f.Slug = @slug AND s.Id > @after
          AND (@days IS NULL OR s.SubmittedUtc >= DATEADD(DAY, -@days, SYSUTCDATETIME()))
        ORDER BY s.Id DESC
        """, new { slug, after = afterId ?? 0, days });
    return Results.Ok(rows.Select(r => new
    {
        r.Id, r.FormVersion, r.SubmittedUtc, r.SubmittedBy, r.Status, r.AssignedTo, r.StatusUpdatedUtc, r.StatusUpdatedBy,
        Payload = JsonDocument.Parse((string)r.Payload).RootElement
    }));
}

async Task<IResult> SubmissionsCsv(string slug, int? days)
{
    await using var db = Db();
    var rows = (await db.QueryAsync("""
        SELECT s.Id, s.SubmittedUtc, s.SubmittedBy, s.Status, s.AssignedTo, s.Payload
        FROM dbo.Submissions s
        JOIN dbo.FormVersions v ON v.Id = s.FormVersionId
        JOIN dbo.Forms f ON f.Id = v.FormId
        WHERE f.Slug = @slug
          AND (@days IS NULL OR s.SubmittedUtc >= DATEADD(DAY, -@days, SYSUTCDATETIME()))
        ORDER BY s.Id
        """, new { slug, days })).ToList();

    var parsed = rows.Select(r => new
    {
        Id = (long)r.Id,
        Ts = (DateTime)r.SubmittedUtc,
        By = (string?)r.SubmittedBy ?? "",
        Status = (string)r.Status,
        Assigned = (string?)r.AssignedTo ?? "",
        Doc = JsonDocument.Parse((string)r.Payload)
    }).ToList();

    // Columns follow the form's field order; anything submitted that isn't in the definition goes at the end.
    var cols = new List<string>();
    var defJson = await db.ExecuteScalarAsync<string?>("""
        SELECT TOP 1 v.Definition FROM dbo.FormVersions v JOIN dbo.Forms f ON f.Id = v.FormId
        WHERE f.Slug = @slug ORDER BY v.Version DESC
        """, new { slug });
    if (defJson is not null)
        foreach (var f in FieldsOf(JsonNode.Parse(defJson)!.AsObject()))
            if (f["key"]?.GetValue<string>() is { } key) cols.Add(key);
    foreach (var extra in parsed.SelectMany(p => p.Doc.RootElement.EnumerateObject().Select(o => o.Name)).Distinct())
        if (!cols.Contains(extra)) cols.Add(extra);

    static string Esc(string s) => s.Contains('"') || s.Contains(',') || s.Contains('\n')
        ? "\"" + s.Replace("\"", "\"\"") + "\""
        : s;

    var sb = new StringBuilder();
    sb.AppendLine(string.Join(',', new[] { "Id", "SubmittedUtc", "SubmittedBy", "Status", "AssignedTo" }.Concat(cols.Select(Esc))));
    foreach (var p in parsed)
    {
        var cells = cols.Select(c => p.Doc.RootElement.TryGetProperty(c, out var v)
            ? Esc(v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString())
            : "");
        sb.AppendLine(string.Join(',', new[]
        {
            p.Id.ToString(), p.Ts.ToString("o", CultureInfo.InvariantCulture), Esc(p.By), Esc(p.Status), Esc(p.Assigned)
        }.Concat(cells)));
    }
    return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"{slug}-submissions.csv");
}

// For the app's screens. Shows the last 90 days by default; ?days=0 shows everything.
static int? ViewDays(int? days) => days is null ? AppViewDays : (days == 0 ? null : days);

api.MapGet("/forms/{slug}/submissions", async (HttpContext ctx, string slug, long? afterId, int? days) =>
{
    var (exists, allowed) = await ManageAccessAsync(ctx, slug);
    if (!exists) return Results.NotFound();
    if (!allowed) return Results.Forbid();
    await audit.WriteAsync(ctx, "SubmissionsViewed", "Form", slug);
    return await SubmissionsJson(slug, afterId, ViewDays(days));
});

api.MapGet("/forms/{slug}/submissions.csv", async (HttpContext ctx, string slug, int? days) =>
{
    var (exists, allowed) = await ManageAccessAsync(ctx, slug);
    if (!exists) return Results.NotFound();
    if (!allowed) return Results.Forbid();
    await audit.WriteAsync(ctx, "SubmissionsExported", "Form", slug, new { days = ViewDays(days) });
    return await SubmissionsCsv(slug, ViewDays(days));
});

// Move a request along (New, In progress, Done) and/or assign it. AssignedTo "" means unassigned; null means unchanged.
api.MapPut("/submissions/{id:long}/tracking", async (HttpContext ctx, long id, TrackingChange body) =>
{
    if (body.Status is not null and not ("New" or "In progress" or "Done"))
        return Results.BadRequest(new { error = "Status must be New, In progress, or Done" });

    await using var db = Db();
    var before = await db.QuerySingleOrDefaultAsync("""
        SELECT s.Status, s.AssignedTo, f.Slug
        FROM dbo.Submissions s JOIN dbo.FormVersions v ON v.Id = s.FormVersionId JOIN dbo.Forms f ON f.Id = v.FormId
        WHERE s.Id = @id
        """, new { id });
    if (before is null) return Results.NotFound();
    if (!(await ManageAccessAsync(ctx, (string)before.Slug)).Allowed) return Results.Forbid();

    var status = body.Status ?? (string)before.Status;
    var assigned = body.AssignedTo is null ? (string?)before.AssignedTo : (body.AssignedTo == "" ? null : body.AssignedTo);
    await db.ExecuteAsync("""
        UPDATE dbo.Submissions
        SET Status = @status, AssignedTo = @assigned, StatusUpdatedUtc = SYSUTCDATETIME(), StatusUpdatedBy = @by
        WHERE Id = @id
        """, new { id, status, assigned, by = ctx.User.Identity?.Name });
    await audit.WriteAsync(ctx, "SubmissionTrackingChanged", "Submission", id.ToString(), new
    {
        before = new { status = (string)before.Status, assignedTo = (string?)before.AssignedTo },
        after = new { status, assignedTo = assigned }
    });
    return Results.Ok(new { id, status, assignedTo = assigned });
});

// Outbox status for troubleshooting.
api.MapGet("/notifications", async () =>
{
    await using var db = Db();
    return Results.Ok(await db.QueryAsync("""
        SELECT TOP 100 Id, SubmissionId, ToAddresses, Subject, Status, Attempts, LastError, CreatedUtc, SentUtc
        FROM dbo.Notifications ORDER BY Id DESC
        """));
}).RequireAuthorization("Admin");

// ---------------------------------------------------------------- machine access (other systems pulling data)
// API key; returns everything, no 90-day limit.

bool KeyOk(HttpContext ctx)
{
    if (string.IsNullOrEmpty(readKey)) return false;
    var given = Encoding.UTF8.GetBytes(ctx.Request.Headers["X-Api-Key"].ToString());
    return CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(readKey));
}

app.MapGet("/api/export/forms/{slug}/submissions", async (HttpContext ctx, string slug, long? afterId) =>
{
    if (!KeyOk(ctx)) return Results.Unauthorized();
    await audit.WriteAsync("api-key", "SubmissionsPulled", "Form", slug, new { afterId }, ctx.Connection.RemoteIpAddress?.ToString());
    return await SubmissionsJson(slug, afterId, null);
}).AllowAnonymous();

app.MapGet("/api/export/forms/{slug}/submissions.csv", async (HttpContext ctx, string slug) =>
{
    if (!KeyOk(ctx)) return Results.Unauthorized();
    await audit.WriteAsync("api-key", "SubmissionsExported", "Form", slug, null, ctx.Connection.RemoteIpAddress?.ToString());
    return await SubmissionsCsv(slug, null);
}).AllowAnonymous();

// ---------------------------------------------------------------- users (admins)

api.MapGet("/users", async () =>
{
    await using var db = Db();
    var rows = await db.QueryAsync(
        "SELECT Id, Account, DisplayName, Role, IsActive, CreatedUtc, LastSignInUtc FROM dbo.AppUsers ORDER BY Account");
    return rows.Select(r => new { r.Id, r.Account, r.DisplayName, r.Role, r.IsActive, r.CreatedUtc, r.LastSignInUtc });
}).RequireAuthorization("Admin");

api.MapPost("/users", async (HttpContext ctx, NewUser body) =>
{
    var account = (body.Account ?? "").Trim();
    var parts = account.Split('\\');
    if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
        return Results.BadRequest(new { error = "Account must look like DOMAIN\\username" });
    if (body.Role is not ("Admin" or "User"))
        return Results.BadRequest(new { error = "Role must be Admin or User" });

    await using var db = Db();
    try
    {
        await db.ExecuteAsync(
            "INSERT dbo.AppUsers (Account, DisplayName, Role) VALUES (@account, @name, @role)",
            new { account, name = string.IsNullOrWhiteSpace(body.DisplayName) ? null : body.DisplayName.Trim(), role = body.Role });
    }
    catch (SqlException e) when (e.Number is 2601 or 2627) { return Results.Conflict(new { error = "That account is already listed" }); }
    userDir.Invalidate(account);
    await audit.WriteAsync(ctx, "UserAdded", "User", account, new { body.Role });
    return Results.Created("/api/users", new { account, body.Role });
}).RequireAuthorization("Admin");

api.MapPut("/users/{id:int}", async (HttpContext ctx, int id, UserChange body) =>
{
    await using var db = Db();
    var user = await db.QuerySingleOrDefaultAsync<AppUser>(
        "SELECT Id, Account, DisplayName, Role, IsActive FROM dbo.AppUsers WHERE Id = @id", new { id });
    if (user is null) return Results.NotFound();

    var role = body.Role ?? user.Role;
    var active = body.IsActive ?? user.IsActive;
    var name = body.DisplayName ?? user.DisplayName;
    if (role is not ("Admin" or "User")) return Results.BadRequest(new { error = "Role must be Admin or User" });

    // Never leave the app without an active admin.
    var losesAdmin = user is { Role: "Admin", IsActive: true } && (role != "Admin" || !active);
    if (losesAdmin && await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.AppUsers WHERE Role = 'Admin' AND IsActive = 1 AND Id <> @id", new { id }) == 0)
        return Results.Conflict(new { error = "There must be at least one active admin" });

    await db.ExecuteAsync(
        "UPDATE dbo.AppUsers SET Role = @role, IsActive = @active, DisplayName = @name WHERE Id = @id",
        new { id, role, active, name });
    userDir.Invalidate(user.Account);
    await audit.WriteAsync(ctx, "UserChanged", "User", user.Account, new
    {
        before = new { user.Role, user.IsActive, user.DisplayName },
        after = new { Role = role, IsActive = active, DisplayName = name }
    });
    return Results.Ok(new { user.Account, role, isActive = active });
}).RequireAuthorization("Admin");

app.MapFallbackToFile("index.html").AllowAnonymous();   // React client-side routes
app.Run();

record FormInfo(int VersionId, int Version, string Slug, string Name, string? Description, string Audience,
    string? NotifyTo, string? NotifySubject, string DefinitionRaw, JsonObject Definition);
record NewForm(string Slug, string Name, string? Description, string? Audience);
record LookupItem(string? Value, string? Label);
record LookupBody(bool? IsPublicSafe, List<LookupItem>? Items);
record AudienceBody(string? Audience);
record OwnerBody(string? Owner);
record ConnectorBody(string? Name, string? Kind, JsonNode? Settings, string? SecretRef, string? LookupName, int? RefreshMinutes, bool? IsEnabled);
record NotifyBody(string? To, string? Subject);
record NewUser(string? Account, string? DisplayName, string? Role);
record UserChange(string? Role, bool? IsActive, string? DisplayName);
record TrackingChange(string? Status, string? AssignedTo);
