# CONTRACT — realtime server API, v1 (frozen 2026-11-07)

The pinned wire contract for app backends (publishers) and — for the bus
half — SPA/mobile clients. Machine-readable: `docs/openapi-v1.json`
(snapshot) or live `GET /openapi/v1.json` (served by the process).
Client-side recipes/behaviors: `docs/INTEGRATION.md`. Server internals:
`docs/TECH.md`.

Freezing rule: additive changes extend this doc + the snapshot; anything
breaking is a **v2** and waits for the Phase 4 SPA cutover risk window.
The bus side (SignalR hub) is governed by the SignalR protocol +
SERVER-side placement rules (§4) — it has no client-invocable methods.

---

## 1 · Security — the one scope key

* Every write: `Authorization: Bearer <IdP access token>` (RS256, verified
  against `AUTH_AUTHORITY`; issuer-only unless `AUTH_AUDIENCE` is pinned)
  **and** the caller's token must carry the publisher scope
  (`PUBLISH_REQUIRED_SCOPE`, default `api://ws-server/app:publisher`).
  Reads (`GET /api/groups*`) need only a valid bearer — no scope.
* The token's `client_id` is the ONE partition key (plan R4): it scopes
  groups, group listing, membership, and per-socket ops. It is never
  accepted from a body/query.
* Cross-service unicast (`/api/users/{sub}/send`) is the ONE deliberate
  exception — it addresses `user_channel|{sub}`, not a partition. (A
  future narrower `users:write` scope may split this; today it rides the
  publisher policy.)
* WS lane: negotiate (`Bearer` header handshake) issues a ≤5-min
  connection JWT — the *only* thing valid at the hub path. Clients never
  publish on the hub (no invocable methods exist).

## 2 · Error envelope (real status codes, plan R8)

Every non-2xx: `{ "error": "<code>", "detail": "<human hint>" }`
· 400 validation (`…_required`, `…_invalid`) · 401 invalid/missing bearer ·
403 valid bearer, wrong scope (`GATE-2`) or foreign connection
(`connection_not_in_partition`) · 404 unknown group/connection
(`group_not_found`, `connection_not_found`) · **429 rate_limited**
(added 2026-11-07, additive — see §2.1).

## 2.1 · Rate limiting (added 2026-11-07)

Fixed window per `client_id` partition (the one scope key again — anonymous
harness storms share the `anon` partition):

* **Two lane ceilings, one window** (`RATE_WINDOW_SECONDS`, default 60 s):
  * default lane (reads, publish/broadcast, group + members ops, disconnect):
    `RATE_MAX_PER_WINDOW` (default 60);
  * unicast send lanes (`POST /api/users/{sub}/send`,
    `POST /api/connections/{id}/send`): `RATE_SEND_PER_WINDOW` (default 600) —
    reserved for high-volume streams, notably **AI reasoning /
    chain-of-thought delivery** (batch deltas into reasonably sized chunks —
    e.g. per token-group, not per token).
* Rejections: HTTP 429 + `Retry-After` (seconds until the window rolls)
  + the standard error envelope with `error:"rate_limited"`.
* Envelope-wise the same for every endpoint; a caller's exhausted budget
  never affects another `client_id` (e2e RL-2/RL-4).
* Not reflected in `openapi-v1.json` (no path/response-schema changes), so
  the frozen snapshot stays byte-identical; OC-1 still drift-guards it.

## 3 · Event envelope (all sends delivered as SignalR method `"event"`)

```json
{ "type": "<discriminator>", …payload, "from": "<caller client_id>", "sent_at": "RFC3339 UTC" }
```
`from` + `sent_at` are server-stamped (overwrite whatever you send);
`from` = the CALLING service's client_id (provenance across partitions).
Receivers ignore unknown `type`s. Delivery is once per connected receiver
at send time — no store-and-forward (history buffer: Phase 3).

## 4 · Endpoints (implemented 2026-11-07, e2e-gated)

| Method & path | Auth | Request | 200/201 response | Errors |
|---|---|---|---|---|
| `POST /api/publish` | Bearer+scope | `{group, data}` | `{ok, group, partition}` | 400 group_required/data_required/data_invalid |
| `POST /api/broadcast` | Bearer+scope | `{groups[], data}` | `{ok, partition, groups_delivered[], unknown_groups[]}` | 400 groups_required/data_… |
| `POST /api/users/{sub}/send` | Bearer+scope | `{data}` | `{ok, user_id}` | 400 data_… |
| `POST /api/connections/{id}/send` | Bearer+scope | `{data}` | `{ok, connection_id, connection_partition}` | 400 data_… · 404 connection_not_found · 403 connection_not_in_partition |
| `POST /api/connections/{id}/disconnect` | Bearer+scope | — | `{ok, connection_id, connection_partition, aborted}` | 404/403 same scoping |
| `POST /api/groups` | Bearer+scope | `{name, history?, history_size?, history_ttl_seconds?}` | `201 {ok, created:true, group, partition, history_enabled}` · `200 {…}` (idempotent; re-post with `history:true` enables/retunes) | 400 group_name_required |
| `POST /api/groups/{g}/members` | Bearer+scope | `{user_id}` | `{ok, group, group_partition, member{user_id,client_id}, connections_joined}` | 404 group_not_found |
| `DELETE /api/groups/{g}/members?user_id=` | Bearer+scope | — | `{ok, group, group_partition, member{…}, removed}` | 404 group_not_found |
| `GET /api/groups` | Bearer | — | `{ok, partition, groups:[{group, members, online}]}` | — |
| `GET /api/groups/{g}/presence` | Bearer | — | `{ok, group, group_partition, presence:[{user_id, client_id, online, connections}]}` | 404 group_not_found |
| `GET /api/groups/{g}/history?limit=` | Bearer+scope | `?limit` (1..10 000, default 100) | `{ok, group, group_partition, history_enabled, total, history:[event…]}` — ascending tail over non-expired entries | 404 group_not_found |
| `DELETE /api/groups/{g}/history` | Bearer+scope | — | `{ok, group, group_partition, cleared}` (disabled buffer → `cleared:0`) | 404 group_not_found |
| `GET /healthz` / `GET /openapi/v1.json` | none | — | config probe / spec | — |
| `GET /metrics` | none | — | Prometheus text format (0.0.4) — HTTP + runtime collectors (`prometheus-net`) | — |

(Added 2026-11-07: `/metrics` is deliberately NOT part of the frozen
OpenAPI snapshot — it is a scrape surface in Prometheus text format
versioned by prometheus-net, not a JSON-API path; OC-1 guards the ten
documented paths and stays green. Expose the unauthenticated scrape
endpoint only on the internal network.)

Scoping laws, one line each:

* **Partitioned (caller's `client_id` prepend)** — publish, broadcast,
  groups create, members add/remove, listing, presence, per-socket
  send/disconnect. A service sees & reaches only its own world; per-socket
  ops additionally verify the target socket belongs to it (404/403).
* **Global (the exception)** — `POST /api/users/{sub}/send` addresses the
  user in every connection they hold, through any service. `sub` is the
  only address; membership does NOT change this lane.

## 5 · Connect-time placement (hub side, automatic)

* Stored membership is re-applied at connect and every reconnect (no
  backend re-adds needed after reconnects; add/remove ONLY when membership
  should actually change).
* Every connection also joins `user_channel|{sub}` (the unicast lane).
* Membership rows persist through disconnects — presence `online` flips,
  `members` stays — until explicitly removed via DELETE members.

## 6 · Contract status / provenance

* Snapshot generated from live introspection (`AddOpenApi`) of the running
  process (9 paths) — committed as `docs/openapi-v1.json`.
* Negative behaviors are e2e-gated (see TODO.md lanes: mock 42/42,
  real 32/32 at freeze time) — a contract claim without a check is a bug.
* Known deferred by design: publish-to-unknown-group stays lenient until
  the strict group-service enforcement; non-history groups record nothing
  (opt-in is the contract, not an implementation detail).
