# Ar.Realtime.Client

> **Purpose**: Official .NET publisher SDK for emitting events and managing groups through the Realtime Server HTTP API.  
> **Audience**: .NET backend service developers building event-driven publishing and streaming features.  
> **Usage**: Consult for publisher client setup, dependency injection, authentication providers, reasoning stream delta delivery, and rate-limit backoff handling.

Publisher client for the **realtime** microservice server API (`docs/CONTRACT.md`). Supports publish/broadcast, unicast lanes (including high-volume reasoning streams), group and member operations, history, presence, and connection termination.

For consumer (hub subscription) client code, see `clients/ts`.

## Quick Start

```csharp
var arAuth = new ArAuthClient(new ArAuthOptions
{
    Authority = "https://auth.adolfrey.com/api",
    ClientId = "your-m2m-client",      // Must carry the publisher scope
    ClientSecret = "…",
});

await using var realtime = new RealtimePublisher(new RealtimePublisherOptions
{
    BaseUrl = "https://realtime.example.com",   // Without /api suffix
    AccessTokenProvider = new ArAuthAccessTokenProvider(arAuth, "api://ws-server/app:publisher"),
    UserAgentSuffix = "my-service",
});

// Group and membership management
await realtime.CreateGroupAsync("approvals");
await realtime.AddMemberAsync("approvals", userSub); // Joins live sockets and persists

// Unicast AI reasoning / streaming send (batch deltas into token-group chunks)
await realtime.SendToUserAsync(userSub, new JsonObject
{
    ["type"] = "ai_emission",   // Required event discriminator
    ["delta"] = chunkDelta,
});

// Publish event to group
var result = await realtime.PublishAsync("approvals", new JsonObject
{
    ["type"] = "chat_receive",
    ["text"] = "hello",
});
```

> [!NOTE]
> `data.type` is required and enforced client-side. `from` and `sent_at` are automatically stamped server-side; do not supply them in payloads.

## Errors & Rate Limiting

* Non-2xx HTTP responses throw `RealtimeApiException` containing `error` and `detail`.
* HTTP 429 (`rate_limited`) responses populate `RetryAfterSeconds`. Pause caller execution and wait until `RetryAfterSeconds` elapses before retrying; immediate retries continue to consume the exhausted partition quota.
* Client-side validation errors (missing `data.type`, empty identifiers) throw `RealtimeValidationException` before network requests are dispatched.

## User-Agent

Requests identify as `realtime-client-dotnet/0.1.0 (<UserAgentSuffix>)`. Provide `UserAgentSuffix` in `RealtimePublisherOptions` to distinguish traffic in reverse proxy and Cloudflare logs.

## Build, Test & Pack

```bash
# Run test suite
dotnet test tests/Ar.Realtime.Client.Tests

# Package NuGet release
dotnet pack src/Ar.Realtime.Client -c Release -o $LOCAL_NUGET
```
