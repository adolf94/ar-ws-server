# TODO — realtime repo, live state

> This file is the session-state document: what was decided in the working
> conversations, what landed in code, what's open. Update it at the end of
> every working session (the `/compact` project command does this).
> Architecture decisions live in `../centrifugo/docs/PLAN-OWN-SERVER.md`;
> implementation facts live in `docs/TECH.md`. Final e2e state: mock lane
> 54/54, real lane 42/42, edge rehearsal 54/54.

---

## Checklist

### Phase 0 — spike (plan §8) — DONE

- [x] Scaffold repo `realtime` (docs/, dev/, src/Realtime/); server on
  net10.0: `ar-idp` (Ar.Auth.OpenId) + `realtime-conn` (HS256) auth schemes,
  security-fence middleware (negotiate auth + `accessToken` injection),
  connection-token service (`sub, client_id, scope, iat, jti`, TTL ≤ 5 min),
  `SubUserIdProvider` + in-memory connection registry, `RealtimeHub`,
  `POST /api/publish` (publisher scope, `data.type` envelope,
  `from`/`sent_at`), `GET /healthz`.
- [x] `Ar.Auth.OpenId` from the local NuGet feed (net8 asset on net10 works);
  mock IdP + OIDC discovery doc added as the offline fallback lane.
- [x] e2e re-scoped per owner to server/WS tests only — token from
  `IDP_TOKEN` env or `.idp-token-cache.json`; authority touched only when
  nothing usable exists (`dev/e2e/mint-token.mjs` refreshes the cache).
  Fixed en-route: both cache writers stored claims but never the raw
  `access_token`, so `e2e.mjs`'s cache-hit path read `undefined` — cache
  file is now self-sufficient; `.idp-token-cache.json` (a live bearer
  token!) is in `.gitignore`. Final: **6/6 PASS**.
- [x] First commit `e69a2f5` (24 files; no token cache / .env staged);
  `dev/run-spike.sh` real-mode smoke green from a fresh server on :5000.
- **Security warning (fixed but worth remembering)**: the WS gate originally
  checked `HttpContext.WebSockets.IsWebSocketRequest`, which reported
  *false* for a perfectly valid node-client upgrade → the gate was skipped
  and the un-`[Authorize]`d hub accepted an anonymous connection; the e2e
  WS-2 check false-passed around it. The gate now matches the explicit
  upgrade headers; negatives GATE-1c/1d (upgrade w/o token / garbage token)
  make WS-2 a real assertion.

### Phase 1 — registry-driven group membership + per-service scoping (R4) — DONE 2026-11-07

- [x] `IGroupPartitioner` (`Groups/GroupPartitions.cs`) namespaces groups
  and user identities behind the verified bearer `client_id`;
  `IGroupRegistry` (`Connections/GroupRegistry.cs`) stores groups +
  memberships (reconnect-surviving).
- [x] Endpoints: `POST /api/groups` (auto-scoped, idempotent 201/200),
  `POST/DELETE /api/groups/{g}/members` (live sockets joined/evicted
  immediately), `GET /api/groups` (partition-private listing + persisted
  `members` count + `online`).
- [x] Owner spec (§Follow-up 4): the member body carries only `user_id` —
  the bearer's `client_id` scopes BOTH the group and the member (closed
  world: a service manages only its own users); cross-service fan-out stays
  `user_channel|{sub}`.
- [x] Hub applies stored membership at connect (spike demo auto-join
  removed). e2e: G-1..7, GATE-2b, PART-1..4 (partition gates exist in the
  mock lane only — only mock mints two different `client_id`s).

### Phase 2 — presence/unicast/contract freeze

- [x] Send-to-user `POST /api/users/{sub}/send { data }` — cross-service
  unicast, routes to `user_channel|{sub}` (joined at connect), so `sub`
  alone reaches the user on every device through ANY service's `client_id`;
  deliberately NOT partition-scoped. Same envelope
  (`data.type` + server-stamped `from` = caller's `client_id`, `sent_at`);
  publisher policy governs for now (narrower `users:write` scope is a
  recorded one-line change later). e2e SEND-1..4 incl. cross-service
  delivery, GATE-3/3b.
- [x] Send-to-connection `POST /api/connections/{id}/send { data }` —
  partition-scoped per owner: resolves the socket via the registry's
  `connectionId → identity` reverse index, decodes the target's partition
  via `IGroupPartitioner.IdentityPartition`, then 404 unknown / 403 foreign
  (`{error:"connection_not_in_partition"}`). Two unicast lanes, two
  different powers. e2e SEND-5/6, GATE-4/4b.
- [x] Broadcast `POST /api/broadcast { groups[], data }` — ONE wire pass
  (`Clients.Groups`) across caller-partition-scoped group names; unknown
  groups deliver to zero but are REPORTED in `unknown_groups` (leaks
  nothing — only the caller's own partition is inspected);
  `400 groups_required` on empty. e2e BC-0..3, GATE-5.
- [x] Presence `GET /api/groups/{g}/presence` — rows decoded from physical
  keys via `IGroupPartitioner.IdentitySubject`; membership persists across
  disconnects (identity row stays, `online` flips false — D-4). Unknown
  group → 404. e2e P-1.
- [x] Disconnect `POST /api/connections/{id}/disconnect` — same 404/403
  scoping; `IHubContext` cannot abort sockets →
  `Connections/ConnectionAborts.cs` tracks each connection's
  `HubCallerContext` at connect. e2e D-1..D-5.
- [x] §4 contract freeze + OpenAPI (Phase 2 exit gate): built-in
  `Microsoft.AspNetCore.OpenApi`, `/openapi/v1.json` live; frozen snapshot
  `docs/openapi-v1.json` (now **10 paths**); human contract
  `docs/CONTRACT.md` (security law, error/envelope law, endpoint matrix,
  partition-vs-global scoping one-liners, versioning rule: additive →
  extend; breaking → v2 at SPA cutover). e2e OC-1 anchors the freeze
  (red-on-drift) — it went red when the history route landed pre-freeze.
- [ ] **Open: `info` endpoint** (remaining §5.3 gap).

### Phase 3 — history, rate limiting, metrics, container — DONE 2026-11-07

- [x] History ring buffer (`Connections/HistoryBuffer.cs`): per-partition-
  group ring (≤10 000, default 100) + optional TTL applied lazily on read;
  publishes/broadcasts append the stamped envelope verbatim (cloned node per
  ring); unicast lanes deliberately unrecorded. `POST /api/groups` gains
  `history`/`history_size`/`history_ttl_seconds` (idempotent enable/retune);
  `GET /api/groups/{g}/history?limit=` returns ascending tail + live
  `total`; DELETE clears → `cleared:n` (disabled → 0). e2e H-1..H-6 incl.
  opt-in respected.
- [x] Rate limiting (CONTRACT §2.1, the 429 story): ASP.NET built-in
  `AddRateLimiter`/`FixedWindowRateLimiter` (no new package); **fixed window
  per `client_id`** — the fence eagerly best-effort-authenticates ANY
  request with an `Authorization` header so the limiter partitions on the
  VERIFIED `client_id` before the endpoint runs (anonymous → the `anon`
  partition). Two lane ceilings in one window (`RATE_WINDOW_SECONDS`,
  default 60 s): `partitioned` = `RATE_MAX_PER_WINDOW` (60: reads,
  publish/broadcast, group + members ops, disconnect) and
  `partitioned-send` = `RATE_SEND_PER_WINDOW` (600, on the two unicast
  lanes). Motivation: AI reasoning streaming — per-chunk send lands
  hundreds of calls/minute; a naive global budget would 429 mid-stream.
  Endpoint-level `EnableRateLimiting` metadata REPLACES the group-level one
  (nearest metadata wins — no stacking). 429 →
  `{error:"rate_limited", detail}` + `Retry-After`.
  e2e RL-1..4 — **must run LAST, after OC-1** (tripping the window must not
  poison other gates); snapshot unchanged, OC-1 stays green.
- [x] Prometheus metrics: `prometheus-net.AspNetCore` 8.2.1, middleware
  order = after the fence, before UseRateLimiter; `app.UseHttpMetrics()`
  (HTTP + runtime collectors) and `app.MapMetrics("/metrics")`. Placement:
  `/metrics` is OUTSIDE the `/api` group → no bearer, no publisher scope,
  NOT rate-limited (a scraper must never consume a client's budget),
  deliberately not in the frozen OpenAPI snapshot (scrape surface is
  prometheus-text-versioned, not a JSON-API path) → OC-1 green without
  re-freeze. Deploy: bind the scrape endpoint to the internal network only
  (Phase 5 compose). e2e MC-1 (before OC-1).
- [x] Containerization (Phase 3 exit): `Dockerfile` multi-stage
  (sdk:10.0 build → aspnet:10.0 runtime; dependency layer cached off
  `Realtime.csproj`); vendored feed restore — `nuget-feed/` (gitignored,
  staged by `dev/build-image.sh` from `../local-nuget`) +
  `docker/NuGet.docker.config` override. `docker-compose.yml`: one service,
  the full 12-factor env surface (TECH §5), restart policy, healthcheck
  `curl /healthz` (runtime base ships no wget — curl installed in the
  runtime stage); YAML trap: `${VAR:?msg}` with colons must be quoted.
  `CONN_TOKEN_SECRET` is the only secret, compose-required.
  OC-1 passed unchanged from inside the container; real-lane e2e against
  the container: **42/42**.

### Phase 4 — client libraries (redirected) — CLOSED

Owner decision: **NO SPA migration** (`../centrifugo` SPA stays). Phase 4 =
client libraries for easy connection to this microservice, all behind
Cloudflare → every lane customizes the User-Agent (never SDK-default
bot-suspicious identities). Token plumbing rides
`ar-auth-client-(js/dotnet/python)` — libs take factories/adapters, never
mint IdP credentials themselves. Owner role split: JS consumes, .NET/Python
publish.

- [x] `clients/ts` — `@adolf94/realtime-client` (commit `a8e25ed`): hub
  subscription wrapper. Verified in installed @microsoft/signalr source:
  `options.headers` are merged LAST on negotiate and forwarded to the ws
  constructor → Node lanes override the stock UA on BOTH negotiate and WS;
  browsers keep the native UA + `X-SignalR-User-Agent` override
  (CORS `AllowAnyHeader` already permits it). Envelope router
  (type-routed handlers + onAny + unknown-frame ignore +
  subscriber-exception containment); tokenFactory re-invoked per
  reconnect. 12 conformance gates: ALL PASS.
- [x] `clients/dotnet` — `Ar.Realtime.Client` 0.1.0-local (packed to the
  local feed): full publisher surface (`IRealtimePublisher`), envelopes as
  `JsonObject` with client-side `data.type` enforcement,
  `RealtimeApiException` maps the error envelope verbatim (429 carries
  `RetryAfterSeconds`), `ArAuthAccessTokenProvider` wraps
  `Ar.Auth.OpenId.ClientCredentialsAsync` (cached until ExpiresIn−60 s).
  Server fix surfaced by the suite: `GroupRegistry.Create` always returned
  `created=true` (`GetOrAdd` bool never captured). 8/8.
- [x] `clients/python` — `ar-realtime-client` (uv project, httpx), version
  **0.1.2** (0.1.2 bump uncommitted in the tree at HEAD `ebbcfbc`; 0.1.1 =
  token-provider fix + hardening `6542156`): publisher
  mirrors dotnet; fix `6542156` —
  `RealtimePublisherOptions` NameErrored on plain-callable token providers
  in the 0.1.0 wheel (`CallableTokenProvider` not imported in
  publisher.py); hardening: garbage raw dict/str providers now raise
  `RealtimeValidationError` up front (bare `hasattr(x,'get')` passes
  dicts → garbage bearer). `ArAuthTokenProvider` (ar_auth-coupled) accepts
  any client_credentials-shaped object (lazy); pypi `ar-auth-client` does
  NOT exist → the sibling SDK is an editable-install/factory decision for
  the consumer, never a hard dep. Suite 22/22.
- [x] Coalesced streaming — single-flight unicast sending in BOTH
  publishers, so AI reasoning-stream producers (bounded by publish RTT, not
  model speed; a 429 silently drops chunks) batchify without
  re-implementation. Contract (CONTRACT §3.1, "pick one" decision):
  batch envelope `{ "type": <stream type>, "batch": [chunk…verbatim],
  "batch_v": 1 }` — outer `type` = stream identity (first chunk's); chunks
  keep their own type/payload; ONE rate-limit unit + history row per
  flush; backward-compatible (consumers ignore `batch` until they opt in).
  * python: `publisher.coalesced_send_to_user(sub, max_pending_chars=…)`
    → `CoalescedSendHandle` (`coalescing.py`): daemon worker lane;
    `offer()` = validate (CONTRACT §3 eager) + lock+append, never touches
    HTTP; flush fires on response land (no timers — batch ≈ gen_rate ×
    RTT, so the 600/min send window now sees RTT-paced flushes instead of
    per-chunk posts); 429 → honor Retry-After, re-send the SAME batch once,
    second 429 → drop + record, stream continues; `drain()` flushes the
    remainder + propagates the last error exactly once; budget sheds
    OLDEST pending chunks (freshest reasoning wins); worker try/finally
    guarantees `drain()` never hangs on a dead lane. Live gate: 500 real
    chunks through the 600/60s lane.
  * Per-TAB lane (both SDKs, same handle — shipped in the tree, uncommitted
    at HEAD `ebbcfbc`): `coalesced_send_to_connection(id)`
    / `CoalescedSendToConnection(id, CoalesceOptions?)` (+ `IRealtimePublisher`
    member) — `POST /api/connections/{id}/send`, identical envelope +
    semantics; SEPARATE handles per TAB keep independent single-flight windows
    and orderings (TAB 1 vs TAB 2 CoT streams never share a flight). Closed or
    foreign ids surface as the NON-429 drop path (404 `connection_not_found`
    / 403 `connection_not_in_partition`): batch drops, counted, stream
    continues, drain raises the last error once — no same-batch retry on
    non-429 (gated). Discovery is producer-architecture: the SPA knows its
    own connection id and passes it at job start (out of client scope);
    multi-tab rate budget vs the 600/60s window is owner-managed.
  * dotnet: API parity — `IRealtimePublisher.CoalescedSendToUser(sub,
    CoalesceOptions?)` → `ICoalescedSendHandle`
    (`Offer`/`CompleteAsync`/`Snapshot`, `CoalesceStats` record, identical
    semantics; async-native LongRunning lane instead of a daemon thread).
    Two real fixes found by the gates: `EnsureEnvelope` made internal
    (shared with Offer's eager validation) and envelope CLONE per send
    attempt — JsonNode parenting made a reused retry envelope throw "node
    already has a parent" (python dict payload has no such graph).
  * Server touches: NONE (rate-limit math unchanged).
- [x] Harness: `bash dev/test-clients.sh [ts|dotnet|python]` — boots the
  mock lane, then runs each lib's suite against it (self-skipping tests
  stay green without a stack).
- [x] Client publish pipelines (CI, per commit `b55fa88`):
  `.github/workflows/publish-clients-{npm,dotnet,python}.yml` — manual
  dispatch; npm/dotnet → GitHub Packages, python → GitHub Releases; all
  three green (dotnet `42378560729`, npm `42378389739`,
  python `42377950688`). Dotnet publishes via the generated `github` NuGet
  source (`c0325f5`); npm uses `npm install` (lockfile is gitignored,
  `6eefb54`, `0a01a05`).
- **Known gap (candidate follow-up, NOT fixed)**: bare `dotnet test` (no
  `SRV`) NREs in PublisherConformanceTests — the file comment claims
  "self-skips" but an xunit `IAsyncLifetime.InitializeAsync` early-return
  does NOT skip; suites are green via `dev/test-clients.sh` / `SRV`-gated
  runs (python relies on pytest.skip).
- [x] INTEGRATION.md rewrite — done: `docs/INTEGRATION.md` is now the full
  developer integration guide (negotiate/WS flows, token lifetimes, publish
  envelopes, group/rate-limit recipes; cross-refs CONTRACT/TECH), rewritten
  at `1a93f93` and condensed at `ebbcfbc`.

### Phase 5 — deploy compose + Caddy single route; decommission the wall

- [x] `deploy/docker-compose.yml` — prod/gh_webhooks target mirroring the
  wall's deploy shape: `IMAGE` defaults to
  `ghcr.io/adolf94/realtime:latest` (CI pins `sha-<short7>` tags and the
  deploy POSTs them), NO host ports, explicit env map, healthcheck,
  internal `realtime` net + external `proxy` net (central Caddy).
- [x] `deploy/caddy-route.caddy` — ONE public prefix (`/realtime-v2`,
  plan §7.4) preserving the service's canonical paths: single `handle_path`
  strip + explicit deny for `/metrics`, `/openapi`, `/healthz` (ops door
  internal-only). Cloudflare Access scoping warning carried over from the
  wall compose.
- [x] Edge rehearsal PROVEN locally (`dev/edge-rehearsal.sh` — caddy:2
  replica + PROD-shaped realtime container): the FULL e2e suite drove only
  the edge (`SRV=edge`, `EDGE=/realtime-v2`, `SRV_OPS=` internal ops door)
  — **54/54 PASSED**; edge surface check → metrics/openapi edge-404.
  `dev/e2e/e2e.mjs` gained `EDGE`/`SRV_OPS` envs (direct mode unchanged;
  metrics+openapi gates always hit the ops door). Iss discipline: token
  `iss` is compared VERBATIM against the container's `AUTH_AUTHORITY` — the
  mock IdP now mints with the
  `host.docker.internal:9001/realms/demo` issuer string on all legs.
- [x] CI pipeline (owner-authoritative, committed `9e895a9`):
  `.github/workflows/deploy-gh-webhooks.yml` — build job (stage vendored
  feed **from GitHub Packages**: `Ar.Auth.OpenId` fetch of the exact pin →
  buildx → GHCR `sha-<short7>`, main-only) + deploy job (signed gh_webhooks
  POST: `{repo, tag}`, `X-Timestamp`+`X-Signature` HMAC over `<ts>.<body>`,
  CF Access headers, hardcoded `https://apis.adolfrey.com/gh_webhooks/d`).
  GHCR naming convergence: CI also aliases the manifest onto
  `ghcr.io/adolf94/realtime:{sha-*,latest}` via
  `docker buildx imagetools create` (server-side retag, no rebuild); repo
  stays `ar-ws-server`. csproj pins: **Ar.Auth.OpenId 1.2.1**
  (server + dotnet client; `local-nuget` carries the nupkg — upstream
  `ar-oauth2`'s `publish-dotnet-client.yml` publishes it).
- [x] **First green runs happened (verified via `gh run list`) — the
  owner-side prerequisites below are satisfied**:
  1. GitHub Packages: `adolf94/ar-ws-server` read access on the
     `Ar.Auth.OpenId` package (GITHUB_TOKEN of another repo cannot read it
     by default — the stage step's fail message says exactly this).
  2. Repo secrets: `CF_ACCESS_CLIENT_ID`, `CF_ACCESS_CLIENT_SECRET`,
     `HOMELAB_WEBHOOK_SECRET` (values match the wall's gh_webhooks setup).
  3. gh_webhooks `deploy_config.json`: whitelist
     `"adolf94/ar-ws-server": {"dir": "/srv/deploy/realtime",
     "image": "ghcr.io/adolf94/realtime"}`.
  4. Deploy host: `/srv/deploy/realtime/` = deploy/docker-compose.yml +
     `.env` (`CONN_TOKEN_SECRET`; optional `ALLOWED_ORIGINS`).
  Evidence: deploy-gh-webhooks success on main at 2026-11-08 (run
  `42448393894`, commit `ebbcfbc`); GHCR aliases resolve on
  `ghcr.io/adolf94/realtime:{sha-*,latest}`.
- [ ] Central-Caddy paste + wall decommission happen on the deploy host
  (apis host; NOT verifiable from this repo — open until owner confirms).
  Demo SPA still rides `/websockets/*` → wall cutoff is an owner decision
  after burn-in.

---

## Session log — condensed

Decisions and gotchas not captured by the checklist, in order:

### 2026-11-07 — plan + spike

- Plan `../centrifugo/docs/PLAN-OWN-SERVER.md`: options A–E, reasons ledger
  R1–R10; the wall's *reasons* preserved, never its code. Old
  `POST /realtime/token` mint endpoint NOT carried over (unused).
- Stack: **Option A — ASP.NET Core SignalR** (mobile later via official
  .NET/Java/Swift clients; Dart-first would have flipped to Socket.IO).
- Trust model: **negotiate-issued connection token** (plan §5.2) — the IdP
  token rides only the negotiate `Authorization` header; the WS query
  string carries a ≤5-min in-process-signed connection JWT. Direct-IdP
  demoted to the drawer fallback; custom ticket endpoint rejected.
- Auth wire mechanics (doc'd in TECH §2/§3): negotiate `accessToken` is
  consumed; Node transports (per installed @microsoft/signalr@10 source)
  send it as the handshake `Authorization` header, browsers as
  `?access_token=` (browser WS API cannot set headers; path-scoped server
  extraction).
- Real IdP `https://auth.adolfrey.com/api/` for the spike — real
  `client_credentials` tokens via the `speedtest` M2M client; mock IdP kept
  as offline fallback. e2e scope: server/WS tests, not authority tests.
- Cross-repo fix: `ar-auth` **1.2.0-local** packed — `ArAuthClient` relaxes
  `HttpDocumentRetriever.RequireHttps` for `http://` authorities (the mock
  IdP lane was dead-on-arrival with IDX20108). Superseded by the 1.2.1 pin.
- Phase-0 hardening decisions: e2e cache checks reject tokens whose `iss` ≠
  current `AUTH_AUTHORITY` (mock vs real lanes no longer
  cross-contaminate); `dev/run-spike.sh` exports
  `IDP_CLIENT_SECRET/SCOPES` so real-mode fresh mint works (earlier greens
  rode on the warm cache), and kills stale :5000/:9001 listeners before
  boot (bit us twice across lanes).
- Diagnostic logs reduced (dispatch → Debug; claim dump → Debug).

### 2026-11-07 — partitioning + unicast design

- Per-service scoping (plan R4) covers groups AND connections: group keys
  encode `client_id|logical` (URL-encoded, `|` separator per owner);
  `SubUserIdProvider` yields `client_id|sub`; hub connect-time placement
  also joins `user_channel|{sub}` (cross-service global user channel).
- **Reserved-name guard**: a token whose `client_id` == `user_channel` is
  boxed into the `server` partition.

---

## Environment caveats

- Legacy centrifugo **wall stack's `auth-server` container squats :9001** —
  `docker stop auth-server` (or `fuser -k 9001/tcp`) before the mock lane
  (restart for the wall).
- Real-IdP e2e credentials come from `../centrifugo/.env`
  (`AUTH_SERVICE_CLIENT_ID/SECRET/SCOPES`) — sourced by `dev/run-spike.sh`,
  never printed anywhere. Fresh session: warm the cache with
  `cd dev/e2e && node mint-token.mjs`, then re-run `node e2e.mjs`
  (server-only mode, no authority round-trip).
