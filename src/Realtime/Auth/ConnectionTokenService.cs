using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Realtime.Auth;

/// <summary>Issues the short-lived, server-signed connection JWT injected into the negotiate response (plan §5.2).</summary>
public interface IConnectionTokenService
{
    void Validate(ClaimsPrincipal idpPrincipal);

    /// <exception cref="InvalidOperationException">the IdP principal carries no usable subject.</exception>
    string Issue(ClaimsPrincipal idpPrincipal, string? partitionAlias = null);
}

public sealed class ConnectionTokenService(SecurityKey signingKey, int ttlSeconds) : IConnectionTokenService
{
    // who we are on our own HS256 system: mint + verify in one process
    public const string Issuer = "realtime";
    public const string Audience = "realtime";

    public const int DefaultTtlSeconds = 300; // plan: ≤ 5 minutes

    private readonly JwtSecurityTokenHandler _handler = new();
    private readonly SigningCredentials _credentials = new(signingKey, SecurityAlgorithms.HmacSha256);
    private readonly int _ttl = ttlSeconds > 0 ? ttlSeconds : DefaultTtlSeconds;

    public void Validate(ClaimsPrincipal idpPrincipal)
    {
        if (string.IsNullOrEmpty(idpPrincipal.GetSubject()))
            throw new InvalidOperationException("negotiate principal has no usable sub claim (refusing to mint an anonymous connection token)");
    }

    public string Issue(ClaimsPrincipal idpPrincipal, string? partitionAlias = null)
    {
        var now = DateTimeOffset.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, idpPrincipal.GetSubject()!),
            new(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };

        // identity context we encrypt INTO the connection token (plan R4/R5/R6);
        // a connect-time partition alias (2026-11-08 — CONTRACT §1) overrides
        // client_id here, so the BUS identity plane lands inside the aliased
        // partition. Minted only after the fence validated the allowlist +
        // aud; the REST plane (IdP bearer) is untouched by this claim.
        var clientId = !string.IsNullOrEmpty(partitionAlias) ? partitionAlias : idpPrincipal.GetClientId();
        if (!string.IsNullOrEmpty(clientId)) claims.Add(new Claim("client_id", clientId));
        if (!string.IsNullOrEmpty(partitionAlias)) claims.Add(new Claim("partition_alias", partitionAlias));

        var scopes = idpPrincipal.FindAll("scope").Concat(idpPrincipal.FindAll("scp"));
        var scopeList = string.Join(' ', scopes.Select(c => c.Value).Distinct(StringComparer.Ordinal));
        if (!string.IsNullOrEmpty(scopeList)) claims.Add(new Claim("scope", scopeList));

        var token = _handler.CreateJwtSecurityToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.UtcDateTime.AddSeconds(_ttl),
            SigningCredentials = _credentials,
            Subject = new ClaimsIdentity(claims),
        });
        return _handler.WriteToken(token);
    }
}
