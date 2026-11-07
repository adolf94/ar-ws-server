using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Ar.Auth.OpenId;
using Ar.Auth.OpenId.Exceptions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Realtime.Auth;

/// <summary>
/// Authentication scheme "ar-idp": verifies the caller's IdP access token
/// (Authorization: Bearer) with the platform's official OIDC client
/// (Ar.Auth.OpenId — discovery + JWKS + issuer/aud/scope/claims policy).
/// Deliberately reads the HEADER only: the WS handshake token (query string)
/// belongs to the <see cref="AuthSchemes.Conn"/> scheme.
/// </summary>
public sealed class ArAccessTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IArAuthClient authClient) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private string? _failureDetail;

    /// <summary>The bearer token — header only, by design.</summary>
    private static string? ReadBearerToken(HttpRequest request)
    {
        if (request.Headers.Authorization is { } header &&
            AuthenticationHeaderValue.TryParse(header, out var parsed) &&
            parsed.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(parsed.Parameter))
        {
            return parsed.Parameter;
        }
        return null;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ReadBearerToken(Request);
        if (token is null)
            return AuthenticateResult.NoResult();

        ClaimsPrincipal principal;
        try
        {
            // Ar.Auth enforces: signature + issuer (+ audience when configured
            // via ArAuthOptions.Audience) + RequiredScopes. Audience stays
            // unset in this deployment: issuer-only trust (plan §5.2).
            principal = await authClient.VerifyTokenAsync(token);
        }
        catch (TokenValidationException ex)
        {
            _failureDetail = ex.Message;
            Logger.LogWarning(ex, "ar-idp: token validation failed: {Reason}", ex.Message);
            return AuthenticateResult.Fail("idp token validation failed");
        }
        catch (ArAuthException ex)
        {
            // configuration / flow problems from Ar.Auth (discovery, metadata, …)
            _failureDetail = ex.Message;
            Logger.LogWarning(ex, "ar-idp: auth client error: {Reason}", ex.Message);
            return AuthenticateResult.Fail("idp token validation failed (auth client error)");
        }

        var subject = principal.GetSubject();
        if (string.IsNullOrEmpty(subject))
        {
            // plan: no anonymous connections — a real user token must carry sub
            _failureDetail = "idp token misses the required sub claim";
            return AuthenticateResult.Fail("idp token misses the required sub claim");
        }

        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var challenge = _failureDetail is { Length: > 0 } detail
            ? $"Bearer error=\"invalid_token\", error_description=\"{Uri.UnescapeDataString(detail)}\"" 
            : "Bearer";
        Response.Headers.WWWAuthenticate = challenge;
        return base.HandleChallengeAsync(properties);
    }
}

/// <summary>Requires the publisher scope in the space-delimited `scope` claim (plan R3).</summary>
public sealed class PublisherScopeRequirement(string scope) : IAuthorizationRequirement
{
    public string Scope { get; } = scope;
}

public sealed class PublisherScopeHandler(string scope) : AuthorizationHandler<PublisherScopeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PublisherScopeRequirement requirement)
    {
        var granted = string.Join(' ', context.User.FindAll("scope").Concat(context.User.FindAll("scp")).Select(c => c.Value))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (granted.Contains(scope, StringComparer.Ordinal))
            context.Succeed(requirement);
        else
            context.Fail(new AuthorizationFailureReason(this, $"missing required scope {requirement.Scope}"));

        return Task.CompletedTask;
    }
}
