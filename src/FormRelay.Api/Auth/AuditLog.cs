using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FormRelay.Api.Auth;

/// <summary>Writes to dbo.AuditLog. If the write fails the exception propagates, so nothing happens unrecorded.</summary>
public class AuditLog(IConfiguration config)
{
    public async Task WriteAsync(string? actor, string action, string? entityType = null, string? entityId = null,
        object? detail = null, string? ip = null)
    {
        await using var db = new SqlConnection(config.GetConnectionString("Default"));
        await db.ExecuteAsync("""
            INSERT dbo.AuditLog (Actor, Action, EntityType, EntityId, Detail, IpAddress)
            VALUES (@actor, @action, @entityType, @entityId, @detail, @ip)
            """,
            new
            {
                actor, action, entityType, entityId, ip,
                detail = detail is null ? null : JsonSerializer.Serialize(detail)
            });
    }

    /// <summary>Records an action by whoever is making the current request.</summary>
    public Task WriteAsync(HttpContext ctx, string action, string? entityType = null, string? entityId = null, object? detail = null) =>
        WriteAsync(ctx.User.Identity?.Name ?? "anonymous", action, entityType, entityId, detail,
            ctx.Connection.RemoteIpAddress?.ToString());
}
