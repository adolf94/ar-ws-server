# @adolf94/realtime-client

Consumer client for the **realtime** microservice (`../`): SignalR hub
subscription with envelope routing, automatic reconnect (always
re-negotiating), and a Cloudflare-safe `User-Agent` on non-browser lanes.

Read-only by contract: browsers/clients never publish — the bus delivers
server-side fanned-out `event` envelopes only (see the realtime repo's
`docs/CONTRACT.md`). Publishing belongs to backend services via the server
API — publisher clients live in `clients/dotnet` / `clients/python`.

## Quick start (browser SPA + @adolf94/ar-auth-client)

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

await hub.start();   // negotiate (auth entry point) → conn JWT → WS
await hub.stop();    // idempotent
```

## Quick start (Node / tooling)

```ts
// callers own the IdP token minting (ar-auth-client or raw client_credentials)
const hub = createRealtimeHub({
  url: 'http://local.realtime:5000/realtime/hub',
  tokenFactory: () => getFreshIdpToken(),
});
```

## Envelope shape

```json
{ "type": "chat_receive", …payload, "from": "<publisher client_id>", "sent_at": "RFC3339 UTC" }
```

`type` + `from` + `sent_at` are server-stamped. Unknown types must be
ignored — route by a `switch`/Map, never `any`-cast the whole delivery.

## User-Agent (Cloudflare)

The service sits behind Cloudflare; bot mitigation must not see
`Microsoft SignalR/…` from a Node-process. This library sends
`realtime-client-ts/x.y.z` (`X-SignalR-User-Agent` +, on Node lanes, the
actual `User-Agent` header — both override SignalR's defaults on negotiate
*and* the WS handshake). Override it with `userAgent` if you want your app's
name; browsers keep the native UA and only send the `X-SignalR-User-Agent`
override, which the server's CORS policy allows explicitly.

## Semantics

* `tokenFactory` is re-invoked on every (re)connect — return a FRESH IdP
  token; the server issues a ≤ 5-min connection JWTs at negotiate only.
* Never `skipNegotiation: true` (it bypasses the auth entry point).
* No store-and-forward: events are delivered to CONNECTED receivers only;
  backend-managed group membership is re-applied at every reconnect, so
  subscribe before `start()`.
* Reconnect default: `[0, 3000, 6000, 12000, 24000]`.
