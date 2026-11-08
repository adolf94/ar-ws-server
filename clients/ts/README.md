# @adolf94/realtime-client

> **Purpose**: Official TypeScript/JavaScript consumer client for subscribing to the SignalR realtime event bus.  
> **Audience**: Frontend web developers (React, Vue, SPA), mobile web engineers, and Node.js consumer service developers.  
> **Usage**: Consult for browser and Node client setup, authentication handshake integration (`@adolf94/ar-auth-client`), typed event listener patterns, and Cloudflare bot-mitigation headers.

Consumer client for the **realtime** microservice. Provides SignalR hub subscriptions with event-type routing, automatic reconnection with fresh negotiation, and Cloudflare-safe `User-Agent` headers.

Browser and consumer clients are read-only; publishing events is handled by backend services via the HTTP server API (`docs/CONTRACT.md`). For publisher implementations, see `clients/dotnet`.

## Quick Start (Browser SPA with @adolf94/ar-auth-client)

```ts
import { createRealtimeHub } from '@adolf94/realtime-client';
import { useAuth } from '@adolf94/ar-auth-client';

const { auth } = useAuth(); // ar-auth session

const hub = createRealtimeHub({
  url: 'https://realtime.example.com/realtime/hub',
  tokenFactory: () => auth.getUser().then((u) => u.access_token),
});

hub.on('chat_receive', (envelope) => renderChat(envelope.from, envelope.text));
hub.onAny((envelope) => telemetry.track('bus', envelope.type)); // optional

await hub.start();   // Negotiates auth -> receives connection JWT -> connects WS
await hub.stop();    // Idempotent
```

## Quick Start (Node.js / Tooling)

```ts
// Callers provide fresh IdP access tokens (via ar-auth-client or client_credentials)
const hub = createRealtimeHub({
  url: 'http://local.realtime:5000/realtime/hub',
  tokenFactory: () => getFreshIdpToken(),
});
```

## Event Envelope

```json
{
  "type": "chat_receive",
  "from": "<publisher client_id>",
  "sent_at": "RFC3339 UTC",
  …payload
}
```

* `from` and `sent_at` are stamped server-side.
* Handlers should route on `type` and ignore unrecognized message types for forward compatibility.

## User-Agent & Cloudflare Handling

To avoid triggering bot mitigations against default SignalR user agent strings, this client emits `realtime-client-ts/<version>`:
* **Node.js**: Sets both `X-SignalR-User-Agent` and the `User-Agent` header during negotiate and WebSocket handshake.
* **Browser**: Preserves native browser user agent headers and passes the `X-SignalR-User-Agent` header (permitted by server CORS).
* Custom identifiers can be configured using the `userAgent` option.

## Connection Semantics

* **Token Negotiation**: `tokenFactory` runs on every initial connect and reconnection. Always return a fresh IdP token; connection JWTs expire within 5 minutes.
* **Negotiation Gate**: Never set `skipNegotiation: true`, as negotiation performs required authentication.
* **Connection id**: `hub.connectionId` returns the server-assigned connection id (`POST /api/connections/{id}/send` and disconnect-by-id target). It is `null` until connected and **changes on every reconnect** (each reconnect re-negotiates) — re-read it after reconnecting, or pass an `onReconnected(id)` option to receive the fresh id directly.
* **Delivery**: Delivery is once per active connected receiver at send time. Backend-managed group memberships are automatically re-applied upon reconnect.
* **Reconnect Backoff**: Default retry intervals are `[0, 3000, 6000, 12000, 24000]` ms.
