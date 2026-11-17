using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FormRelay.Api.Email;

/// <summary>Turns a submission into an email subject and plain-text body.</summary>
public static partial class NotificationTemplate
{
    public static (string Subject, string Body) Build(
        string formName, string? subjectTemplate, string definitionJson, JsonElement payload, long submissionId,
        string? submittedBy = null)
    {
        // Field labels come from the form definition: { "fields": [ { "key": "...", "label": "..." } ] }
        var labels = new Dictionary<string, string>();
        var order = new List<string>();
        using (var def = JsonDocument.Parse(definitionJson))
        {
            if (def.RootElement.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
                foreach (var f in fields.EnumerateArray())
                    if (f.TryGetProperty("key", out var k) && k.GetString() is { } key)
                    {
                        order.Add(key);
                        labels[key] = f.TryGetProperty("label", out var l) && l.GetString() is { } lab ? lab : key;
                    }
        }

        // Fields not in the definition (older versions, extras) go after the known ones.
        var values = new Dictionary<string, string>();
        if (payload.ValueKind == JsonValueKind.Object)
            foreach (var p in payload.EnumerateObject())
            {
                values[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
                if (!order.Contains(p.Name)) order.Add(p.Name);
            }

        var subject = string.IsNullOrWhiteSpace(subjectTemplate)
            ? $"New submission: {formName}"
            : Placeholder().Replace(subjectTemplate, m => values.GetValueOrDefault(m.Groups[1].Value, ""));
        subject = subject.ReplaceLineEndings(" ").Trim();   // never let submitted text inject header lines

        var body = new StringBuilder();
        body.AppendLine($"A new \"{formName}\" submission was received (#{submissionId}).");
        if (!string.IsNullOrEmpty(submittedBy)) body.AppendLine($"Submitted by: {submittedBy}");
        body.AppendLine();
        foreach (var key in order)
            body.AppendLine($"{labels.GetValueOrDefault(key, key)}: {values.GetValueOrDefault(key, "")}");

        return (subject, body.ToString());
    }

    [GeneratedRegex(@"\{\{\s*(\w+)\s*\}\}")]
    private static partial Regex Placeholder();
}
