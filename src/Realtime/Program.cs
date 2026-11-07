using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Ar.Auth.OpenId;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
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

// ── pipeline ────────────────────────────────────────────────────────────────
var app = builder.Build();

app.UseCors("origin-fence");      // must precede the fence: preflights terminate here
app.UseAuthentication();          // default scheme handling (challenge cosmetics)
app.UseMiddleware<SecurityFenceMiddleware>(); // the §5.2 two-door fence
app.UseAuthorization();

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
