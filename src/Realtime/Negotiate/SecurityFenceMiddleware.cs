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
/// Everything else passes through untouched (server API endpoints carry
/// their own [Authorize]).
/// </summary>
public sealed class SecurityFenceMiddleware(RequestDelegate next, IConnectionTokenService tokens, ILogger<SecurityFenceMiddleware> logger)
{
    private const string HubPath = "/realtime/hub";

    public async Task InvokeAsync(HttpContext context)
    {
        // TEMP spike diagnostics: every /realtime/hub request logged at dispatch
        if (context.Request.Path.StartsWithSegments("/realtime/hub"))
        {
            logger.LogInformation("fence dispatch: {Method} {Path} negotiatePath={IsNegotiatePath} isWs={IsWs}",
                context.Request.Method,
                context.Request.Path,
                context.Request.Path == $"{HubPath}/negotiate",
                context.WebSockets.IsWebSocketRequest);
        }

        if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path == $"{HubPath}/negotiate")
        {
            await HandleNegotiate(context);
            return;
        }

        if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == HubPath && context.WebSockets.IsWebSocketRequest)
        {
            await HandleWebSocketHandshake(context);
            return;
        }

        await next(context);
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
                negotiateResponse["accessToken"] = tokens.Issue(context.User);
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
        logger.LogInformation(
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
