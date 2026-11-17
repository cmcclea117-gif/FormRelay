using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace FormRelay.Api.Auth;

/// <summary>After Windows sign-in, attach the person's app role (from dbo.AppUsers) as a claim.</summary>
public class AppUserClaims(UserDirectory users) : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not { IsAuthenticated: true, Name: { } account }) return principal;
        if (principal.HasClaim(c => c.Type == "fr:checked")) return principal;   // already done for this request

        var user = await users.FindAsync(account);
        var extra = new ClaimsIdentity();
        extra.AddClaim(new Claim("fr:checked", "1"));
        if (user is { IsActive: true })
        {
            extra.AddClaim(new Claim(ClaimTypes.Role, user.Role));
            extra.AddClaim(new Claim("fr:name", user.DisplayName ?? account));
        }

        var clone = principal.Clone();
        clone.AddIdentity(extra);
        return clone;
    }
}
