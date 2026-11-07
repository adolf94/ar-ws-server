using System.Security.Claims;

namespace Realtime.Auth;

/// <summary>Claim-reading helpers that work across both schemes (mapped and raw claim names).</summary>
public static class PrincipalExtensions
{
    /// <summary>The verified user id (<c>sub</c>), however the JWT handler mapped it.</summary>
    public static string? GetSubject(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? principal.FindFirstValue("sub");

    /// <summary>The OAuth client of the token (the per-service isolation key, plan R4).</summary>
    public static string? GetClientId(this ClaimsPrincipal principal) =>
        principal.FindFirstValue("client_id") ?? principal.FindFirstValue("azp");
}
