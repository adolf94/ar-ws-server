# INTEGRATION — connecting clients & app backends to the realtime server

> **Purpose**: Developer integration guide with practical recipes for connecting client applications to the SignalR hub and publishing events from backend services.  
> **Audience**: Frontend/SPA engineers, mobile app developers, and backend service integrators.  
> **Usage**: Consult for connection negotiation flows, authentication token lifetimes, event subscription code examples, group management recipes, and rate-limiting handling.

Supporting documentation:
* `docs/CONTRACT.md`: Wire contract and API specifications.
* `docs/TECH.md`: Server architecture, middleware, and configuration.
* `README.md`: Quick start and project overview.

---

## 1 · Architecture Overview

```
browser / mobile (read-only) ──► POST /realtime/hub/negotiate  (IdP Bearer header)
                                  ◄ { connectionId, accessToken, … }
                              ──► GET  /realtime/hub (WS upgrade; conn token via ?access_token or header)
                                  ◄ SignalR "event" messages

app backend (publisher) ─────► POST /api/publish (IdP Bearer + publisher scope)
                                  ◄ { ok: true, group }
```

## 2 · Trust mechanics

* **IdP access tokens travel only in HTTP headers** — never in query strings or WS URLs.
* The negotiate response returns a server-injected `accessToken`: a short-lived **connection JWT** (HS256, `{ sub, client_id, scope, iat, jti }`, TTL ≤ 5 min). The SignalR client forwards this token during WebSocket handshake automatically.
* Every reconnect re-runs the full cycle (fresh IdP token → negotiate → fresh connection JWT).
  * Provide the IdP token using `accessTokenFactory` (JS) or `AccessTokenProvider` (.NET).
  * **Never set `skipNegotiation: true`** — negotiate is the authentication gate.
* **Connect-time group placement**:
  * Connections automatically join `user_channel|{sub}` (a global channel reachable regardless of `client_id` via `POST /api/users/{sub}/send`).
  * Connections join all partition-scoped groups the identity belongs to. Membership is persisted server-side and re-applied on reconnect.
* **Stable addressing (v1.1, opt-in)**: pass `?instance=<tag>` on the hub URL — a client-generated UUID kept in localStorage / process env. The server then binds the socket to a server-minted logical `connection_id` that is delivered to that socket in a server-published `connection_registered` event. The same tag on every reconnect re-binds to the SAME id; the tag is not security-relevant (identity stays fenced by the connection JWT — CONTRACT §2.2). Without a tag the socket gets legacy per-socket semantics (fresh id every reconnect).
* The connection JWT is verified only during the handshake. Existing connections stay alive past token expiry; expiry only affects subsequent reconnects.
* **Identity and fan-out isolation**:
  * User identity and group names are partitioned by the token's `client_id`. Identical user IDs or group names across different services remain completely isolated.
  * Backend management APIs operate strictly within the caller's partition.

## 3 · Bus client (SPA)

Dependency: `@microsoft/signalr` (^10).

### 3.1 · Implementation

```js
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';

const connection = new HubConnectionBuilder()
  .withUrl('https://realtime.example/realtime/hub', {
    accessTokenFactory: () => getIdpAccessToken(), // re-invoked per (re)connect
  })
  .withAutomaticReconnect()
  .configureLogging(LogLevel.Warning)
  .build();

// All publications arrive via the "event" method.
// Event type is in data.type; from and sent_at are stamped server-side.
connection.on('event', (data) => {
  switch (data.type) {
    case 'connection_registered': // v1.1: your logical connection_id (addressing)
      rememberConnectionId(data.connection_id);
      break;
    case 'chat_receive':
      renderChat(data.from, data.sent_at, data.text);
      break;
    default:
      break; // Forward compatibility: ignore unknown types
  }
});

await connection.start();
```

### 3.2 · Wire Protocol (Non-SDK implementations / Debugging)

**Step 1 — Negotiate:**

```http
POST /realtime/hub/negotiate?negotiateVersion=1 HTTP/1.1
Authorization: Bearer <IdP access token>
Content-Type: application/x-www-form-urlencoded
```

Response `200`:

```json
{
  "connectionId": "zf8FcCYCEjd_Tj1iS2KyEQA==",
  "connectionToken": "…",
  "negotiateVersion": 1,
  "accessToken": "<connection JWT>",
  "availableTransports": [ { "transport": "WebSockets" } ]
}
```

Missing or invalid token returns `401 {"error":"invalid_token"}`.

**Step 2 — Connect:**

The client establishes a WebSocket connection to `/realtime/hub?id=<connectionToken>` — append `&instance=<tag>` here to opt into the v1.1 stable logical id (client-generated UUID; see §2). The client appends `?access_token=<connection JWT>` (or provides `Authorization: Bearer <connection JWT>` for native transports). Once the SignalR handshake frame (`{"protocol":"json","version":1}`) completes, the connection is active. Clients only receive `"event"` methods and cannot invoke hub methods; the logical id is handed over as the first server-published envelope on the socket: `{ type: "connection_registered", connection_id: "<uuidv7>", socket_id, instance_tagged, superseded, from: "server", sent_at }` (CONTRACT §5).

**Reconnects**: Always initiate a new negotiate call. Never cache connection JWTs across reconnect attempts.

### 3.3 · Group Membership

Client-side group joining is disabled. Backends manage group membership via the backend members API (§4.2).

## 4 · App backend (publisher)

All write requests require: `Authorization: Bearer <IdP token>` with the publisher scope (`PUBLISH_REQUIRED_SCOPE`, default `api://ws-server/app:publisher`). Publishers require no server-side secrets.

### 4.1 · Endpoint Matrix

| Action | Endpoint | Scoping |
|---|---|---|
| Publish to group | `POST /api/publish` `{ group, data }` | Partition-scoped |
| Broadcast to groups | `POST /api/broadcast` `{ groups[], data }` | Partition-scoped (unknown groups reported in `unknown_groups`) |
| Create / tune group | `POST /api/groups` `{ name, history?, history_size?, history_ttl_seconds? }` | Partition-scoped (idempotent, returns 201/200) |
| Add member | `POST /api/groups/{g}/members` `{ user_id }` | Partition-scoped (applies live + persists) |
| Remove member | `DELETE /api/groups/{g}/members?user_id=…` | Partition-scoped (evicts live sockets) |
| Send to user | `POST /api/users/{sub}/send` `{ data }` | **Global** across all active connections for `sub` |
| Send to connection | `POST /api/connections/{id}/send` `{ data }` | Partition-scoped; `id` = the logical `connection_id` from `connection_registered` — fans to every bound socket, `delivered` count returned; 404 → fall back to the user lane |
| Disconnect socket | `POST /api/connections/{id}/disconnect` | Partition-scoped; aborts ALL sockets bound to the logical id (`aborted` = count); foreign connection returns 403 |
| Group presence | `GET /api/groups/{g}/presence` | Partition-scoped (`user_id`, `client_id`, `online`, `connections`) |
| Group history | `GET /api/groups/{g}/history?limit=` | Partition-scoped (returns buffered events) |
| Clear history | `DELETE /api/groups/{g}/history` | Partition-scoped |

### Fan-Out Semantics:
* **Group**: Reaches all sockets currently joined to the group inside the caller's `client_id` partition.
* **User** (`POST /api/users/{sub}/send`): Reaches all connections of that user identity across all services.
* **Connection** (`POST /api/connections/{id}/send`): Delivers to every live socket bound to the logical `connection_id` within the caller's partition (`delivered` in the response). Addressable ids come only from the `connection_registered` envelope (§3.2); with the `?instance=` tag they are stable across client reconnects, but **not** across server restarts — on `404 connection_not_found` fall back to the user lane above.
* **Delivery**: Delivered once per connected receiver at send time. If history is enabled for a group, messages are buffered for late joins.

### 4.2 · Managing Groups

1. **Create Group**: `POST /api/groups { name }` (idempotent; 201 created or 200 existing).
2. **Add Member**: `POST /api/groups/{g}/members { user_id }` (joins live sockets immediately and persists for automatic reconnects).
3. **Remove Member**: `DELETE /api/groups/{g}/members?user_id=…` (evicts live sockets and removes persisted record).
4. **Broadcast**: `POST /api/broadcast { groups[], data }` (publishes across multiple groups in the partition; returns `groups_delivered` and `unknown_groups`).
5. **List Groups**: `GET /api/groups` (lists caller's groups with `members` count and `online` count).

### 4.3 · Event Envelopes

* Set payload type in `data.type`. The server automatically stamps `from` (caller `client_id`) and `sent_at` (RFC 3339 UTC).
* Coalesced flushes (publisher SDKs, §4.4) ride one envelope with a `batch` array — CONTRACT §3.1: `{ "type": <stream type>, "batch": [chunk…verbatim], "batch_v": 1 }`. The outer `type` is pinned from the handle's first chunk. The server treats it as ONE event: one stamp, one rate-limit unit, one history row.
* Standard errors: `400` (validation), `401` (invalid token), `403` (missing scope or wrong partition), `404` (unknown group or socket).
* Publishing to an unknown or empty group succeeds with `200 ok` and delivers to zero subscribers.

### 4.4 · Rate Limiting

Rate limits use a fixed window per `client_id` (`RATE_WINDOW_SECONDS`, default 60 s):
* **Default lane**: 60 requests/window (reads, publish, broadcast, group operations, disconnect).
* **Unicast send lanes**: 600 requests/window (`users/{sub}/send`, `connections/{id}/send`).

When tripped:
* Returns `429 { "error": "rate_limited", "detail": "…" }` with a `Retry-After` header indicating seconds until reset.
* **For streaming workloads, use the publisher SDKs' coalesced send** rather than hand-rolled batching (semantics identical in both SDKs):
  * python (`ar-realtime-client`, clients/python): `RealtimePublisher.coalesced_send_to_user(sub)` or `coalesced_send_to_connection(id)` (both keyword `max_pending_chars=64_000`) → `CoalescedSendHandle`. The connection-lane target is the logical `connection_id` from `connection_registered` — with the `?instance=` tag it survives tab reconnects; a 404 takes the drop path (see below) and the sender falls back to the user lane.
  * .NET (`Ar.Realtime.Client`, clients/dotnet): `CoalescedSendToUser(sub, CoalesceOptions?)` or `CoalescedSendToConnection(id, CoalesceOptions?)`, `CoalesceOptions.MaxPendingChars = 64_000` → `ICoalescedSendHandle`. Same targeting rule for the connection lane.
  * `offer()` / `Offer()` is validate + enqueue only — never blocks the producer hot path (validated against CONTRACT §3: `data.type` must be a string).
  * Single-flight: at most ONE in-flight publish per handle; chunks offered while a request is in flight ride the NEXT flush, which fires the instant the response lands — batches self-tune to `generation_rate × RTT`, offer order is strictly preserved.
  * Memory bound (`max_pending_chars` / `MaxPendingChars`): the budget counts queued-but-unflushed chunk chars; on overflow the OLDEST pending chunks are shed (freshest reasoning wins; the newest survives even if it alone exceeds the budget). The in-flight batch is already detached and is never shed.
  * On 429: honor `Retry-After` and re-send the SAME batch once; a second 429 (or any other failure — e.g. `404 connection_not_found` / `connection_not_connected` on the connection lane when a tab is gone) drops that batch, records it in stats, and the stream continues. Caveat: python sleeps `Retry-After` seconds, defaulting to 60 s when the header is absent (`coalescing.py` 429 handler); .NET only retries when a positive `Retry-After` was returned (`MapFailure` in `RealtimePublisher.cs`), otherwise it drops immediately.
  * Stream end: `drain()` / `CompleteAsync()` flush the remainder, propagate the last failed batch exactly once, then keep serving stats idempotently; `offer()` rejects afterwards — open a new handle per stream. Running counters: `stats()` / `Snapshot()` → `CoalesceStats(offered, flushed, dropped, publishes, last_error)`.
  * Wire shape: CONTRACT §3.1.

### 4.5 · CORS & Origin Restrictions

Allowed browser origins must be configured in `ALLOWED_ORIGINS` (comma-separated, wildcard `*` matches one subdomain label). Unlisted origins fail preflight and receive `403 forbidden_origin` at negotiate and WebSocket upgrade. Native clients omitting the `Origin` header are exempt.

## 5 · Native / Mobile Clients

Native clients (.NET, mobile) that can set HTTP headers can supply the connection JWT directly in the `Authorization: Bearer` header on the WebSocket upgrade, or use `?access_token=`. Re-negotiate on connection drops to obtain a fresh connection token.

## 6 · Key Environment Variables

| Variable | Description |
|---|---|
| `ALLOWED_ORIGINS` | CORS allowed origins list (§4.5). |
| `AUTH_AUTHORITY` | OIDC authority for token verification. |
| `AUTH_AUDIENCE` | Optional expected audience claim. |
| `PUBLISH_REQUIRED_SCOPE` | Required publisher scope (default `api://ws-server/app:publisher`). |
| `RATE_WINDOW_SECONDS` | Rate limit window duration in seconds. |
| `RATE_MAX_PER_WINDOW` | Standard API rate limit per window. |
| `RATE_SEND_PER_WINDOW` | High-volume send rate limit per window. |
