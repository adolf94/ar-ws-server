# Realtime Server — Technical Documentation

Basis for this design: `../centrifugo/docs/PLAN-OWN-SERVER.md` (the plan that
replaced the Centrifugo + proxy-wall stack). That document defines the
*why* — the reasons-ledger R1–R10 and the trust model. This document defines
the *what/how* as it is actually implemented, for contributors working in
this repository.

Owner decisions locked in the plan: Option A (ASP.NET Core + SignalR) ·
negotiate-issued connection tokens (§5.2) · SPA + mobile clients later ·
single-node homelab scale.

---

## 1 · What this is

One container, one process, two doors:

```
                     ┌──────────────── one .NET process (net10.0) ────────────────┐
 SPA / mobile ───────┼─ ► POST /realtime/hub/negotiate   [IdP bearer scheme]      │
                     │     ◄ { connectionId, accessToken (conn JWT, ≤5 min) }     │
                     │                                                           │
 SPA / mobile ───────┼─ ► GET /realtime/hub?access_token=…  [conn-token scheme]  │
                     │      SignalR hub — publications out; clients never publish│
                     │                                                           │
 app backends ───────┼─ ► POST /api/*          [IdP bearer + publisher scope]    │
                     │      server API — publish today; group ops in Phases 1–2  │
 IdP (Ar.Auth) ── JWKS ─► verification anchor for every bearer lane          │
                     └───────────────────────────────────────────────────────────┘
```

* **The bus** is a SignalR hub behind a security fence (`§3`). At connect the
  hub places every connection into `user_channel|{sub}` (cross-service
  unicast address; sends arrive in Phase 2) + its partition-scoped groups.
* **The server API** is a minimal API calling `IHubContext<RealtimeHub>`
  directly — no relay, no injected API key, real HTTP status codes.
* **There is exactly one secret in the whole system**: the in-process
  connection-token signing key. Nothing is shared across containers; app
  backends hold zero realtime secrets.

## 2 · The trust model (plan §5.2, implemented)

Two authentication schemes, by endpoint:

| Endpoint | Scheme (`AuthSchemes.cs`) | Trusts | Verified by |
|---|---|---|---|
| `POST /realtime/hub/negotiate` | `ar-idp` | IdP access tokens (RS256) | `Ar.Auth.OpenId` → `IArAuthClient.VerifyTokenAsync` (discovery + JWKS + issuer (+audience when pinned)); header only |
| `GET /realtime/hub` (WS upgrade) | `realtime-conn` | connection JWTs (HS256) | `Microsoft.AspNetCore.Authentication.JwtBearer` with the in-process symmetric key; token from `?access_token` on the hub path only, or header (native clients) |
| `POST /api/*` | `ar-idp` + `publisher` policy | IdP bearer + scope | same `VerifyTokenAsync` + `PublisherScopeHandler` (space-delimited `scope` claim) |

### Negotiate-issued connection tokens (the core mechanic)

1. Client presents its IdP access token in the `Authorization` header on
   negotiate (a fetch — headers allowed; **the IdP token never touches a URL**).
2. The fence authenticates it (scheme `ar-idp`), then runs SignalR's negotiate.
3. The middleware injects `accessToken` into the negotiate JSON: a
   **connection JWT** — `{ sub, client_id, scope, iat, jti }`, `iss=realtime`,
   `aud=realtime`, TTL ≤ 5 min (`CONN_TOKEN_TTL`), signed HS256 in-process.
   This is the transport protocol's native `{ url, accessToken }` slot
   (`TransportProtocols.md`) — the same mechanism Azure SignalR uses — with
   no custom endpoint and no client-side special casing.
4. The WS handshake is validated against the connection JWT only. In
   browsers it arrives as `?access_token=` (browser WS API cannot set
   headers); native/Node transports send it as the `Authorization: Bearer`
   header on the handshake itself (verified in the installed
   `@microsoft/signalr@10` source: `Platform.isNode` → header, browser →
   query). Both lanes end in the same conn scheme, both gates enforced.
5. On reconnect the client re-negotiates (fresh IdP token via its token
   factory) → fresh connection token. Never set `skipNegotiation: true`.

### Identity inside the hub

* Hub identity = `Context.UserIdentifier` = `partition(client_id)-scoped`
  `sub` (via `SubUserIdProvider`): the fan-out address space is per-service —
  the same user's tokens from two services are two disjoint identities, so
  `Clients.User(sub)` fans out only to connections of *that service's* tokens
  (plan R5 + R4 on the connection plane).
* The bearer's verified `client_id` scopes EVERYTHING a caller names
  (plan R4, owner spec 2026-11-07): `GroupPartitioner` namespaces group
  names and user identities; `IGroupRegistry` stores groups + memberships
  (closed world — a service manages its own users); the hub applies stored
  membership at connect and joins `user_channel|{sub}`.
* Hub-side policies read the embedded `scope`.

## 3 · The security fence (`Negotiate/SecurityFenceMiddleware.cs`)

Single middleware, exact-path dispatch, wired after `UseAuthentication`:

* **negotiate** (`POST /realtime/hub/negotiate`):
  1. `AuthenticateAsync("ar-idp")` → reject `401 {error:"invalid_token"}` otherwise
  2. buffer SignalR's negotiate response body
  3. on `200`, parse JSON and set `"accessToken" = connJwt` (Content-Length fixed)
  4. non-200 responses relay unchanged
* **WS handshake** (`GET /realtime/hub` + upgrade):
  `AuthenticateAsync("realtime-conn")` → `401` otherwise; the principal is
  assigned to `context.User` before the hub accepts the connection.
  Gate note: the match is on the explicit upgrade headers
  (`Connection: Upgrade` + `Upgrade: websocket`) rather than only
  `HttpContext.WebSockets.IsWebSocketRequest`, which reported false for a
  perfectly valid node-client upgrade; the e2e's GATE-1c/1d checks prove
  anonymous/garbage upgrades are rejected (anti-false-pass for WS-2).
* Everything else (server API, health) passes through untouched — those
  endpoints carry their own `[Authorize]`.

Shape (a) (normal negotiate response + accessToken) is **verified working**
with `@microsoft/signalr` — the e2e WS-1/WS-2 checks prove the client
consumed the injected token. The Azure-style `{url, accessToken}` redirect
(shape b) is documented in the plan as the fallback if a future SDK ever
misbehaves.

## 4 · Components (repository map)

| Path | Role |
|---|---|
| `src/Realtime/Program.cs` | Assembly of everything: config, schemes, CORS fence, hub + API endpoints. 12-factor env style. |
| `src/Realtime/Auth/AuthSchemes.cs` | Scheme-name constants (`ar-idp`, `realtime-conn`). |
| `src/Realtime/Auth/ArAccessTokenHandler.cs` | IdP verification scheme (`VerifyTokenAsync`; header-only; `sub` required → no anonymous connections). Also `PublisherScopeRequirement/Handler`. |
| `src/Realtime/Auth/ConnectionTokenService.cs` | Mints + defines the connection JWT (claims, TTL, HS256 in-process). |
| `src/Realtime/Auth/PrincipalExtensions.cs` | `GetSubject()` / `GetClientId()` across mapped and raw claim names. |
| `src/Realtime/Groups/GroupPartitions.cs` | `IGroupPartitioner` — plan R4: encodes `client_id | logical` physical keys for groups AND user identities; every publish, hub join, and `SubUserIdProvider` id goes through it. |
| `src/Realtime/Auth/SubUserIdProvider.cs` | `sub` → partitioned `Context.UserIdentifier` (`client_id`-scoped user namespace). |
| `src/Realtime/Connections/ConnectionRegistry.cs` | `IConnectionRegistry` + in-memory impl: `user → connectionIds`. Bridge for user-targeted group ops (used from Phase 2 on). |
| `src/Realtime/Connections/GroupRegistry.cs` | `IGroupRegistry` — plan §5.4 component 2: partition-scoped group records + membership bookkeeping (member keys = the physical `client_id|sub` identities); drives hub connect-time joins. |
| `src/Realtime/Hubs/RealtimeHub.cs` | WS lifecycle: connect/disconnect registry registration; applies stored group membership + `user_channel|{sub}` at connect (no auto-join); `event` is the only fan-out method. |
| `src/Realtime/Negotiate/SecurityFenceMiddleware.cs` | The fence (§3 above). |
| `src/Realtime/Api/RealtimeApiEndpoints.cs` | Server API: `POST /api/publish` — Bearer + publisher scope, `data`-only convention, `from`/`sent_at` stamping. |
| `dev/mock-idp/server.js` | Offline mock IdP (copied from the wall repo, dependency-free; adds an OIDC discovery doc for Ar.Auth). Fallback only. |
| `dev/e2e/e2e.mjs` | See §6. |
| `dev/e2e/mint-token.mjs` | Refreshes the e2e token cache without running tests. |
| `dev/run-spike.sh` | Boots (mock|real) stack and runs the e2e. |

Runtime packages: `Ar.Auth.OpenId` 1.2.0-local (local NuGet feed, from the
`ar-auth` sibling repo; 1.2.0 fixes http-authority discovery for the mock IdP
lane — `ArAuthClient` now sets `HttpDocumentRetriever.RequireHttps` based on
the authority scheme), `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.0.
`NuGet.config`), `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.0.
SignalR itself ships in the shared framework.

## 5 · Configuration (environment)

| Variable | Default (spike) | Meaning |
|---|---|---|
| `AUTH_AUTHORITY` | `http://localhost:9001/realms/demo` (mock) / `https://auth.adolfrey.com/api/` (real) | OIDC authority Ar.Auth resolves (discovery + JWKS). Issuer-only trust by default. |
| `AUTH_AUDIENCE` | *(empty)* | Empty = issuer-only trust (plan §5.2). Set to pin the `aud` check. |
| `ALLOWED_ORIGINS` | `http://localhost:3000,https://*.adolfrey.com` | Origin fence — one `*` per origin = one subdomain label (same glob rule as the wall). |
| `PUBLISH_REQUIRED_SCOPE` | `api://ws-server/app:publisher` | Scope required by the `publisher` policy on `/api/*`. |
| `CONN_TOKEN_SECRET` | *(ephemeral random in dev, warned at boot)* | HMAC key for connection JWTs. ≥ 32 bytes. Prod: `openssl rand -hex 32`. The **only** secret. |
| `CONN_TOKEN_TTL` | `300` | Connection JWT TTL seconds (≤ 5 min per plan). |
| `ASPNETCORE_URLS` | — | e.g. `http://localhost:5000`. |
| `ASPNETCORE_ENVIRONMENT` | — | `Development` enables detailed hub errors. |

`GET /healthz` echoes the effective auth configuration (no secrets).

## 6 · Running and testing (current status: `TODO.md`)

```bash
# server (terminal 1)
cd src/Realtime
ASPNETCORE_URLS=http://localhost:5000 \
  AUTH_AUTHORITY=https://auth.adolfrey.com/api/ \
  CONN_TOKEN_SECRET=$(openssl rand -hex 32) \
  dotnet run

# e2e (terminal 2) — real IdP; credentials sourced, never printed (run-spike.sh)
bash dev/run-spike.sh           # IDP_MODE defaults to real
IDP_MODE=mock bash dev/run-spike.sh   # offline lab lane
```

e2e focus (per owner request): **server/WS tests, not authority tests**.
Token source priority: `IDP_TOKEN` env → `.idp-token-cache.json` ( refreshed
via `node dev/e2e/mint-token.mjs`) → fresh mint (authority round-trip only as
a fallback). No secret or full token is ever printed — decoded claims only.

Checks today: negotiate injects `accessToken` (shape a) · handshake consumes
it (proven real by the WS-gate negatives) · publish → 200 · publication
received with `from`/`sent_at` · unauthenticated publish → 401 · tokenless
negotiate → 401 · WS upgrade without/garbage connection token → rejected ·
(mock mode only) scopeless publish → 403. Latest full real-IdP run: 9/9 PASS;
latest server/WS-only run: 8/8 PASS with hub identity verified
(`connected: user=speedtest`).

## 7 · Public surface (implemented vs planned)

| Endpoint | State | Notes |
|---|---|---|
| `GET /healthz` | ✅ spike | auth config echo |
| `POST /realtime/hub/negotiate` | ✅ spike | Bearer(IdP) in header → conn JWT out |
| `GET /realtime/hub` (WS only) | ✅ spike | read-only clients; `event` method carries publications |
| `POST /api/publish` `{group, data}` | ✅ spike | publisher scope; envelope stamping |
| `POST /api/broadcast` | ⬜ Phase 2 | |
| `POST /api/groups` `{name}` | ✅ 2026-11-07 | create group record, auto-scoped to bearer's partition; idempotent |
| `POST /api/groups/{g}/members` `{user_id}` | ✅ 2026-11-07 | bearer's client_id scopes group + member; applies live + persists |
| `DELETE /api/groups/{g}/members?user_id=…` | ✅ 2026-11-07 | same scoping; evicts live sockets |
| `POST /api/users/{sub}/send` `{data}` | ✅ 2026-11-07 | cross-service unicast via `user_channel|{sub}`; NOT partition-scoped by design |
| `POST /api/connections/{id}/send` `{data}` | ✅ 2026-11-07 | per-socket send, PARTITION-scoped: caller reaches only its own client's sockets (404 unknown / 403 foreign) |
| `GET /api/groups[/presence|/history]` | ⬜ Phase 2 | presence + ring-buffer history (+ group listing with counts) |
| Prometheus metrics, rate limiting | ⬜ Phase 3 | |
| Dockerfile + prod compose + Caddy route | ⬜ Phase 5 | single route, no bus/wall split |

Event wire convention (unchanged from the wall): the emitted event's identity
is `data.type` (`chat_receive`, `ai_emission`, …), delivered as SignalR
method `"event"`; `from` + `sent_at` are stamped server-side. Receivers
ignore unknown types.

## 8 · Known limitations (spike stage)

* Per-service group scoping (plan R4) is implemented — `GroupPartitioner`
  namespaces every logical group (and every user identity) behind the
  caller's `client_id`; cross-partition publishes are inherently isolated
  and e2e-gated (PART-1..4, mock mode). The group service (plan §5.4) is
  live: `POST /api/groups` + members ops with stored, reconnect-surviving
  membership (e2e G-1..G-4 + GATE-2b). Remaining: group listing/counts,
  presence, broadcast (Phase 2).
* In-memory registry + root hub logging only: single process, no metrics.
* The publisher scope lane's 403 negative test runs only in mock mode (the
  real IdP's `speedtest` client carries the publisher scope unconditionally).
* No rate limiting, no history, no persistence — deliberately out of
  Phase-0 scope.

## 9 · Deployment shape (target, Phase 5)

* One container; single reverse-proxy route (`handle_path /websockets/*` —
  name TBD) to one upstream. No bus/wall split, no Cloudflare-Access
  prefix scoping traps, no file bind-mounts.
* Rotating `CONN_TOKEN_SECRET` invalidates live connection tokens (clients
  re-negotiate: the IdP path remains untouched → no IdP rotation impact).
* The push-deploy story (gh_webhooks) stays as documented in the sibling
  repo's `DEPLOY.md`, pointed at this repo's GHCR image.
