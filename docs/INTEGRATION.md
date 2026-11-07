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
                              ──► GET  /realtime/hub (WS upgrade; connection token via ?access_token or header)
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
* Identity for fan-out purposes = the verified IdP `sub` claim. Your token's
  `client_id` determines per-service scoping (⬜ Phase 1 — publish targets
  bare group names today).

## 3 · Bus client (SPA) — verified recipe

Dependencies: `@microsoft/signalr` (^10). Verified by the e2e
(`dev/e2e/e2e.mjs`, 6/6 PASS — shape (a) is the settled negotiate contract).

```js
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';

const idpToken = /* your IdP access token (with any needed scopes) */;

const connection = new HubConnectionBuilder()
  .withUrl('https://realtime.example/realtime/hub', {
    accessTokenFactory: () => idpToken,       // re-invoked per (re)connect
  })
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

Notes:

* **No handler needed for obtaining a token** — the client fetches the
  negotiate response and switches to the injected `accessToken` on its own.
  Your `accessTokenFactory` returning the *IdP* token is the only moving part.
* Groups today: every connection auto-joins group `demo` (phase-0
  convenience). Real membership arrives in Phase 1; the target pattern is
  your app backend calling `POST /api/groups/{g}/members` right after the
  client connects (⬜ — see plan §5.3).
* Reconnect: use `withAutomaticReconnect()`; every successful reconnect went
  through a fresh negotiate automatically, so no special handling needed.

## 4 · App backend (publisher)

One endpoint live today:

```http
POST /api/publish
Authorization: Bearer <IdP access token, carrying the publisher scope>
Content-Type: application/json

{ "group": "demo", "data": { "type": "chat_receive", "text": "hello" } }
```

* Required scope (default): `api://ws-server/app:publisher` — a normal IdP
  bearer from your M2M client with that scope grant. Your backend holds **zero
  realtime secrets**; any scope-bearing IdP token works.
* The event's own identity: set `data.type`. The server stamps `from`
  (your token's `client_id`) and `sent_at` (RFC 3339 UTC) on top — keep your
  own fields inside `data`, and do not send `from`/`sent_at` yourself (they are
  overwritten).
* Responses: `200 {ok:true, group}` · `400 {error:"group_required"|"data_required"|"data_invalid", detail}`
  · `401 {error:"invalid_token"}` (no/bad bearer) · `403` (bearer valid, wrong
  scope). Publishers read these like normal HTTP — no `202`-with-error
  semantics like the old wall.
* `POST /api/broadcast`, group membership endpoints, presence, history, and
  rate limits (429) are the Phase 1–3 plan items — don't build against them
  yet; they will appear with tests in `dev/e2e`.

### CORS / origins

Your SPA's origin must be in the server's `ALLOWED_ORIGINS` (comma list; one
`*` = one subdomain label, e.g. `https://*.adolfrey.com`). Forbidden origins
fail at preflight before auth matters. WebSockets-only transport — the old
long-polling profiles no longer exist, so your proxy must pass WS upgrades.

## 5 · Mobile / native clients (.NET today; Swift/Java later)

Native transports can set headers, so the same flow works without any URL
token: call negotiate yourself, or — since `?access_token=` is also honored
on the hub path — `HttpConnection`'s `AccessTokenProvider` and let the
library negotiate; the fence extracts the connection JWT from the header too
(`docs/TECH.md` §2). Never cache a connection JWT across reconnects.

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
