# CONTRACT — realtime server API, v1.1 (v1 frozen 2026-11-07; addressing model amended 2026-11-08)

> **Purpose**: Authoritative wire contract specification for the Realtime Server HTTP API and SignalR bus.  
> **Audience**: Backend engineers implementing event publishers and frontend/mobile developers connecting to the bus.  
> **Usage**: Consult for exact endpoint signatures, request/response envelopes, HTTP status codes, partition scoping rules, and rate limits.

The pinned wire contract for app backends (publishers) and — for the bus
half — SPA/mobile clients. Machine-readable: `docs/openapi-v1.json`
(snapshot) or live `GET /openapi/v1.json` (served by the process).
Client-side recipes/behaviors: `docs/INTEGRATION.md`. Server internals:
`docs/TECH.md`.

Freezing rule: additive changes extend this doc + the snapshot; breaking
changes require **v2**. Server-side placement rules are in §4–§5.

---

## 1 · Security — the one scope key

* Every write: `Authorization: Bearer <IdP access token>` (RS256, verified
  against `AUTH_AUTHORITY`; issuer-only unless `AUTH_AUDIENCE` is pinned)
  **and** the caller's token must carry the publisher scope
  (`PUBLISH_REQUIRED_SCOPE`, default `api://ws-server/app:publisher`).
  Reads (`GET /api/groups*`) need only a valid bearer — no scope.
* The token's `client_id` is the partition key: it scopes groups, group listing,
  membership, and per-socket ops. It is never accepted from a request body or query.
* Cross-service unicast (`/api/users/{sub}/send`) is the one deliberate
  exception — it addresses `user_channel|{sub}`, not a partition.
* WS lane: negotiate (`Bearer` header handshake) issues a ≤5-min
  connection JWT — the only credential accepted at the hub path. Clients never
  publish on the hub (no invocable methods exist).

## 2 · Error envelope

Every non-2xx response follows: `{ "error": "<code>", "detail": "<human hint>" }`

* **400**: validation failure (`…_required`, `…_invalid`).
* **401**: invalid or missing bearer token.
* **403**: valid bearer with wrong scope (`GATE-2`) or foreign connection (`connection_not_in_partition`).
* **404**: unknown group or connection (`group_not_found`; `connection_not_found` — no such id in this process, `connection_not_connected` — id known but currently boundless).
* **429**: rate limited (`rate_limited` — see §2.1).

## 2.1 · Rate limiting

Fixed window per `client_id` partition (anonymous callers share the `anon` partition):

* **Two lane ceilings, one window** (`RATE_WINDOW_SECONDS`, default 60 s):
  * **Default lane** (reads, publish/broadcast, group + member operations, disconnect):
    `RATE_MAX_PER_WINDOW` (default 60).
  * **Unicast send lanes** (`POST /api/users/{sub}/send`, `POST /api/connections/{id}/send`):
    `RATE_SEND_PER_WINDOW` (default 600) — sized for high-volume streams such as AI reasoning and chain-of-thought delivery (deltas should be batched per token-group, not per token).
* **Rejections**: HTTP 429 + `Retry-After` (seconds until window reset) + `{ "error": "rate_limited", "detail": "…" }`.
* Rate limits are isolated per partition; an exhausted budget on one `client_id` never impacts another.
* Rate limiting behavior does not alter JSON response schemas in `openapi-v1.json`.

## 2.2 · Addressing model (v1.1)

Three addresses with three lifetimes:

| Address | Scope | Lifetime | Reached via |
|---|---|---|---|
| **`socket_id`** | one live transport socket | dies with the socket; **changes on every reconnect** (each reconnect re-negotiates) | never — server-internal (logs/diagnostics only) |
| **`connection_id`** | one logical client instance | survives client reconnects; dies on server restart (a new id is minted after) | `POST /api/connections/{id}/send`, `POST /api/connections/{id}/disconnect`, `GET /api/users/{sub}/connections` (own partition), and its connect-time envelope |
| **`user_id` (`sub`)** | the person, every tab/device at once | lives as long as the JWT identity | `user_channel\|{sub}`, `POST /api/users/{sub}/send` |

* `connection_id` is **server-minted (UUIDv7, time-ordered)** at first connect to
  `(client_id, sub, instance tag)` — clients never choose their own id, which
  keeps the address authoritative and uniformly sortable.
* The instance tag is the client's stable self-label (a UUID in localStorage /
  process env), carried as the `instance` query parameter on the hub URL —
  the hub reads it off the client's WS upgrade request.
  It is NOT security-relevant: identity and partitioning are fenced by the
  connection JWT; a forged tag only addresses a sibling socket of the same `sub`.
* Sockets NOT sending a tag get per-socket logical ids (fresh each reconnect —
  legacy one-socket semantics).
* **Reconnect rebind**: the same tag re-binds to the SAME `connection_id`; a
  stale socket still bound to it is superseded (server aborts it on bind).
* Persistence is deliberately out of scope for v1.1: ids survive **client**
  reconnects only. A server restart mints new ids — senders must treat
  `404 connection_not_found` as the cue to fall back to the user-channel lane.

## 3 · Event envelope (all sends delivered as SignalR method "event")

```json
{ "type": "<discriminator>", …payload, "from": "<caller client_id>", "sent_at": "RFC3339 UTC" }
```

* `from` and `sent_at` are stamped server-side, overwriting any client-supplied values.
* `from`: the calling service's `client_id` (preserves provenance across partitions).
* Receivers ignore unknown `type` discriminators.
* Delivery is once per connected receiver at send time (no store-and-forward unless history is enabled for the group).

### 3.1 · Client-side coalescing batch (`batch_v`, optional, publisher SDKs only)

The event envelope can carry several original events from one coalesced flush
(publisher SDKs — handover spec 2026-11-08: single-flight publishing so a
reasoning stream batchifies instead of stalling the producer on RTT).
Entry points: python `coalesced_send_to_user(sub)` / `coalesced_send_to_connection(id)`
(`ar-realtime-client`), .NET `CoalescedSendToUser(sub)` / `CoalescedSendToConnection(id)`
(`Ar.Realtime.Client`); the connection lane posts the same envelope to
`POST /api/connections/{id}/send`.

```json
{ "type": "<stream type>", "batch": [ <chunk1>, <chunk2>, … ], "batch_v": 1, "from": …, "sent_at": … }
```

* `batch` chunks ride VERBATIM (each keeps its own `type`/payload); the outer
  `type` (the stream identity) is the handle's first chunk's type under
  `batch_v: 1` and stays pinned for that stream's lifetime (both SDKs keep it
  in a once-set `stream_type` field).
* `batch_v: 1` keeps renderers forward compatible (new envelope additions
  would bump it).
* SDK flush discipline: at most ONE in-flight publish per handle — request N+1
  is sent only after N's response, so offer order is preserved and a retried
  batch can never reorder (PC-2/PC-3 tests); one request per flush carries
  everything pending at the flush boundary.
* Server-side this is ONE event: one envelope, one `from`/`sent_at` stamp, one
  rate-limit unit, and — when the group has history — one history row.
* Consumers replaying coalesced deltas loop over `batch` items exactly like
  individual "event" deliveries per item.

## 4 · Endpoints

| Method & path | Auth | Request | 200/201 response | Errors |
|---|---|---|---|---|
| `POST /api/publish` | Bearer+scope | `{group, data}` | `{ok, group, partition}` | 400 group_required/data_required/data_invalid |
| `POST /api/broadcast` | Bearer+scope | `{groups[], data}` | `{ok, partition, groups_delivered[], unknown_groups[]}` | 400 groups_required/data_… |
| `POST /api/users/{sub}/send` | Bearer+scope | `{data}` | `{ok, user_id}` | 400 data_… |
| `POST /api/connections/{id}/send` | Bearer+scope | `{data}` | `{ok, connection_id, connection_partition, delivered}` — fans to every socket bound to the logical id | 400 data_… · 404 connection_not_found / connection_not_connected (id known, zero live sockets) · 403 connection_not_in_partition |
| `POST /api/connections/{id}/disconnect` | Bearer+scope | — | `{ok, connection_id, connection_partition, aborted}` — `aborted` counts the sockets force-closed | 404/403 same scoping |
| `POST /api/groups` | Bearer+scope | `{name, history?, history_size?, history_ttl_seconds?}` | `201 {ok, created:true, group, partition, history_enabled}` · `200 {…}` (idempotent; re-posting with `history:true` tunes config) | 400 group_name_required |
| `POST /api/groups/{g}/members` | Bearer+scope | `{user_id}` | `{ok, group, group_partition, member{user_id,client_id}, connections_joined}` | 404 group_not_found |
| `DELETE /api/groups/{g}/members?user_id=` | Bearer+scope | — | `{ok, group, group_partition, member{…}, removed}` | 404 group_not_found |
| `GET /api/groups` | Bearer | — | `{ok, partition, groups:[{group, members, online}]}` | — |
| `GET /api/groups/{g}/presence` | Bearer | — | `{ok, group, group_partition, presence:[{user_id, client_id, online, connections}]}` | 404 group_not_found |
| `GET /api/users/{sub}/connections` | Bearer+scope | — | `{ok, user_id, partition, connections:[{connection_id, instance_tag, persistent, sockets}]}` — the caller's partition's logical connections for one user (the inventory for the per-connection lanes); ordered oldest-first (uuidv7). Unknown/foreign users → empty list (no existence leak) | — |
| `GET /api/groups/{g}/history?limit=` | Bearer+scope | `?limit` (1..10 000, default 100) | `{ok, group, group_partition, history_enabled, total, history:[event…]}` — ascending tail over non-expired entries | 404 group_not_found |
| `DELETE /api/groups/{g}/history` | Bearer+scope | — | `{ok, group, group_partition, cleared}` (disabled buffer → `cleared:0`) | 404 group_not_found |
| `GET /healthz` / `GET /openapi/v1.json` | none | — | config probe / spec | — |
| `GET /metrics` | none | — | Prometheus text format (0.0.4) — HTTP + runtime collectors (`prometheus-net`) | — |

> [!NOTE]
> `/metrics` is a scrape surface in Prometheus text format (0.0.4), not a JSON API path. Expose this unauthenticated endpoint only on an internal network.

### Scoping rules:

* **Partitioned (prepended with caller's `client_id`)**: Publish, broadcast, group creation, member management, listing, presence, and per-socket send/disconnect. Callers only reach and see resources within their partition; per-socket operations reject foreign sockets with 403.
* **Global**: `POST /api/users/{sub}/send` delivers to the user across all their active connections in any service.

## 5 · Connect-time placement (hub side, automatic)

* **Mint & bind (v1.1)**: on connect the hub performs the §2.2 mint/bind/rebind mechanics for this socket's logical `connection_id` (first-connect mint, same-tag reconnect re-bind, stale-socket supersede-abort), then hands the id over:
* **Id delivery**: the hub has no client-invocable methods (§1), so the id is handed over as a server-published envelope addressed to the just-connected socket:
  `{ "type": "connection_registered", "connection_id": "<uuidv7>", "socket_id": "<transport id>", "instance_tagged": <bool>, "superseded": <n>, "from": "server", "sent_at": … }`
* Stored membership is re-applied upon connect and on every reconnect. App backends do not need to re-add members after client reconnects.
* Every connection joins `user_channel|{sub}` (the global unicast lane).
* Group membership persists through disconnects: presence `online` flips to false while membership remains until removed via `DELETE /api/groups/{g}/members`.

## 6 · Contract status

* Publishing to an unknown group is accepted (delivers to zero subscribers).
* Non-history groups do not record messages (history is opt-in per group).