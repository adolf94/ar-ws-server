namespace Realtime.Auth;

/// <summary>Authentication scheme name constants for the two-trust-scheme split (plan §5.2).</summary>
public static class AuthSchemes
{
    /// <summary>IdP bearer tokens (RS256/JWKS via Ar.Auth.OpenId) — negotiate + server API.</summary>
    public const string Idp = "ar-idp";

    /// <summary>Connection JWTs (HS256 in-process) — the hub handshake.</summary>
    public const string Conn = "realtime-conn";
}
