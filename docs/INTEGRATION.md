# INTEGRATION — connecting clients & app backends to the realtime server

Who this is for: SPA/mobile maintainers wiring the *bus* onto this service,
and app-backend maintainers *publishing* events through it. The wire contract
below is the **phase-0 subset** — endpoints still to come are marked ⬜ and
reserved in plan §5.3 (`../centrifugo/docs/PLAN-OWN-SERVER.md`).

Supporting docs: `docs/TECH.md` (how the server is built) · `README.md` ·
plan §5.2 (trust model) · §5.3 (target API contract).

---

## 1 · The two personas, two doors

```
browser / mobile (read-only) ──► POST /realtime/hub/negotiate  (IdP Bearer header)
                                  ◄ { connectionId, accessToken, … }
                              ──► GET  /realtime/hub (WS upgrade; conn token
                                                   via ?access_token or header)
                                  ◄ SignalR "event" messages

app backend (publisher) ─────► POST /api/publish (IdP Bearer + publisher scope)
                                  ◄ { ok: true, group }
```

## 2 · Trust mechanics

* Your **IdP access token travels only in HTTP headers**. It never goes into a
  URL or a WS query string.
* The negotiate response contains an `accessToken` injected by the server: a
  short-lived **connection JWT** (HS256, `{ sub, client_id, scope, iat, jti }`,
  ≤ 5 min). The SignalR client passes it to the WS handshake automatically;
  your code never touches it.
* Every reconnect repeats the whole cycle (fresh IdP token → negotiate → fresh
  connection JWT). Therefore:
  * supply the IdP token via `accessTokenFactory` (JS) / `AccessTokenProvider`
    (.NET) so it is re-evaluated per connect;
  * **never set `skipNegotiation: true`** — negotiate is the auth entry point.
* **Connect-time group placement** (hub, after the fence validates): the
  connection joins `user_channel|{sub}` — a cross-service channel reachable
  regardless of `client_id` (sends via `POST /api/users/{sub}/send`, §4.1
  row 6) — plus
  every partition-scoped group the identity was added to via the members
  API (membership is stored server-side and re-applied on every reconnect).
* The connection JWT is checked only at handshake time. A connection stays
  alive past its 5-min TTL; expiry triggers a reconnect, not a mid-session kill.
* Identity for fan-out = the verified IdP `sub` claim **partitioned by your
  token's `client_id`**: user identity AND group names are per-service
  namespaces — the same human's tokens from two services are two disjoint
  fan-out addresses, and publishes never reach outside the caller's
  partition (plan R4). Group membership is scoped the same way: the bearer's
  `client_id` locates the group AND the member — a service manages its own
  users into its own groups.

## 3 · Bus client (SPA)

Dependency: `@microsoft/signalr` (^10).

### 3.1 · The code (SDK path)

```js
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';

const connection = new HubConnectionBuilder()
  .withUrl('https://realtime.example/realtime/hub', {
    accessTokenFactory: () => getIdpAccessToken(), // re-invoked per (re)connect
  })
  .withAutomaticReconnect()
  .configureLogging(LogLevel.Warning)
  .build();

// The bus delivers every publication as method "event". Identity lives in
// data.type; from/sent_at are stamped server-side. Ignore unknown types.
connection.on('event', (data) => {
  switch (data.type) {
    case 'chat_receive': renderChat(data.from, data.sent_at, data.text); break;
    // case 'ai_emission': …
    default: break; // forward-compat: skip unknown types
  }
});

await connection.start();
```

You provide the factory returning the *IdP* token and the `on('event', …)`
handlers. The SDK handles negotiate and connect.

### 3.2 · The wire (for debugging / non-SDK ports)

**Step 1 — negotiate:**

```http
POST /realtime/hub/negotiate?negotiateVersion=1 HTTP/1.1
Authorization: Bearer <IdP access token>
Content-Type: application/x-www-form-urlencoded
```

Response `200`:

```json
{
  "connectionId": "zf8FcCYCEjd_Tj1iS2KyEQA==",
  "connectionToken": "…",        // opaque; used by the URL below
  "negotiateVersion": 1,
  "accessToken": "<conn JWT>",   // injected by the fence (§2), not by SignalR
  "availableTransports": [ { "transport": "WebSockets", … } ]  // WS-only
}
```

Tokenless or malformed → `401 {"error":"invalid_token"}`. The `accessToken`
differs from the IdP token and carries `{ sub, client_id, scope, iat, jti }`.

**Step 2 — connect:** the client opens a WebSocket to
`/realtime/hub?id=<connection-token>`. The SDK appends `?access_token=` with the
connection JWT (browsers cannot set WS headers; the fence accepts it on the hub
path, or as an `Authorization: Bearer` header for native transports). The fence
validates it against the in-process HS256 key, then the client sends the
SignalR handshake frame (`{"protocol":"json","version":1}`) and the connection
is live as that user (`sub`). After that the server invokes `"event"`; clients
invoke nothing.

**Reconnects** (`withAutomaticReconnect`): nothing special is needed — every
attempt re-negotiates first. Never cache a connection JWT across reconnects.

### 3.3 · Groups today

Membership is **API-driven** (no auto-join): your backend creates the group
and puts your users in it — see §4.2. Nothing is joined client-side.

## 4 · App backend (publisher)

Auth for every write: `Authorization: Bearer <IdP token>` whose token carries
the publisher scope — default `api://ws-server/app:publisher`
(`PUBLISH_REQUIRED_SCOPE`). The backend holds **zero realtime secrets**; any
scope-bearing M2M token works.

### 4.1 · Send-target matrix

| # | Send target | Endpoint | Status |
|---|---|---|---|
| 1 | To one group | `POST /api/publish { group, data }` | ✅ today |
| 2 | To several groups | `POST /api/broadcast { groups[], data }` | ⬜ Phase 2 |
| 3 | Create a group record | `POST /api/groups { name }` → `201 {ok, group, partition}` | ✅ today (2026-11-07) |
| 4 | Put user into a group | `POST /api/groups/{g}/members { user_id }` | ✅ today — bearer's `client_id` scopes both the group and the member; no client_id in the body, ever |
| 5 | Remove user from group | `DELETE /api/groups/{g}/members?user_id=…` | ✅ today |
| 6 | Send to one user (all connections, any service) | `POST /api/users/{sub}/send { data }` | ✅ today |
| 7 | Send to one connection | `POST /api/connections/{id}/send { data }` | ✅ today — **partition-scoped**: only sockets whose identity lives inside the caller's own `client_id`; unknown → 404, foreign → 403 |
| 8 | Disconnect a connection | `POST /api/connections/{id}/disconnect` | ⬜ Phase 2 |

Row 6 is deliberately NOT partition-scoped: it addresses `user_channel|{sub}`
— the channel every connection joins at connect — so `sub` is the only
address needed and fan-out ignores which client_id the user connected
through (e2e SEND-1..4: a send from service A lands on a user connected via
service B). Same envelope rules; `from` = the calling service's client_id.

Fan-out semantics, server-side:

* **group** → every connection currently in that group, *including other
  devices of the same user*, **inside the caller's `client_id` partition** —
  partition isolation is enforced physically (the publish lands on the
  caller's namespaced group key, which no other service's connection joins).
* **user** (`Clients.User`) → all live connections named by that user identity
  — and identities are partitioned too: the fan-out address is
  `(client_id, sub)`, so user sends never cross service boundaries.
* **connection** (`Clients.Client(id)`) → exactly one socket. Connection ids
  are opaque; they already belong to exactly one partition by construction.

Delivery is **once per connected receiver, at send time**. There is no
store-and-forward: a receiver offline during a send misses that event. The
history buffer (⬜ Phase 3, opt-in per group) is the late-join fix.

### 4.2 · Groups — how a client ends up in one

Your backend owns the flow (no client-side joining, no auto-join ever):

1. `POST /api/groups { name }` — creates the record inside the bearer's
   partition (`409`-free: idempotent, `200` when it already exists).
2. `POST /api/groups/{g}/members { user_id }` — puts the user in; the
   endpoint scopes BOTH the group and the member by the bearer's
   `client_id` (closed world: a service manages its own users only). It
   applies immediately to every live socket of that identity and persists
   the membership — the hub re-applies it on every reconnect, so the
   backend does NOT need to re-add after reconnects.
3. `DELETE /api/groups/{g}/members?user_id=…` — removes (and evicts live
   sockets immediately).
4. Errors: unknown group → `404 {error:"group_not_found"}`; missing
   `user_id` → `400`. Publish to an unregistered group stays lenient
   for now (`200`, zero subscribers) — the strict `404` story arrives with
   Phase 2.

Group names are free-form strings today; each lives inside the caller's
`client_id` partition (see §2), so different services' identical names
never collide and one service cannot publish into another's groups.

### 4.3 · Envelope rules

* Put the event's identity in `data.type`. The server stamps `from` (your
  token's `client_id` — do not send your own) and `sent_at` (RFC 3339 UTC;
  overwritten regardless of what you send).
* Responses: `200 {ok:true, group}` ·
  `400 {error:"group_required"|"data_required"|"data_invalid", detail}` ·
  `401 {error:"invalid_token"}` · `403` (valid bearer, wrong scope).
* **Unknown group today = `200 ok`, delivered to zero subscribers.** SignalR
  keeps no persistent group registry, so a publish to an unknown or empty group
  silently reaches nobody. The "404 if group unknown" behavior arrives with the
  group service (Phase 1). Until then, a typo'd group name fails silently.

### 4.4 · CORS / origins

Your SPA's origin must be in `ALLOWED_ORIGINS` (comma list; one `*` = one
subdomain label, e.g. `https://*.adolfrey.com`). Forbidden origins fail at
preflight before auth is checked. The transport is WebSockets only, so your
proxy must pass WS upgrades.

## 5 · Mobile / native clients (.NET today; Swift/Java later)

Native transports can set headers, so the same flow works without a URL token.
Either call negotiate yourself, or pass `HttpConnection`'s `AccessTokenProvider`
and let the library negotiate. The fence accepts the connection JWT from the
header as well as `?access_token=` (`docs/TECH.md` §2).

## 6 · Things to avoid

1. Never send the IdP token in a URL, a WS query string, or any header other
   than the negotiate `Authorization` — the trust boundary depends on it.
2. Never mock the fence by calling the WS path directly — tokenless handshakes
   get `401` by design.
3. Don't `skipNegotiation`, don't disable the automatic re-negotiation cycle,
   and don't build client-side group management — pieces are owned by the
   backend via the members API (no hub methods exist for clients at all).
4. There is no hub method to invoke and no client→server frame. Every write
   goes through `/api/*` with a scope check.

## 7 · Environment knobs for integrators

* `ALLOWED_ORIGINS` — CORS allow-list (§4.4).
* `AUTH_AUTHORITY` (+ `AUTH_AUDIENCE` to pin `aud`) — IdP issuer.
* `PUBLISH_REQUIRED_SCOPE` — publisher scope (§4).

The full table lives in `docs/TECH.md` §5. `GET /healthz` echoes the effective
auth configuration — useful when a browser client gets an unexpected 401 at
negotiate.
