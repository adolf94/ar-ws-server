# Ar.Realtime.Client

> **Purpose**: Official .NET publisher SDK for emitting events and managing groups through the Realtime Server HTTP API.  
> **Audience**: .NET backend service developers building event-driven publishing and streaming features.  
> **Usage**: Publisher client setup, authentication providers, reasoning stream delivery (coalesced and inline), and rate-limit backoff handling.

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

## Coalesced Streaming (single-flight publishes)

Both send lanes can batch on their own: `Offer()` is validate+enqueue
only, and the next flush fires the instant the in-flight response lands — batch
size self-tunes to `generation_rate × RTT`, ordering guaranteed (request N+1
never sends before N's response). Parity with python `coalesced_send_to_user`:

```csharp
await using var chunks = realtime.CoalescedSendToUser(userSub);
foreach (var chunk in EnumerateModelStream())      // NEVER awaits HTTP
    chunks.Offer(new JsonObject { ["type"] = "reclassifyThinking", ["text"] = chunk });
// DisposeAsync → CompleteAsync: flushes the remainder, propagates the last error once
```

Per-TAB CoT streams ride the **connection lane** the same way —
`CoalescedSendToConnection(connectionId)` (identical semantics; independent
single-flight windows per tab). A closed/foreign connection id surfaces as the
non-429 drop path (404/403).

Explicit form when you want the stats: `await chunks.CompleteAsync()` → throws
the last batch failure once (typically `RealtimeApiException`; then re-serves
idempotent `CoalesceStats` — `Offered`, `Flushed`, `Dropped`, `Publishes`,
`LastError` — via `Snapshot()`). 429s honor `RetryAfterSeconds` and re-send
the SAME batch once; any second failure drops the batch (counted in
`Dropped`) and the stream continues. A non-429 failure — or a 429 that lacks
`Retry-After` — drops immediately without retry. Wire shape is CONTRACT §3.1: one request per flush
carrying `{type, batch, batch_v: 1}`.
`CoalescedSendToUser(userSub, new CoalesceOptions { MaxPendingChars = 64_000 })`
bounds queued-but-unflushed memory by shedding the OLDEST chunks — freshest
reasoning survives a burst.

## Errors & Rate Limiting

* Non-2xx HTTP responses throw `RealtimeApiException` containing `error` and `detail`.
* HTTP 429 (`rate_limited`) responses populate `RetryAfterSeconds`. Wait that long before retrying; immediate retries continue to consume the exhausted partition quota.
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
