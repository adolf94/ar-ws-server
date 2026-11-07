# Ar.Realtime.Client

Publisher client for the **realtime** microservice — the HTTP server API
door (CONTRACT v1, `../../docs/CONTRACT.md`): publish/broadcast, unicast
lanes (the reasoning-stream addresses), group + members ops, history,
presence, force-disconnect.

Consumer (hub subscription) code lives in `clients/ts` — per the owner's
role split, .NET backends here are PUBLISHERS.

## Quick start

```csharp
var arAuth = new ArAuthClient(new ArAuthOptions
{
    Authority = "https://auth.adolfrey.com/api",
    ClientId = "your-m2m-client",      // must carry the publisher scope
    ClientSecret = "…",
});

await using var realtime = new RealtimePublisher(new RealtimePublisherOptions
{
    BaseUrl = "https://realtime.example.com",   // no /api suffix
    AccessTokenProvider = new ArAuthAccessTokenProvider(arAuth, "api://ws-server/app:publisher"),
    UserAgentSuffix = "my-service",
});

await realtime.CreateGroupAsync("approvals");
await realtime.AddMemberAsync("approvals", userSub);              // live join + persists

// reasoning stream: proper lane, batch deltas per chunk (CONTRACT §2.1)
//   unicast lanes' ceiling: RATE_SEND_PER_WINDOW (default 600/min)
await realtime.SendToUserAsync(userSub, new JsonObject
{
    ["type"] = "ai_emission",   // REQUIRED — the wire identity (CONTRACT §3)
    ["delta"] = chunkDelta,
});

var result = await realtime.PublishAsync("approvals", new JsonObject
{
    ["type"] = "chat_receive",
    ["text"] = "hello",
});
```

`data.type` is enforced client-side; `from`/`sent_at` are server-stamped —
do NOT set them.

## Errors & rate limiting

Every non-2xx maps to `RealtimeApiException` carrying the envelope's
`error` code + `detail`. A 429 (`rate_limited`, CONTRACT §2.1) additionally
populates `RetryAfterSeconds` — **pause input work and retry at that
deadline**; a spinning retry re-draws from the same exhausted partition.

Validation problems (`data.type` missing, empty member id) throw
`RealtimeValidationException` BEFORE any wire traffic.

## User-Agent (Cloudflare)

The service sits behind Cloudflare; every lane identifies itself as
`realtime-client-dotnet/0.1.0 (suffix)` — override per-app via
`UserAgentSuffix` for traceability in CF logs.

## Build / test / pack

```bash
dotnet test tests/Ar.Realtime.Client.Tests       # SRV env → live conformance
dotnet pack src/Ar.Realtime.Client -c Release -o $LOCAL_NUGET   # 0.1.0-local
```

`Ar.Auth.OpenId` resolves from the sibling repo's local NuGet feed
(`../../NuGet.config` → `../local-nuget`).
