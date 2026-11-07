using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Ar.Auth.OpenId;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Tokens;
using Realtime.Api;
using Realtime.Auth;
using Realtime.Connections;
using Realtime.Groups;
using Realtime.Hubs;
using Realtime.Negotiate;

var builder = WebApplication.CreateBuilder(args);

// ── configuration: 12-factor env style, matching the existing stack ─────────
var cfg = builder.Configuration;

string Conf(string name, string fallback = "") =>
    cfg[name] is { Length: > 0 } value ? value : fallback;

var authority      = Conf("AUTH_AUTHORITY", "http://localhost:9001/realms/demo"); // mock idp in the spike; auth.adolfrey.com in prod
var audience       = Conf("AUTH_AUDIENCE");                                       // empty => issuer-only trust (plan §5.2)
var allowedOrigins = Conf("ALLOWED_ORIGINS", "http://localhost:3000,https://*.adolfrey.com");
var publishScope   = Conf("PUBLISH_REQUIRED_SCOPE", "api://ws-server/app:publisher");
var connTokenTtl   = int.TryParse(Conf("CONN_TOKEN_TTL", ""), out var ttl) && ttl > 0 ? ttl : ConnectionTokenService.DefaultTtlSeconds;
// §9 rate limiting: fixed-window per client_id partition
var rateWindowSec  = int.TryParse(Conf("RATE_WINDOW_SECONDS", ""), out var rws) && rws > 0 ? rws : 60;
var rateLimit      = int.TryParse(Conf("RATE_MAX_PER_WINDOW", ""), out var rls) && rls > 0 ? rls : 60;
// the unicast lanes carry high-volume streams too — an AI reasoning backend
// sending chain-of-thought deltas per chunk is the motivating use case — so
// send addresses get their own (much higher) ceiling within the same window.
var rateSendLimit  = int.TryParse(Conf("RATE_SEND_PER_WINDOW", ""), out var rsl) && rsl > 0 ? rsl : 600;
var connSecret     = Conf("CONN_TOKEN_SECRET");

void WarnBootstrap(string message)
{
    // startup-time warning beforeILoggerFactory exists
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"[realtime] WARNING: {message}");
    Console.ResetColor();
}

if (Encoding.UTF8.GetBytes(connSecret).Length < 32)
{
    connSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    WarnBootstrap("CONN_TOKEN_SECRET missing/short — generated EPHEMERAL dev secret (connection tokens die with the process)");
}

// ── SignalR + the platform's official IdP client ────────────────────────────
builder.Services.AddSignalR(o => o.EnableDetailedErrors = builder.Environment.IsDevelopment());
builder.Services.AddArAuth(o =>
{
    o.Authority = authority;
    if (audience.Length > 0) o.Audience = audience; // set = pinned audience; unset = issuer-only
});

// ── auth wiring: two schemes per plan §5.2 ──────────────────────────────────
var connSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(connSecret));

builder.Services.AddSingleton<IConnectionTokenService>(new ConnectionTokenService(connSigningKey, connTokenTtl));
builder.Services.AddSingleton<IConnectionRegistry, InMemoryConnectionRegistry>();
builder.Services.AddSingleton<IGroupRegistry, InMemoryGroupRegistry>();
builder.Services.AddSingleton<IConnectionAborts, InMemoryConnectionAborts>();
builder.Services.AddSingleton<IHistoryBuffer, InMemoryHistoryBuffer>();   // Phase 3: opt-in per group
builder.Services.AddSingleton<IGroupPartitioner, GroupPartitioner>();   // plan R4 namespaces
builder.Services.AddSingleton<IUserIdProvider, SubUserIdProvider>();
builder.Services.AddSingleton<IAuthorizationHandler>(new PublisherScopeHandler(publishScope));

builder.Services
    .AddAuthentication(options => options.DefaultChallengeScheme = AuthSchemes.Idp)
    .AddScheme<AuthenticationSchemeOptions, ArAccessTokenHandler>(AuthSchemes.Idp, o => { })
    .AddJwtBearer(AuthSchemes.Conn, o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = ConnectionTokenService.Issuer,
            ValidateAudience = true,
            ValidAudience = ConnectionTokenService.Audience,
            IssuerSigningKey = connSigningKey,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        // connection JWT minted by us: keep raw claim names ("sub", "scope")
        o.MapInboundClaims = false;
        o.Events = new JwtBearerEvents
        {
            // the documented SignalR pattern — query token on the HUB PATH ONLY
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!StringValues.IsNullOrEmpty(token) &&
                    context.HttpContext.Request.Path == "/realtime/hub")
                {
                    context.Token = token;
                }
                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("publisher", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new PublisherScopeRequirement(publishScope));
    });

// ── origin fence (CORS): plan R9 ────────────────────────────────────────────
var origins = allowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(options => options.AddPolicy("origin-fence", policy => policy
    .WithOrigins(origins) // one '*' = one subdomain label — same glob rule as Centrifugo
    .AllowAnyHeader()
    .AllowAnyMethod()
    .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

// ── API surface (plan §5.3) ─────────────────────────────────────────────────
builder.Services.AddOpenApi();                    // machine contract: /openapi/v1.json

// §9: fixed-window, partitioned by the verified bearer's client_id
// (anonymous requests → the "anon" partition). The claims must be resolvable
// BEFORE this middleware runs, so the fence pre-authenticates every request
// that presents an Authorization header (see §3).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (ctx, ct) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = rateWindowSec.ToString();
        ctx.HttpContext.Response.ContentType = "application/json";
        return new ValueTask(ctx.HttpContext.Response.WriteAsJsonAsync(new
        {
            error = "rate_limited",
            detail = $"this client_id partition exceeded its rate budget ({rateWindowSec}s window); retry after the window (see Retry-After)",
        }, ct));
    };

    // default lane: reads, publish/broadcast, group + members ops, disconnect
    options.AddPolicy<string>(
        "partitioned",
        httpContext => RateLimitPartition.GetFixedWindowLimiter(
            // partition key: the verified client_id (fence eager-auth), anon otherwise
            httpContext.User.GetClientId() ?? "anon",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimit,
                Window = TimeSpan.FromSeconds(rateWindowSec),   // fixed window per partition
                QueueLimit = 0,                                  // no queuing: refuse
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));

    // unicast lane (POST /api/users/{sub}/send, POST /api/connections/{id}/send):
    // the reasoning/streaming lane — same per-client_id partition + window,
    // but a ceiling far above the default lane. If a stream trips even this
    // budget the 429 + Retry-After tells the caller when to resume.
    options.AddPolicy<string>(
        "partitioned-send",
        httpContext => RateLimitPartition.GetFixedWindowLimiter(
            httpContext.User.GetClientId() ?? "anon",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateSendLimit,
                Window = TimeSpan.FromSeconds(rateWindowSec),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            }));
});

// ── pipeline ────────────────────────────────────────────────────────────────
var app = builder.Build();

app.UseCors("origin-fence");      // must precede the fence: preflights terminate here
app.UseAuthentication();          // default scheme handling (challenge cosmetics)
app.UseMiddleware<SecurityFenceMiddleware>(); // the §5.2 two-door fence (+ eager auth for rate limiting)
app.UseRateLimiter();             // §9: partitions need context.User set by the fence
app.UseAuthorization();

app.MapOpenApi();                 // /openapi/v1.json — contract freeze (plan §8 Phase 2 exit)
app.MapGet("/healthz", () => Results.Ok(new
{
    ok = true,
    service = "realtime",
    phase = "phase-0 spike",
    auth = new
    {
        negotiate_scheme = AuthSchemes.Idp,
        hub_scheme = AuthSchemes.Conn,
        authority = authority,
        audience_pinned = audience.Length > 0,
        connection_token_ttl_seconds = connTokenTtl,
        connection_token_secret_present = Conf("CONN_TOKEN_SECRET").Length > 0,
    },
    transports = "websockets-only",
}));

app.MapHub<RealtimeHub>("/realtime/hub", options => options.Transports = HttpTransportType.WebSockets);
app.MapRealtimeApi();

app.Run();
