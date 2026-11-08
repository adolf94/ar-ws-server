# Realtime Server — Technical Documentation

> **Purpose**: In-depth architectural, operational, and implementation documentation for the Realtime Server codebase.  
> **Audience**: Core service maintainers, platform engineers, and DevOps/SREs.  
> **Usage**: Consult for server internals, security fence mechanics, component maps, environment configurations, local test execution, and container deployment.

---

## 1 · Architecture Overview

One process, one container, providing two distinct communication surfaces:

```
                     ┌──────────────── one .NET process (net10.0) ────────────────┐
 SPA / mobile ───────┼─ ► POST /realtime/hub/negotiate   [IdP bearer scheme]      │
                     │     ◄ { connectionId, accessToken (conn JWT, ≤5 min) }     │
                     │                                                           │
 SPA / mobile ───────┼─ ► GET /realtime/hub?access_token=…  [conn-token scheme]  │
                     │      SignalR hub — publications out; clients never publish│
                     │                                                           │
 app backends ───────┼─ ► POST /api/*          [IdP bearer + publisher scope]    │
                     │      server API — publish, groups, membership, history     │
 IdP (Ar.Auth) ── JWKS ─► verification anchor for every bearer lane              │
                     └───────────────────────────────────────────────────────────┘
```

* **The bus**: A SignalR hub protected by an upfront security fence (§3). At connect time, the hub joins every connection to `user_channel|{sub}` (cross-service unicast address) and all persisted partition-scoped groups.
* **The server API**: Minimal API invoking `IHubContext<RealtimeHub>` directly — zero external relays, returning standard HTTP status codes.
* **Single secret**: The in-process symmetric key for signing connection tokens. App backends require no realtime server secrets.

## 2 · Trust Model

Two authentication schemes by endpoint:

| Endpoint | Scheme (`AuthSchemes.cs`) | Identity Handled | Verification |
|---|---|---|---|
| `POST /realtime/hub/negotiate` | `ar-idp` | IdP access tokens (RS256) | `Ar.Auth.OpenId` (`IArAuthClient.VerifyTokenAsync` via discovery, JWKS, issuer check; header-only) |
| `GET /realtime/hub` (WS upgrade) | `realtime-conn` | Connection JWTs (HS256) | `Microsoft.AspNetCore.Authentication.JwtBearer` with in-process symmetric key (query `?access_token` or header) |
| `POST /api/*` | `ar-idp` + `publisher` policy | IdP bearer + publisher scope | `VerifyTokenAsync` + `PublisherScopeHandler` checking space-delimited `scope` |

### Negotiate-Issued Connection Tokens

1. The client sends its IdP access token in the `Authorization: Bearer` header on negotiate (`POST /realtime/hub/negotiate`). The IdP token never touches query strings or URLs.
2. The fence verifies the token against the IdP (`ar-idp` scheme) and executes SignalR's internal negotiate logic.
3. Middleware injects `accessToken` into the negotiate JSON payload: a **connection JWT** (`{ sub, client_id, scope, iat, jti }`, `iss=realtime`, `aud=realtime`, TTL ≤ 5 min via `CONN_TOKEN_TTL`), signed with the in-process HS256 secret.
4. The WebSocket handshake verifies this connection JWT. Browsers send it via `?access_token=`; native or Node transports send it via `Authorization: Bearer`.
5. On reconnect, the client re-negotiates with a fresh IdP token to receive a new connection token (`skipNegotiation` must remain false).

### Identity Scoping

* **Hub identity**: `Context.UserIdentifier` resolves to a `client_id`-scoped `sub` via `SubUserIdProvider`. This ensures each calling service's fan-out namespace is isolated.
* **Partition scoping**: The caller's `client_id` partitions groups and user identities via `GroupPartitioner`. A service manages only its own users and groups.
* **Global unicast**: Cross-service user messaging (`POST /api/users/{sub}/send`) addresses `user_channel|{sub}`, delivering to all active connections for that `sub` across all services.

## 3 · Security Fence (`Negotiate/SecurityFenceMiddleware.cs`)

A single middleware placed after `UseAuthentication`:

* **Negotiate (`POST /realtime/hub/negotiate`)**:
  1. Authenticates against `ar-idp`; non-authenticated calls return `401 {"error":"invalid_token"}`.
  2. Buffers SignalR's negotiate response body.
  3. On `200`, parses the JSON and sets `"accessToken" = connJwt`, adjusting `Content-Length`.
* **WebSocket handshake (`GET /realtime/hub` with upgrade headers)**:
  1. Matches explicit upgrade headers (`Connection: Upgrade` and `Upgrade: websocket`).
  2. Authenticates against `realtime-conn`; rejects invalid tokens with `401`.
  3. Assigns the principal to `context.User` before the hub accepts the connection.
* **All other endpoints**:
  1. Performs eager authentication on requests with an `Authorization` header to populate `context.User` before rate limiting and endpoint handlers execute.

## 4 · Component Map

| Path | Purpose |
|---|---|
| `src/Realtime/Program.cs` | Application entry point: configuration, auth schemes, CORS, SignalR hub, and API routes. |
| `src/Realtime/Auth/AuthSchemes.cs` | Scheme-name constants (`ar-idp`, `realtime-conn`). |
| `src/Realtime/Auth/ArAccessTokenHandler.cs` | IdP token validation and publisher scope authorization handler. |
| `src/Realtime/Auth/ConnectionTokenService.cs` | Mints and signs connection JWTs (HS256, TTL, claims). |
| `src/Realtime/Auth/PrincipalExtensions.cs` | Helper extensions to extract `sub` and `client_id` claims. |
| `src/Realtime/Auth/SubUserIdProvider.cs` | Maps connection principal to partitioned `Context.UserIdentifier`. |
| `src/Realtime/Groups/GroupPartitions.cs` | Encodes and decodes physical `client_id|logical` keys for groups and users. |
| `src/Realtime/Connections/ConnectionRegistry.cs` | In-memory registry tracking active connection IDs per user identity. |
| `src/Realtime/Connections/GroupRegistry.cs` | In-memory registry tracking group metadata, history buffers, and memberships. |
| `src/Realtime/Connections/ConnectionAborts.cs` | Tracks live `HubCallerContext` instances to support server-initiated disconnects. |
| `src/Realtime/Hubs/RealtimeHub.cs` | SignalR hub: manages connect/disconnect lifecycle and re-applies group memberships. |
| `src/Realtime/Negotiate/SecurityFenceMiddleware.cs` | Negotiate token injection and WebSocket handshake validation. |
| `src/Realtime/Api/RealtimeApiEndpoints.cs` | REST API routes for publish, broadcast, groups, members, presence, history, and disconnect. |
| `dev/mock-idp/server.js` | Standalone mock IdP server for offline local development and test automation. |
| `dev/e2e/e2e.mjs` | End-to-end integration and security test suite. |
| `dev/run-spike.sh` | Orchestration script to run server and E2E tests against real or mock IdP. |
| `Dockerfile` + `docker-compose.yml` | Multi-stage build and container deployment configuration. |

### 4.1 · Client packages (`clients/`)

| Package | Side | Notes |
|---|---|---|
| `@adolf94/realtime-client` (clients/ts) | consumer | hub subscription over negotiate-issued tokens; Cloudflare-safe UA. |
| `ar-realtime-client` (clients/python, 0.1.1) | publisher | full server-API surface (uv/httpx); **coalesced send**: `coalesced_send_to_user(sub)` — single-flight publishing (one in-flight request per stream, batches self-tune to `generation_rate × RTT`), `offer()`/`drain()` semantics, 429 → `Retry-After` honored with one same-batch retry then drop+record. |
| `Ar.Realtime.Client` (clients/dotnet, 0.1.0-local) | publisher | mirrors the python SDK, async-native: `CoalescedSendToUser(sub, CoalesceOptions?)` → `ICoalescedSendHandle` (`Offer` / `CompleteAsync` / `Snapshot`), identical semantics, `IRealtimePublisher` interface member. |

Both publishers implement the CONTRACT §3.1 batch envelope: a flush is ONE
`POST /api/users/{sub}/send` carrying `{type, batch, batch_v:1}` with the
original chunks verbatim — one rate-limit unit per flush instead of one per
chunk (the AI-reasoning token lane was the driving case; handover 2026-11-08).

## 5 · Configuration

| Variable | Default | Purpose |
|---|---|---|
| `AUTH_AUTHORITY` | `http://localhost:9001/realms/demo` (mock) / `https://auth.adolfrey.com/api/` (prod) | OIDC authority for discovery and JWKS verification. |
| `AUTH_AUDIENCE` | *(empty)* | Optional audience check. When empty, verification is issuer-only. |
| `ALLOWED_ORIGINS` | `http://localhost:3000,https://*.adolfrey.com` | Allowed CORS origins for browser clients. Wildcard `*` matches one subdomain label. |
| `PUBLISH_REQUIRED_SCOPE` | `api://ws-server/app:publisher` | Scope required on IdP tokens for write endpoints. |
| `CONN_TOKEN_SECRET` | *(ephemeral random in dev)* | HMAC secret key (≥32 bytes) for signing connection JWTs. |
| `CONN_TOKEN_TTL` | `300` | Connection JWT TTL in seconds (maximum 300 s / 5 min). |
| `RATE_WINDOW_SECONDS` | `60` | Duration of the fixed rate limit window. |
| `RATE_MAX_PER_WINDOW` | `60` | Max requests per window for the default API lane. |
| `RATE_SEND_PER_WINDOW` | `600` | Max requests per window for high-volume unicast send lanes. |
| `ASPNETCORE_URLS` | `http://localhost:5000` | Server listening URLs. |
| `ASPNETCORE_ENVIRONMENT` | `Production` | Runtime environment mode. Set to `Development` for detailed hub errors. |

`GET /healthz` echoes effective auth settings without exposing secret keys.

## 6 · Running and Testing

```bash
# Start server locally
cd src/Realtime
ASPNETCORE_URLS=http://localhost:5000 \
  AUTH_AUTHORITY=https://auth.adolfrey.com/api/ \
  CONN_TOKEN_SECRET=$(openssl rand -hex 32) \
  dotnet run

# Run end-to-end tests against real IdP
bash dev/run-spike.sh

# Run end-to-end tests against offline mock IdP
IDP_MODE=mock bash dev/run-spike.sh
```

The test suite validates negotiate token injection, WebSocket handshake authentication, publish/broadcast routing, group and membership persistence, history ring buffers, cross-service unicast isolation, 401/403 negative gates, and rate limiting.

## 7 · Public API Surface

| Endpoint | Method | Scope / Auth | Purpose |
|---|---|---|---|
| `/healthz` | `GET` | None | Health check and auth configuration echo. |
| `/openapi/v1.json` | `GET` | None | OpenAPI v1 specification document. |
| `/metrics` | `GET` | None | Prometheus runtime and HTTP metrics (text/plain 0.0.4). |
| `/realtime/hub/negotiate` | `POST` | Bearer (`ar-idp`) | Handshake negotiation and connection JWT issuance. |
| `/realtime/hub` | `GET` (WS) | Bearer (`realtime-conn`) | WebSocket bus connection. |
| `/api/publish` | `POST` | Bearer + Scope | Publish event to a single group. |
| `/api/broadcast` | `POST` | Bearer + Scope | Publish event across multiple groups in partition. |
| `/api/users/{sub}/send` | `POST` | Bearer + Scope | Global cross-service unicast send to a user. |
| `/api/connections/{id}/send` | `POST` | Bearer + Scope | Unicast send to a specific connection within partition. |
| `/api/connections/{id}/disconnect` | `POST` | Bearer + Scope | Forcibly close a connection within partition. |
| `/api/groups` | `POST` | Bearer + Scope | Create or configure a group (idempotent). |
| `/api/groups` | `GET` | Bearer | List groups, member counts, and online counts for caller's partition. |
| `/api/groups/{g}/members` | `POST` | Bearer + Scope | Add user to group (immediate join + persisted). |
| `/api/groups/{g}/members` | `DELETE` | Bearer + Scope | Remove user from group (immediate eviction + unpersisted). |
| `/api/groups/{g}/presence` | `GET` | Bearer | Retrieve active member presence and socket counts. |
| `/api/groups/{g}/history` | `GET` | Bearer + Scope | Query message history for opt-in groups. |
| `/api/groups/{g}/history` | `DELETE` | Bearer + Scope | Clear message history for a group. |

## 8 · Operational Characteristics & Constraints

* **In-memory state**: Group registry, active connection mappings, and history buffers are held in memory. State is reset on process restart.
* **Single-process scope**: Group and connection tracking operates within the local process instance.
* **Rate limiting**: Fixed-window counters are maintained in memory per `client_id` partition.
* **Metrics isolation**: `/metrics` is unauthenticated and unrate-limited to facilitate automated scraping; bind this endpoint to an internal network interface.

## 9 · Deployment

```bash
# Build Docker image
bash dev/build-image.sh

# Run container with production secret
CONN_TOKEN_SECRET=$(openssl rand -hex 32) docker compose up -d realtime

# Verify health status
docker inspect --format '{{.State.Health.Status}}' realtime
```

Rotating `CONN_TOKEN_SECRET` invalidates active connection JWTs and prompts clients to re-negotiate against the IdP. The IdP token and credentials are not affected by secret rotation.

##### Edge + wall cutover (Phase 5)

The central Caddy routes hang the whole service under one public prefix — the public URL is the prefix + the service's canonical paths, verbatim. `deploy/docker-compose.yml` (gh_webhooks target, `IMAGE=<sha>`, no host ports, homelab-wide `proxy` network) + `deploy/caddy-route.caddy` (paste into the `apis.adolfrey.com` server block): `/realtime-v2/*` → realtime:5000, while `/metrics`, `/openapi`, `/healthz` are denied at the edge (ops door stays on the internal docker network). Parallel-run first; the wall's `/websockets/*` handles stay until its consumers move.

Before touching the central Caddy, the route shape is proven locally: `bash dev/edge-rehearsal.sh` boots a caddy:2 replica + the realtime container and the **full e2e drives only the edge** (`SRV=<edge>, EDGE=<prefix>, SRV_OPS=<internal ops door>`); metrics/openapi return edge-404 when the rehearsal is correct.

On the deploy host: put image + secret in `/srv/deploy/realtime/.env` (gh_webhooks sets `IMAGE`; `.env` holds `CONN_TOKEN_SECRET`, optional `ALLOWED_ORIGINS` overrides), run `docker compose up -d`, paste the Caddy route, reload, verify with `curl -si https://apis.adolfrey.com/realtime-v2/healthz | head` (expect 404-public, edge denies) and a live negotiate through the prefix. Decommission of the wall (stop `centrifugo`/`proxy`, archive `CENTRIFUGO.md`) is the final step after burn-in and requires the wall's consumers to have moved off `/websockets/*` first — the demo SPA still rides the wall, so its cutoff is an owner decision.
