using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Realtime.Auth;

namespace Realtime.Negotiate;

/// <summary>
/// The security fence between the Wire and SignalR, implementing plan §5.2's
/// two-door split:
///
///   • POST /realtime/hub/negotiate — authenticate with the IdP scheme, run
///     SignalR's negotiate, then INJECT the short-lived connection JWT into
///     the response's "accessToken" field (the native SignalR mechanism,
///     TransportProtocols.md's { url, accessToken } extension).
///
///   • GET /realtime/hub (WS upgrade) — authenticate with the connection
///     scheme; the principal carries the minted identity into the hub.
///
/// Everything else: eager-auth on any request with an Authorization header
/// (so rate limiting partitions on the verified client_id, §9), then pass
/// through untouched — server API endpoints carry their own [Authorize].
/// </summary>
public sealed class SecurityFenceMiddleware(RequestDelegate next, IConnectionTokenService tokens, OriginAllowList origins, ILogger<SecurityFenceMiddleware> logger)
{
    private const string HubPath = "/realtime/hub";

    public async Task InvokeAsync(HttpContext context)
    {
        // TEMP spike diagnostics: every /realtime/hub request logged at dispatch
        if (context.Request.Path.StartsWithSegments("/realtime/hub"))
        {
            logger.LogDebug("fence dispatch: {Method} {Path} negotiatePath={IsNegotiatePath} isWs={IsWs} connection='{Connection}' upgrade='{Upgrade}' version='{Version}'",
                context.Request.Method,
                context.Request.Path,
                context.Request.Path == $"{HubPath}/negotiate",
                context.WebSockets.IsWebSocketRequest,
                context.Request.Headers.Connection.ToString(),
                context.Request.Headers.Upgrade.ToString(),
                context.Request.Headers["Sec-WebSocket-Version"].ToString());
        }

        if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path == $"{HubPath}/negotiate")
        {
            if (!await IsOriginAllowed(context)) return;
            await HandleNegotiate(context);
            return;
        }

        if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == HubPath
            && (context.WebSockets.IsWebSocketRequest
                // node/native SignalR transports upgrade identically, but
                // HttpContext.WebSockets may report false for them — accept
                // the explicit upgrade headers as the transport signal
                || (context.Request.Headers.Connection.ToString().Contains("Upgrade", StringComparison.OrdinalIgnoreCase)
                    && context.Request.Headers.Upgrade.ToString().Contains("websocket", StringComparison.OrdinalIgnoreCase))))
        {
            if (!await IsOriginAllowed(context)) return;
            await HandleWebSocketHandshake(context);
            return;
        }

        // ── eager auth (owner-approved rate limiting, §9): every non-hub
        // request presenting an Authorization header pre-authenticates so
        // the rate limiter partitions on the VERIFIED client_id. Best-effort:
        // no token / invalid token falls through untouched — the endpoint's
        // own [Authorize] will still challenge with the proper 401/403.
        if (context.Request.Headers.Authorization.Count > 0)
        {
            var eagerAuth = await context.AuthenticateAsync(AuthSchemes.Idp);
            if (eagerAuth.Succeeded && eagerAuth.Principal is not null)
                context.User = eagerAuth.Principal;
        }

        await next(context);
    }

    // Browsers exempt WebSocket handshakes from CORS, so the hub path checks the
    // Origin header itself (cross-site WebSocket hijacking). Requests without an
    // Origin are non-browser clients (native/.NET) and are not origin-bound.
    private async Task<bool> IsOriginAllowed(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length == 0 || origins.IsAllowed(origin)) return true;

        logger.LogWarning("hub: rejected origin {Origin} on {Path}", origin, context.Request.Path);
        await WriteJson(context, StatusCodes.Status403Forbidden, new JsonObject
        {
            ["error"] = "forbidden_origin",
            ["detail"] = "the request origin is not in ALLOWED_ORIGINS",
        });
        return false;
    }

    private async Task HandleNegotiate(HttpContext context)
    {
        var idpAuth = await context.AuthenticateAsync(AuthSchemes.Idp);
        if (!idpAuth.Succeeded)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
            await WriteJson(context, StatusCodes.Status401Unauthorized, new JsonObject
            {
                ["error"] = "invalid_token",
                ["detail"] = "realtime negotiate requires a valid IdP access token",
            });
            return;
        }
        context.User = idpAuth.Principal!;
        logger.LogInformation("negotiate: verified user={User} client={ClientId}",
            idpAuth.Principal!.GetSubject(), idpAuth.Principal.GetClientId());

        // ── connect-time partition alias (CONTRACT §1, additive 2026-11-08) ──
        // negotiate ?partition=<client_id>: granted iff the VERIFIED token's
        // aud claim contains the requested partition — aud is IdP-issued,
        // so the operator controls which client apps may join which
        // resource partition via audience issuance, not by server config.
        // The minted connection JWT carries the alias as its client_id —
        // the BUS identity plane lands there (groups, memberships, socket
        // ops) while the REST plane keeps the real token's client_id.
        // Closes the SPA-vs-backend client split (SPA app token audiences
        // the backend resource → may join the backend's group plane).
        // The reserved pseudo-partition user_channel is never grantable:
        // aud-a misconfig must not hand out the global unicast join key.
        string? partitionAlias = null;
        var requestedPartition = context.Request.Query["partition"].ToString();
        if (requestedPartition.Length > 0)
        {
            if (requestedPartition == Realtime.Groups.GroupPartitioner.UserChannelPartition)
            {
                logger.LogWarning("negotiate: partition alias {Partition} rejected — reserved pseudo-partition",
                    requestedPartition);
                await WriteJson(context, StatusCodes.Status403Forbidden, new JsonObject
                {
                    ["error"] = "partition_not_allowed",
                    ["detail"] = "the requested partition is reserved and cannot be joined",
                });
                return;
            }
            var inAud = idpAuth.Principal.FindAll("aud").Any(c =>
                string.Equals(c.Value, requestedPartition, StringComparison.Ordinal));
            if (!inAud)
            {
                logger.LogWarning("negotiate: partition alias {Partition} rejected — token aud does not contain it",
                    requestedPartition);
                await WriteJson(context, StatusCodes.Status403Forbidden, new JsonObject
                {
                    ["error"] = "partition_not_in_token",
                    ["detail"] = "requested partition is not in the bearer token's aud claim",
                });
                return;
            }
            partitionAlias = requestedPartition;
            logger.LogInformation("negotiate: partition alias {Partition} granted (token aud contains it)",
                partitionAlias);
        }

        // Run SignalR's negotiate, then inject accessToken into the response.
        var originalBody = context.Response.Body;
        try
        {
            using var buffer = new MemoryStream();
            context.Response.Body = buffer;
            await next(context);
            context.Response.Body = originalBody;

            if (context.Response.StatusCode == StatusCodes.Status200OK)
            {
                buffer.Seek(0, SeekOrigin.Begin);
                var negotiateResponse = await JsonNode.ParseAsync(buffer);
                if (negotiateResponse is null)
                {
                    logger.LogWarning("negotiate: could not parse SignalR's response; accessToken NOT injected");
                    return;
                }
                negotiateResponse["accessToken"] = tokens.Issue(context.User, partitionAlias);
                var bytes = Encoding.UTF8.GetBytes(negotiateResponse.ToJsonString());
                context.Response.ContentLength = bytes.Length;
                await originalBody.WriteAsync(bytes);
                logger.LogInformation("negotiate: connection token issued (ttl from config), injected into response");
            }
            else
            {
                logger.LogWarning("negotiate: signalr handler answered {Status}; response relayed unchanged", context.Response.StatusCode);
                buffer.Seek(0, SeekOrigin.Begin);
                await buffer.CopyToAsync(originalBody);
            }
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private async Task HandleWebSocketHandshake(HttpContext context)
    {
        var connAuth = await context.AuthenticateAsync(AuthSchemes.Conn);
        if (!connAuth.Succeeded)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
            await WriteJson(context, StatusCodes.Status401Unauthorized, new JsonObject
            {
                ["error"] = "invalid_token",
                ["detail"] = "the hub handshake requires the connection token issued at negotiate (?access_token)",
            });
            return;
        }
        context.User = connAuth.Principal!;
        var principal = connAuth.Principal!;
        var claimDump = string.Join(" | ", principal.Claims.Select(c => $"{c.Type}={(c.Value.Length <= 24 ? c.Value : c.Value[..24] + "..")}"));
        logger.LogDebug(
            "hub handshake: connection token verified; identity.Name={Name} nameId={NameId} rawSub={RawSub}; claims=[{Claims}]",
            principal.Identity?.Name,
            principal.FindFirstValue(ClaimTypes.NameIdentifier),
            principal.FindFirstValue("sub"),
            claimDump);
        await next(context);
    }

    private static async Task WriteJson(HttpContext context, int status, JsonObject body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes);
    }
}
