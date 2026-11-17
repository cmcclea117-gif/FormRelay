using System.Data.Common;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FormRelay.Api.Connectors;

public record Choice(string Value, string Label);

/// <summary>
/// A connector pulls rows from somewhere else (a SQL query, a REST/JSON endpoint, a CSV file) and turns them into
/// the choices of one picklist (dbo.Lookups). Secrets are never stored in the database: a connector's SecretRef names
/// a key under "Connectors:Secrets" in config.json (a SQL connection string, or a REST header value).
///
/// Settings by kind (JSON):
///   sql : { "query": "SELECT Id, Name FROM dbo.Customers", "valueColumn": "Id", "labelColumn": "Name" }
///   rest: { "url": "https://host/api/items", "itemsPath": "data.items", "valueField": "id", "labelField": "name",
///           "headerName": "Authorization" }          (header value comes from the secret)
///   csv : { "path": "C:\\data\\customers.csv", "valueColumn": "Id", "labelColumn": "Name" }
/// </summary>
public class ConnectorService(IConfiguration config, IHttpClientFactory http, ILogger<ConnectorService> log)
{
    public const int MaxChoices = 5000;

    public async Task<List<Choice>> FetchAsync(string kind, JsonObject s, string? secretRef, CancellationToken ct)
    {
        var secret = secretRef is { Length: > 0 } ? config[$"Connectors:Secrets:{secretRef}"] : null;
        var rows = kind switch
        {
            "sql" => await FromSqlAsync(s, secret, ct),
            "rest" => await FromRestAsync(s, secret, ct),
            "csv" => await FromCsvAsync(s, ct),
            _ => throw new InvalidOperationException($"Unknown connector kind '{kind}'")
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Choice>();
        foreach (var c in rows)
        {
            var value = c.Value.Trim(); var label = string.IsNullOrWhiteSpace(c.Label) ? value : c.Label.Trim();
            if (value.Length == 0 || value.Length > 200) continue;
            if (label.Length > 200) label = label[..200];
            if (seen.Add(value)) result.Add(new(value, label));
            if (result.Count >= MaxChoices) break;
        }
        return result;
    }

    // ------------------------------------------------------------ sql
    private static async Task<List<Choice>> FromSqlAsync(JsonObject s, string? connStr, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connStr)) throw new InvalidOperationException("The SQL connection string secret is not set (Connectors:Secrets:<name> in config.json).");
        var query = Req(s, "query").Trim();
        if (!query.StartsWith("select", StringComparison.OrdinalIgnoreCase) && !query.StartsWith("with", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only SELECT queries are allowed. Use a read-only database login as well.");
        var valueCol = Req(s, "valueColumn"); var labelCol = s["labelColumn"]?.GetValue<string>() ?? valueCol;

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = query; cmd.CommandTimeout = 30;
        await using DbDataReader r = await cmd.ExecuteReaderAsync(ct);
        int vi = Ordinal(r, valueCol), li = Ordinal(r, labelCol);
        var list = new List<Choice>();
        while (await r.ReadAsync(ct) && list.Count < MaxChoices * 2)
            if (!r.IsDBNull(vi)) list.Add(new(Convert.ToString(r.GetValue(vi), CultureInfo.InvariantCulture) ?? "",
                r.IsDBNull(li) ? "" : Convert.ToString(r.GetValue(li), CultureInfo.InvariantCulture) ?? ""));
        return list;
    }
    private static int Ordinal(DbDataReader r, string col)
    {
        for (var i = 0; i < r.FieldCount; i++) if (string.Equals(r.GetName(i), col, StringComparison.OrdinalIgnoreCase)) return i;
        throw new InvalidOperationException($"The query did not return a column named '{col}'");
    }

    // ------------------------------------------------------------ rest
    private async Task<List<Choice>> FromRestAsync(JsonObject s, string? secret, CancellationToken ct)
    {
        var url = Req(s, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("The URL must start with http:// or https://");
        var valueField = Req(s, "valueField"); var labelField = s["labelField"]?.GetValue<string>() ?? valueField;

        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(secret) && s["headerName"]?.GetValue<string>() is { Length: > 0 } hn)
            req.Headers.TryAddWithoutValidation(hn, secret);
        using var client = http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));

        var node = root;
        if (s["itemsPath"]?.GetValue<string>() is { Length: > 0 } path)
            foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries)) node = (node as JsonObject)?[part];
        if (node is not JsonArray arr) throw new InvalidOperationException("The response did not contain a list at itemsPath");

        return arr.OfType<JsonObject>()
            .Select(o => new Choice(Text(o[valueField]), Text(o[labelField])))
            .ToList();
    }
    private static string Text(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<string>(out var str) ? str : v.ToString()) : "";

    // ------------------------------------------------------------ csv
    private static async Task<List<Choice>> FromCsvAsync(JsonObject s, CancellationToken ct)
    {
        var path = Req(s, "path");
        if (!File.Exists(path)) throw new InvalidOperationException($"File not found: {path}");
        var valueCol = Req(s, "valueColumn"); var labelCol = s["labelColumn"]?.GetValue<string>() ?? valueCol;
        var lines = await File.ReadAllLinesAsync(path, ct);
        if (lines.Length == 0) return [];
        var header = ParseCsvLine(lines[0]);
        int vi = header.FindIndex(h => h.Equals(valueCol, StringComparison.OrdinalIgnoreCase));
        int li = header.FindIndex(h => h.Equals(labelCol, StringComparison.OrdinalIgnoreCase));
        if (vi < 0) throw new InvalidOperationException($"The file has no column named '{valueCol}'");
        if (li < 0) throw new InvalidOperationException($"The file has no column named '{labelCol}'");
        return lines.Skip(1).Where(l => l.Length > 0).Select(ParseCsvLine)
            .Where(c => c.Count > Math.Max(vi, li)).Select(c => new Choice(c[vi], c[li])).ToList();
    }
    internal static List<string> ParseCsvLine(string line)
    {
        var cells = new List<string>(); var sb = new System.Text.StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted) { if (ch == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else quoted = false; } else sb.Append(ch); }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        cells.Add(sb.ToString());
        return cells;
    }

    private static string Req(JsonObject s, string key) =>
        s[key]?.GetValue<string>() is { Length: > 0 } v ? v : throw new InvalidOperationException($"Setting '{key}' is required");

    // ------------------------------------------------------------ run: fetch, then replace the list's choices
    public async Task<int> RunAsync(SqlConnection db, int id, CancellationToken ct)
    {
        var c = await db.QuerySingleOrDefaultAsync("SELECT * FROM dbo.Connectors WHERE Id = @id", new { id })
            ?? throw new KeyNotFoundException();
        try
        {
            var settings = JsonNode.Parse((string)c.Settings) as JsonObject ?? new JsonObject();
            var choices = await FetchAsync((string)c.Kind, settings, (string?)c.SecretRef, ct);
            if (choices.Count == 0) throw new InvalidOperationException("The source returned no rows; the list was left as it was.");

            string lookup = c.LookupName;
            await using var tx = await db.BeginTransactionAsync(ct);
            var existing = await db.QuerySingleOrDefaultAsync("SELECT Id, Source FROM dbo.Lookups WHERE Name = @lookup", new { lookup }, tx);
            int lookupId;
            if (existing is null)
                lookupId = await db.ExecuteScalarAsync<int>("INSERT dbo.Lookups (Name, Source) OUTPUT INSERTED.Id VALUES (@lookup, 'connector')", new { lookup }, tx);
            else if ((string)existing.Source != "connector")
                throw new InvalidOperationException($"The list '{lookup}' is edited by hand. Pick a new list name for this connector.");
            else lookupId = (int)existing.Id;

            await db.ExecuteAsync("DELETE dbo.LookupItems WHERE LookupId = @lookupId", new { lookupId }, tx);
            await db.ExecuteAsync("INSERT dbo.LookupItems (LookupId, Value, Label, SortOrder) VALUES (@lookupId, @Value, @Label, @SortOrder)",
                choices.Select((x, i) => new { lookupId, x.Value, x.Label, SortOrder = i }), tx);
            await db.ExecuteAsync("UPDATE dbo.Lookups SET LastSyncedUtc = SYSUTCDATETIME() WHERE Id = @lookupId", new { lookupId }, tx);
            await db.ExecuteAsync("UPDATE dbo.Connectors SET LastRunUtc = SYSUTCDATETIME(), LastStatus = 'ok', LastMessage = @m WHERE Id = @id",
                new { id, m = $"{choices.Count} choices" }, tx);
            await tx.CommitAsync(ct);
            return choices.Count;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Connector {Id} failed", id);
            var msg = e.Message.Length > 450 ? e.Message[..450] : e.Message;
            await db.ExecuteAsync("UPDATE dbo.Connectors SET LastRunUtc = SYSUTCDATETIME(), LastStatus = 'failed', LastMessage = @msg WHERE Id = @id", new { id, msg });
            throw;
        }
    }
}

/// <summary>Refreshes connectors that have a RefreshMinutes schedule. Checks once a minute.</summary>
public class ConnectorScheduler(IConfiguration config, ConnectorService svc, ILogger<ConnectorScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var connStr = config.GetConnectionString("Default")!;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var db = new SqlConnection(connStr);
                await db.OpenAsync(stop);
                var due = await db.QueryAsync<int>("""
                    SELECT Id FROM dbo.Connectors
                    WHERE IsEnabled = 1 AND RefreshMinutes IS NOT NULL
                      AND (LastRunUtc IS NULL OR DATEADD(MINUTE, RefreshMinutes, LastRunUtc) <= SYSUTCDATETIME())
                    """);
                foreach (var id in due)
                    try { await svc.RunAsync(db, id, stop); } catch (Exception e) when (e is not OperationCanceledException) { /* recorded on the row */ }
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Connector scheduler pass failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stop); } catch (OperationCanceledException) { }
        }
    }
}
