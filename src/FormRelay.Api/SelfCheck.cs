using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FormRelay.Api;

public record CheckResult(string Name, string Status, string Detail);   // Status: ok | warn | fail

/// <summary>
/// Read-only self-check. Reports what is wrong without changing anything. Used by GET /api/diagnostics (admins)
/// and, through the installer's "check" mode, after every install and update.
/// </summary>
public class SelfCheck(IConfiguration config, IHostEnvironment env)
{
    public static string Version =>
        typeof(SelfCheck).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    public async Task<List<CheckResult>> RunAsync(CancellationToken ct = default)
    {
        var r = new List<CheckResult> { new("Version", "ok", Version) };

        if (env.IsDevelopment())
            r.Add(new("Environment", "warn", "Running in Development mode. Do not set ASPNETCORE_ENVIRONMENT on a server."));

        var conn = config.GetConnectionString("Default");
        try
        {
            await using var db = new SqlConnection(conn);
            await db.OpenAsync(ct);
            r.Add(new("Database", "ok", $"Connected to {await db.ExecuteScalarAsync<string>("SELECT DB_NAME()")}"));

            var schema = await db.ExecuteScalarAsync<int?>("SELECT MAX(Version) FROM dbo.SchemaVersion");
            r.Add(schema is null
                ? new("Schema", "warn", "No schema version recorded. Run the installer's update to apply migrations.")
                : new("Schema", "ok", $"Version {schema}"));

            var admins = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.AppUsers WHERE Role = 'Admin' AND IsActive = 1");
            var bootstrap = config.GetSection("Auth:BootstrapAdmins").GetChildren().Any();
            r.Add(admins > 0 && bootstrap ? new("Administrators", "warn", $"{admins} active, but Auth:BootstrapAdmins is still set. Remove it from config.json now that a real admin exists.")
                : admins > 0 ? new("Administrators", "ok", $"{admins} active")
                : bootstrap ? new("Administrators", "warn", "None yet. The first bootstrap admin to sign in will become one.")
                : new("Administrators", "fail", "No admin exists and Auth:BootstrapAdmins is empty. Nobody can manage the app."));

            var o = await db.QuerySingleAsync("""
                SELECT SUM(CASE WHEN Status = 'pending' THEN 1 ELSE 0 END) AS Pending,
                       SUM(CASE WHEN Status = 'failed'  THEN 1 ELSE 0 END) AS Failed,
                       MIN(CASE WHEN Status = 'pending' THEN CreatedUtc END) AS Oldest
                FROM dbo.Notifications
                """);
            int pending = (int?)o.Pending ?? 0, failed = (int?)o.Failed ?? 0;
            DateTime? oldest = o.Oldest;
            var stuck = oldest is { } t && DateTime.UtcNow - t > TimeSpan.FromMinutes(15);
            r.Add(failed > 0 || stuck
                ? new("Email outbox", "warn", $"{pending} pending{(stuck ? " (oldest over 15 minutes old)" : "")}, {failed} failed. Check the email settings.")
                : new("Email outbox", "ok", $"{pending} pending, {failed} failed"));
        }
        catch (Exception e) { r.Add(new("Database", "fail", e.Message.Split('\n')[0])); }

        r.Add(await CheckEmailAsync(ct));
        return r;
    }

    private async Task<CheckResult> CheckEmailAsync(CancellationToken ct)
    {
        var provider = config["Email:Provider"] ?? "Smtp";
        if (provider.Equals("Graph", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var f in new[] { "TenantId", "ClientId", "Sender" })
                if (string.IsNullOrWhiteSpace(config[$"Graph:{f}"])) return new("Email (Graph)", "fail", $"Graph:{f} is not set.");
            var thumb = config["Graph:CertificateThumbprint"];
            if (string.IsNullOrWhiteSpace(thumb))
                return string.IsNullOrWhiteSpace(config["Graph:ClientSecret"]) && string.IsNullOrWhiteSpace(config["Graph:CertificatePath"])
                    ? new("Email (Graph)", "fail", "No certificate thumbprint, .pfx path or client secret is set.")
                    : new("Email (Graph)", "ok", "Using a .pfx path or client secret.");
            foreach (var loc in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
            {
                using var store = new X509Store(StoreName.My, loc);
                store.Open(OpenFlags.ReadOnly);
                var cert = store.Certificates.Find(X509FindType.FindByThumbprint, thumb.Replace(" ", ""), false).FirstOrDefault();
                if (cert is null) continue;
                var days = (int)(cert.NotAfter - DateTime.Now).TotalDays;
                var canSign = false;
                try { canSign = cert.HasPrivateKey && cert.GetRSAPrivateKey() is not null; } catch { }
                if (!canSign) return new("Email (Graph)", "fail", "Certificate found, but the service account can't read its private key.");
                return days < 0 ? new("Email (Graph)", "fail", $"Certificate expired {cert.NotAfter:yyyy-MM-dd}. Mail is not being sent.")
                    : days < 30 ? new("Email (Graph)", "warn", $"Certificate expires {cert.NotAfter:yyyy-MM-dd} ({days} days).")
                    : new("Email (Graph)", "ok", $"Certificate expires {cert.NotAfter:yyyy-MM-dd} ({days} days).");
            }
            return new("Email (Graph)", "fail", "Certificate thumbprint not found in LocalMachine\\My.");
        }

        var host = config["Smtp:Host"];
        if (string.IsNullOrWhiteSpace(host))
            return new("Email (SMTP)", "warn", "Email is not set up yet. Forms work and submissions are saved; notification emails are held until a mail server is set. Run FormRelay-Setup.ps1 set-email (or choose it in FormRelay-Setup.cmd).");
        var port = config.GetValue("Smtp:Port", 25);
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(host, port, cts.Token);
            return new("Email (SMTP)", "ok", $"{host}:{port} is reachable.");
        }
        catch (Exception e) { return new("Email (SMTP)", "fail", $"Can't reach {host}:{port} ({e.GetType().Name})."); }
    }
}
