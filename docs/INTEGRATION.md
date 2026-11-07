# INTEGRATION — connecting clients & app backends to the realtime server

Who this is for: SPA/mobile maintainers wiring the *bus* onto this service,
and app-backend maintainers *publishing* events through it. The full wire
contract below is the **phase-0 subset** — endpoints still to come are marked
⬜ and reserved in plan §5.3 (`../centrifugo/docs/PLAN-OWN-SERVER.md`).

Supporting docs: `docs/TECH.md` (how the server is built) · `README.md` ·
plan §5.2 (trust model) · §5.3 (target API contract).

---

## 1 · The two personas, two doors

```
browser / mobile (read-only) ──► POST /realtime/hub/negotiate  (IdP Bearer header)
                                  ◄ { connectionId, accessToken, … }
                              ──► GET  /realtime/hub (WS upgrade; conn token
                                                   via ?access_token or header)
                                  ◄ SignalR "event" messages — clients never publish

app backend (publisher) ─────► POST /api/publish (IdP Bearer + publisher scope)
                                  ◄ { ok: true, group }
```

## 2 · Trust mechanics — what you see, what you never see

* Your **IdP access token travels only in HTTP headers** — negotiate is a
  fetch, so headers are allowed. It never goes into a URL or a WS query
  string.
* The negotiate response body contains an `accessToken` field injected by
  the server: a short-lived **connection JWT** (HS256, `{ sub, client_id,
  scope, iat, jti }`, ≤ 5 min). The SignalR client hands it to the WS
  handshake automatically — **your code never touches this token**.
* On every reconnect the whole cycle repeats (fresh IdP token → fresh
  negotiate → fresh connection JWT). Therefore:
  ∙ supply the IdP token via `accessTokenFactory` (JS) / `AccessTokenProvider`
  (.NET) so it's re-evaluated per connect,
  ∙ **never set `skipNegotiation: true`** — negotiate is the auth entry door.
* The connection JWT is checked at handshake time only; a connection stays
  **alive past its 5-min token TTL** — expiry is a *reconnect* concern, not a
  mid-session kill. Rotating `CONN_TOKEN_SECRET` (ops) does force everyone
  through a fresh negotiate.
* Identity for fan-out purposes = the verified IdP `sub` claim. Your token's
  `client_id` determines per-service scoping (⬜ Phase 1 — publish targets
  bare group names today).

## 3 · Bus client (SPA) — verified recipe

Dependencies: `@microsoft/signalr` (^10). Verified by the e2e
(`dev/e2e/e2e.mjs`, 6/6 PASS — shape (a) is the settled negotiate contract).

### 3.1 · The code (SDK path — what you should ship)

```js
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';

const idpToken = /* your IdP access token (with any needed scopes) */;

const connection = new HubConnectionBuilder()
  .withUrl('https://realtime.example/realtime/hub', {
    accessTokenFactory: () => idpToken,       // re-invoked per (re)connect
  })
  .withAutomaticReconnect()
  .configureLogging(LogLevel.Warning)
  .build();

// The bus delivers every publication as method "event". Identity lives in
// data.type; from/sent_at are stamped server-side. IGNORE unknown types.
connection.on('event', (data) => {
  switch (data.type) {
    case 'chat_receive': renderChat(data.from, data.sent_at, data.text); break;
    // case 'ai_emission': …
    default: break;                            // forward-compat: skip silently
  }
});

await connection.start();
```

The SDK performs the entire sequence in §3.2 for you. What you must provide:
the factory returning the *IdP* token, and `on('event', …)` handlers.

### 3.2 · The wire — negotiate, then hub (for debugging / non-SDK ports)

**Step 1 — negotiate** (this is how the client uses negotiate):

```http
POST /realtime/hub/negotiate?negotiateVersion=1 HTTP/1.1
Authorization: Bearer <IdP access token>
Content-Type: application/x-www-form-urlencoded

(SignalR's standard negotiate form body — the auth that matters is the header)
```

Response `200` (shape (a) — verified):

```json
{
  "connectionId": "zf8FcCYCEjd_Tj1iS2KyEQA==",
  "connectionToken": "…",        // opaque; used by the URL below
  "negotiateVersion": 1,
  "accessToken": "<conn JWT>",   // ← injected by OUR fence (§2), not by SignalR
  "availableTransports": [ { "transport": "WebSockets", … } ]  // WS-only fork
}
```

Tokenless/malformed → `401 {"error":"invalid_token"}`. The `accessToken`
differs from the IdP token and carries `{ sub, client_id, scope, iat, jti }`.

**Step 2 — connect via the hub**: the client opens a WebSocket to
`/realtime/hub?id=<connection-token>`, the SDK appends `?access_token=`
(the connection JWT — browser WS cannot set headers; the fence accepts it
on the hub path only, or as an `Authorization: Bearer` header for native
transports). The fence validates it against the in-process HS256 key, then
the SDK sends the SignalR handshake frame (`{"protocol":"json","version":1}`)
and the connection is live as that user (`sub`). After that it's a normal
SignalR connection: the server invokes `"event"`; clients invoke **nothing**.

**Reconnects** (`withAutomaticReconnect`): do nothing special — every attempt
re-negotiates first (factory re-invoked → fresh IdP token → fresh conn JWT).
Never cache a connection JWT across reconnects.

### 3.3 · Groups today

Every connection auto-joins group `demo` (phase-0 convenience; a bare
publish→receive path). Real membership lands in Phase 1 — the target
pattern is your *app backend* calling group-add right after the client
connects (§4.2) — no subscription budget, no shaping channel.

## 4 · App backend (publisher)

Auth for every write: `Authorization: Bearer <IdP token>` whose token carries
the publisher scope — default `api://ws-server/app:publisher` (`PUBLISH_REQUIRED_SCOPE`).
Your backend holds **zero realtime secrets**; any scope-bearing M2M token works.

### 4.1 · Send-target matrix (what exists, what's coming)

| # | Send target | Endpoint | Status |
|---|---|---|---|
| 1 | To one group | `POST /api/publish { group, data }` | ✅ today (e2e-verified) |
| 2 | To several groups | `POST /api/broadcast { groups[], data }` | ⬜ Phase 2 |
| 3 | Put user into a group | `POST /api/groups/{g}/members { user_id }` | ⬜ Phase 1 |
| 4 | Remove user from group | `DELETE /api/groups/{g}/members?user_id=…` | ⬜ Phase 1 |
| 5 | Send to one user (all their connections) | `POST /api/users/{sub}/send { data }` | ⬜ **not yet in plan §5.3** — needs adding (see note) |
| 6 | Send to one connection | `POST /api/connections/{id}/send { data }` | ⬜ **not yet in plan §5.3** — needs adding (see note) |
| 7 | Disconnect a connection | `POST /api/connections/{id}/disconnect` | ⬜ Phase 2 (in plan §5.3) |

Live semantics once received (fan-out primitives, server-side):

* **group** → every connection currently in that group, *including other
  devices of the same user* (group fan-out has no per-user exclusion — that's
  by design).
* **user** (`Clients.User(sub)`, plan R5) → *all* live connections of that
  user — the replacement for the old `user:` reserved channel: one send
  reaches every device, tab, and reconnect instance.
* **connection** (`Clients.Client(id)`) → exactly one socket.

Delivery is **once per connected receiver, at send time** — there is no
store-and-forward: a receiver offline mid-send misses that event (the
history buffer, ⬜ Phase 3, is the late-join/late-receive fix, opt-in per
group).

> **Contract gap (owner decision pending):** plan §5.3 only routes
> publish/broadcast/members/presence/history/disconnect. Targets #5/#6
> (send-to-user, send-to-connection) are wired server-side
> (`Clients.User` / `Clients.Client`) but have **no planned HTTP endpoint**.
> If you need them from an app backend, ask for `/api/users/{sub}/send` and
> `/api/connections/{id}/send` to be added to §5.3 before Phase 2 work
> starts — otherwise unicast stays hub-internal.

### 4.2 · Groups — how a client ends up in one (and how you put it there)

Two mechanics (the second is the target model):

1. **today (phase 0):** every connection auto-joins `demo`.
2. **⬜ Phase 1 (target):** your backend calls `POST /api/groups/{g}/members`
   with the client's `sub` (from the token it issued for them, or echoed from
   the connect record) right after the client connects; remove on demand via
   #4. Membership is keyed by user id, so the add applies to *all* of that
   user's current sockets — and after each reconnect the backend simply
   re-applies the group-adds (connect-time membership is the model; there is
   no group memory in the server to rely on).

Do **not** build against `demo`. Group names are free-form strings today;
per-service scoping by `client_id` (plan R4) lands alongside Phase 1.

### 4.3 · Envelope rules

* Put the event's identity in `data.type`. The server stamps `from`
  (your token's `client_id` — do not send your own) and `sent_at`
  (RFC 3339 UTC; overwritten regardless of what you send).
* Responses: `200 {ok:true, group}` ·
  `400 {error:"group_required"|"data_required"|"data_invalid", detail}` ·
  `401 {error:"invalid_token"}` · `403` (valid bearer, wrong scope).
  Real status codes — no `202`-with-error semantics like the old wall.
* **Unknown group today = `200 ok` delivered to zero subscribers** — SignalR
  keeps no persistent group registry, so a publish to an unknown/empty group
  silently reaches nobody. The plan §5.3 "404 if group unknown" arrives with
  the group service (Phase 1); until then, a typo'd group name fails
  silently — coordinate group names in the event registry instead of probing.

### 4.4 · CORS / origins

Your SPA's origin must be in the server's `ALLOWED_ORIGINS` (comma list; one
`*` = one subdomain label, e.g. `https://*.adolfrey.com`). Forbidden origins
fail at preflight before auth matters. WebSockets-only transport — the old
long-polling profiles no longer exist, so your proxy must pass WS upgrades.

## 5 · Mobile / native clients (.NET today; Swift/Java later)

Native transports can set headers, so the same flow works without any URL
token: call negotiate yourself, or — since `?access_token=` is also honored
on the hub path — `HttpConnection`'s `AccessTokenProvider` and let the
library negotiate; the fence accepts the connection JWT from the header too
(`docs/TECH.md` §2).

## 6 · Things to avoid

1. Never send the IdP token in a URL, WS query string, or header
   other than the negotiate `Authorization` — trust boundaries depend on it.
2. Never mock the fence by calling the WS path directly — tokenless
   handshakes get `401` by design.
3. Don't `skipNegotiation`, don't disable the automatic re-negotiation cycle,
   and don't build your app around the `demo` auto-join (phase-0 only).
4. Don't log or store connection JWTs on the client side — they're worthless
   after ≤ 5 min and the next negotiate mints a fresh one anyway.
5. No `access_token` minting endpoint exists — the old
   `POST /realtime/token` is gone (decided in plan §5.2). If you need a
   server-side token, that's an IdP contract, not this service's job.
6. Clients never publish — there is no hub method to invoke, no client→server
   frames, ever. Every write goes through `/api/*` with a scope check.

## 7 · Operate quickly (self-host check)

```bash
bash dev/run-spike.sh                  # real IdP; e2e 6/6 gate
IDP_MODE=mock bash dev/run-spike.sh    # offline lane
```

Environment knobs that matter to integrators: `ALLOWED_ORIGINS`,
`AUTH_AUTHORITY` (+ `AUTH_AUDIENCE` to pin aud), `PUBLISH_REQUIRED_SCOPE`.
The full table lives in `docs/TECH.md` §5. Server health (`GET /healthz`)
echoes the effective auth configuration — useful when a browser client gets
401 at negotiate unexpectedly.
