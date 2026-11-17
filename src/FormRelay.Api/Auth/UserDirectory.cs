using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;

namespace FormRelay.Api.Auth;

public record AppUser(int Id, string Account, string? DisplayName, string Role, bool IsActive);

/// <summary>
/// Looks up a Windows account in dbo.AppUsers (cached for 30 seconds). Windows proves who someone is;
/// this table decides what they can do. Unlisted accounts get no role.
/// </summary>
public class UserDirectory(IConfiguration config, IMemoryCache cache, IHostEnvironment env, AuditLog audit)
{
    private const string Select = "SELECT Id, Account, DisplayName, Role, IsActive FROM dbo.AppUsers WHERE Account = @account";

    private static string Key(string account) => "user:" + account.ToLowerInvariant();
    private SqlConnection Db() => new(config.GetConnectionString("Default"));

    public Task<AppUser?> FindAsync(string account) =>
        cache.GetOrCreateAsync(Key(account), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return await LoadAsync(account);
        });

    /// <summary>Call after changing a user so the change takes effect immediately.</summary>
    public void Invalidate(string account) => cache.Remove(Key(account));

    private async Task<AppUser?> LoadAsync(string account)
    {
        await using var db = Db();
        var user = await db.QuerySingleOrDefaultAsync<AppUser>(Select, new { account });

        if (user is null)
        {
            if (!await ShouldBootstrapAsync(db, account))
            {
                await audit.WriteAsync(account, "AccessDenied", "User", account, new { reason = "not listed" });
                return null;
            }
            try
            {
                await db.ExecuteAsync(
                    "INSERT dbo.AppUsers (Account, DisplayName, Role) VALUES (@account, @account, 'Admin')", new { account });
                await audit.WriteAsync(account, "BootstrapAdmin", "User", account);
            }
            catch (SqlException e) when (e.Number is 2601 or 2627) { /* created by a concurrent request */ }
            user = await db.QuerySingleOrDefaultAsync<AppUser>(Select, new { account });
        }

        if (user is { IsActive: false })
        {
            await audit.WriteAsync(account, "AccessDenied", "User", user.Id.ToString(), new { reason = "disabled" });
            return user;
        }

        if (user is not null)
        {
            // Count a sign-in at most once per 12 hours so the audit log records sessions, not every request.
            var touched = await db.ExecuteAsync("""
                UPDATE dbo.AppUsers SET LastSignInUtc = SYSUTCDATETIME()
                WHERE Id = @Id AND (LastSignInUtc IS NULL OR LastSignInUtc < DATEADD(HOUR, -12, SYSUTCDATETIME()))
                """, new { user.Id });
            if (touched > 0) await audit.WriteAsync(account, "SignIn", "User", user.Id.ToString());
        }
        return user;
    }

    // The first admin has to come from somewhere. Listed in config (Auth:BootstrapAdmins), or, in
    // Development only, the first person to sign in while the table is empty.
    private async Task<bool> ShouldBootstrapAsync(SqlConnection db, string account)
    {
        var listed = config.GetSection("Auth:BootstrapAdmins").Get<string[]>() ?? [];
        if (listed.Any(a => string.Equals(a, account, StringComparison.OrdinalIgnoreCase))) return true;
        return env.IsDevelopment() && await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.AppUsers") == 0;
    }
}
