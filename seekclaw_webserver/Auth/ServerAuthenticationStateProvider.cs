using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using seekclaw_webserver.Services;

namespace seekclaw_webserver.Auth;

public sealed class ServerAuthenticationStateProvider(
    IHttpContextAccessor httpContextAccessor,
    AuthService auth)
    : AuthenticationStateProvider
{
    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var principal = httpContextAccessor.HttpContext?.User
            ?? new ClaimsPrincipal(new ClaimsIdentity());

        if (principal.Identity?.IsAuthenticated != true)
        {
            return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
        }

        var userIdValue = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdValue, out var userId))
        {
            return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
        }

        var user = await auth.GetByIdAsync(userId);
        if (user is null)
        {
            return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.IsSuperAdmin ? "SuperAdmin" : "User")
        };

        var identity = new ClaimsIdentity(
            claims,
            principal.Identity.AuthenticationType ?? "SeekClawCookie");

        return new AuthenticationState(new ClaimsPrincipal(identity));
    }
}
