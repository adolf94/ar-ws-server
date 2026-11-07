# TODO — realtime repo, live state

> This file is the session-state document: what was decided in the working
> conversations, what landed in code, what's open. Update it at the end of
> every working session (the `/compact` project command does this).
> Architecture decisions live in `../centrifugo/docs/PLAN-OWN-SERVER.md`;
> implementation facts live in `docs/TECH.md`.

---

## Checklist

### Phase 0 — spike (plan §8)

- [x] Scaffold repo `realtime` (git init; docs/, dev/, src/Realtime/)
- [x] Server implemented (net10.0, builds clean)
  - [x] Two auth schemes: `ar-idp` (Ar.Auth.OpenId) + `realtime-conn` (HS256)
  - [x] Security fence middleware — negotiate auth + `accessToken` injection
  - [x] Connection-token service (`sub, client_id, scope, iat, jti`, TTL ≤ 5 min)
  - [x] `SubUserIdProvider` + in-memory connection registry
  - [x] `RealtimeHub` (lifecycle + spike demo-group auto-join)
  - [x] `POST /api/publish` (publisher scope, `data.type` envelope, `from`/`sent_at`)
  - [x] `GET /healthz` echo
- [x] `Ar.Auth.OpenId` 1.0.5-local wired from local NuGet feed (net8 asset on net10 works)
- [x] Mock IdP copied + OIDC discovery doc added (offline fallback lane)
- [x] **Real-OAuth e2e green: 9/9 PASS** (discovery → mint via real
  `client_credentials` (`speedtest` client) → negotiate shape (a) → WS
  handshake consumed the negotiate-issued token → publish → publication
  received with stamps → negative gates 401/403-coverage)
- [x] e2e split per owner request: **server/WS tests only** — token from
  `IDP_TOKEN` env or `.idp-token-cache.json`; authority touched only when
  nothing usable exists (`dev/e2e/mint-token.mjs` refreshes the cache)
- [x] `docs/TECH.md` written; README links it
- [x] `/compact` project command `.opencode/commands/compact.md` — session
  state → TODO.md + handoff log (shadows the built-in in this repo)
- [x] **Re-run e2e after the ws-only rewrite: 6/6 PASS** (2026-11-07; scope
  narrowed from 9/9 in pre-rewrite run to server/WS + gates only). Fixed
  en-route: both cache writers (`mint-token.mjs`, `e2e.mjs`) stored claims
  but never the raw `access_token`, so `e2e.mjs`'s cache-hit path read
  `undefined` — cache file is now self-sufficient, and `.idp-token-cache.json`
  (live bearer token!) added to `.gitignore`
- [x] Commit (first commit): `e69a2f5` "phase-0 spike: negotiate-issued
  tokens, real-IdP e2e green" — 24 files; no token cache / .env staged
- [x] `dev/run-spike.sh` full smoke green (real mode, fresh server on :5000,
  e2e 6/6). Caveat: script only auto-kills the IdP port in mock mode — port
  :5000 must be cleared externally first (see Next up)

### Next up

- [ ] `dev/run-spike.sh`: `fuser -k <SRV port>` in real mode too (currently
  only the mock-IdP port is cleared; a stale :5000 listener defeats the smoke)
- [x] Phase 1 core: registry-driven group membership + per-service scoping
  (plan R4) — **done 2026-11-07**: `IGroupPartitioner` namespaces groups and
  user identities behind the verified bearer `client_id`
  (`Groups/GroupPartitions.cs`); `IGroupRegistry` stores groups + memberships
  (`Connections/GroupRegistry.cs`); `POST /api/groups` (auto-scoped,
  idempotent), `POST/DELETE /api/groups/{g}/members` (body carries only
  `user_id` — bearer's `client_id` scopes BOTH the group and the member,
  owner spec); hub applies stored membership at connect (no auto-join).
  Connect-time + runtime verified: e2e G-1..4, GATE-2b, PART-1..4 —
  mock lane 18/18, real lane 13/13. Remaining Phase 1 polish: group
  listing/counts endpoint (data exists in `IGroupRegistry`).
- [ ] Phase 2: presence/info endpoints; full contract + OpenAPI.
  §5.3 unicast gap **closed 2026-11-07**:
  ✅ send-to-user (`POST /api/users/{sub}/send`, user_channel, e2e SEND-1..4
  incl. cross-service delivery) and ✅ send-to-connection
  (`POST /api/connections/{id}/send`, partition-scoped per owner —
  e2e SEND-5/6, GATE-4/4b; registry gained a `connectionId → identity`
  reverse index, `IGroupPartitioner.IdentityPartition` decodes the target's
  partition). Also ✅ 2026-11-07: broadcast (one wire pass, unknown groups
  reported) + `GET /api/groups` (partition-private, members/online counts)
  — e2e BC-0..3, G-5..G-7 + presence (`GET /api/groups/{g}/presence`,
  decoded identity rows) + disconnect (`POST /api/connections/{id}/disconnect`,
  same 404/403 scoping, registry-backed `HubCallerContext` aborts)
  — e2e P-1, D-1..D-5. Remaining §5.3: history read/clear (Phase 3
  buffer behind it), `info` — and OpenAPI/contract freeze.
- [x] §4 contract freeze + OpenAPI (Phase 2 exit gate, plan §8) — done
  2026-11-07: built-in OpenAPI wired (`AddOpenApi`/`MapOpenApi`,
  `/openapi/v1.json` live); snapshot `docs/openapi-v1.json` (9 paths);
  human contract `docs/CONTRACT.md` (security law, envelope, endpoint
  matrix, scoping one-liners, versioning rule); e2e OC-1 gates the live
  doc against the snapshot — **mock 43/43, real 33/33**
- [ ] Phase 3: history ring buffer (**done 2026-11-07**:
  `Connections/HistoryBuffer.cs`; `POST /api/groups` gains
  `history`/`history_size`/`history_ttl_seconds`; publish/broadcast append;
  `GET/DELETE /api/groups/{g}/history` live — e2e H-1..H-6 incl. opt-in
  respected and 404s; snapshot re-frozen → now **10 paths**, OC-1 red-on-drift
  proved itself when it caught the new route pre-freeze). Also ✅ rate
  limiting (**done 2026-11-07**, see Follow-up 11): fixed window per
  `client_id`, two lane ceilings, e2e RL-1..4. ✅ Prometheus metrics
  (**done 2026-11-07** — Follow-up 12): prometheus-net, `/metrics`,
  e2e MC-1. Remaining Phase 3: Dockerfile + compose (**done
  2026-11-07** — Follow-up 13: `Dockerfile` + `docker-compose.yml` +
  `dev/build-image.sh`; real-lane e2e green against the container: 42/42)
- [ ] Phase 4: SPA migration (`centrifuge-js` → `@microsoft/signalr`),
  INTEGRATION.md rewrite (nucleus written: `docs/INTEGRATION.md`, phase-0
  contract — extend as Phases 1–3 land)
- [ ] Phase 5: deploy compose + Caddy single route; decommission the wall

---

## Session log — 2026-11-07

**Decisions taken (in order):**

1. Plan written: `../centrifugo/docs/PLAN-OWN-SERVER.md` — options A–E,
   reasons-ledger R1–R10; the wall's *reasons* preserved, never its code.
2. Old `POST /realtime/token` mint endpoint: **not carried over** (unused).
3. Stack decided: **Option A — ASP.NET Core SignalR** (mobile later via
   official .NET/Java/Swift-clients; Dart-first would have flipped to Socket.IO).
4. Auth wire mechanics documented (`?access_token` for browser WS — browser
   WS API cannot set headers; path-scoped server extraction).
5. Trust model upgraded to **negotiate-issued connection token** (§5.2): the
   IdP token rides only the negotiate `Authorization` header; the WS query
   string carries a ≤5-min in-process-signed connection JWT. Direct-IdP
   demoted to the drawer fallback; custom ticket endpoint rejected.
6. Real IdP (`https://auth.adolfrey.com/api/`) for the spike — real
   `client_credentials` tokens via the `speedtest` M2M client; mock IdP kept
   as offline fallback.
7. e2e scope narrowed per owner: **server/WS tests, not authority tests**.

**Files created in this repo:** see the checklist + `docs/TECH.md` §4 (repo map).

**Open blockers:** none outstanding. Environment caveats:
- Background server currently **ALIVE** (`:5000`, real authority, dev secret);
  an old mock-IdP listener still lingers on `:9001` (kill with
  `fuser -k 9001/tcp` when the mock lane is needed).
- Real-IdP e2e credentials come from `../centrifugo/.env`
  (`AUTH_SERVICE_CLIENT_ID/SECRET/SCOPES`) — sourced by `dev/run-spike.sh`,
  never printed anywhere.

**Next action (for a fresh session):**
run `cd dev/e2e && node mint-token.mjs` (with creds sourced) to warm the
token cache, re-run `node e2e.mjs` (server-only mode, no authority
round-trip), confirm green, then `git add -A && git commit -m "phase-0 spike: negotiate-issued tokens, real-IdP e2e green"`.

**Follow-up — 2026-11-07 (later the same day):**
- All three open Phase-0 items closed: re-written e2e → **6/6 PASS** (cache
  path, zero authority round-trips); first commit `e69a2f5`;
  `dev/run-spike.sh` real-mode smoke → 6/6 from a fresh server it started.
- Bug fixed en-route: cache writers saved claims but no `access_token`, making
  the cache unusable by `e2e.mjs` (`Bearer undefined` on the next run);
  token cache added to `.gitignore` before staging.
- Phase 0 is **done**; next session starts Phase 1 (§Next up) per plan §8.

**Follow-up 2 — 2026-11-07 (WS-gate hardening):**
- **Security bug found + fixed** in the WS fence: `HttpContext.WebSockets.
  IsWebSocketRequest` reported *false* for a perfectly valid node-client
  upgrade (`Connection: Upgrade` / `Upgrade: websocket` present) → the gate
  was skipped and the un-`[Authorize]`d hub accepted an anonymous connection;
  the e2e's predecessor WS-2 check **false-passed** around it.
- Gate now matches the explicit upgrade headers. Client facts confirmed from
  installed `@microsoft/signalr@10` source: negotiate `accessToken` is
  consumed; Node transports send it as the handshake `Authorization` header,
  browsers as `?access_token=` — doc'd in TECH.md §2/§3.
- New e2e negatives **GATE-1c/1d** (WS upgrade w/o token / garbage token →
  rejected) make WS-2 a real assertion. server/WS suite: **8/8 PASS**;
  hub identity verified: `connected: user=speedtest`.
- Diagnostic logs reduced (dispatch → Debug; claim dump → Debug).
- Next action: `fuser -k 5000/tcp 9001/tcp; bash dev/run-spike.sh` → then
  start Phase 1 group membership work.

**Follow-up 3 — 2026-11-07 (partitioning session):**
- Designed with owner → **per-service scoping (plan R4) ready for groups
  AND connections**. `Groups/GroupPartitions.cs` — `IGroupPartitioner`
  encodes `client_id|logical` (URL-encoded, `|` separator per owner) for
  group keys; `SubUserIdProvider` yields `client_id|sub` (user namespace per
  service); hub connect-time placement also joins `user_channel|{sub}`
  (cross-service global user channel, join now / **send side deferred to the
  Phase 2 publish work** per owner); `POST /api/publish` is partition-scoped
  and stamps `partition` in its 200 reply. Reserved-name guard: a token whose
  `client_id` == `user_channel` is boxed into the `server` partition.
- e2e grew a partition-gate section (mock lane only — only mock mints two
  different `client_id`s): PART-1..4 prove cross-partition publish leaks
  nothing and same-partition delivers. Mock lane **12/12**, real lane **8/8**.
- Cross-repo fix: `ar-auth` **1.2.0-local** packed — `ArAuthClient` now relaxes
  `HttpDocumentRetriever.RequireHttps` for `http://` authorities (the mock
  IdP lane was dead-on-arrival with IDX20108); realtime bumped 1.0.5 → 1.2.0.
- Infra gotcha: the legacy centrifugo **wall stack's `auth-server` container
  squats on :9001** — `docker stop auth-server` before the mock lane
  (restart for the wall).
- e2e cache check now rejects tokens whose `iss` ≠ current `AUTH_AUTHORITY`
  (mock vs real lanes are no longer cross-contaminated); `run-spike.sh`
  exports `IDP_CLIENT_SECRET/SCOPES` so real-mode fresh mint finally works
  (earlier greens rode on the warm cache).
- Next action: start Phase 1 — registry-driven group membership ops on the
  partitioned keys (`POST/DELETE /api/groups/{g}/members` by user id).

**Follow-up 4 — 2026-11-07 (group service, owner-specced):**
- Owner spec locked: `/groups/{g}/members` takes the member's **actual
  client_id + sub** BUT the client_id comes from the **Authorization
  bearer, never the body** (closed world — a service manages only its own
  users; cross-service fan-out stays `user_channel|{sub}`); endpoints
  auto-prepend the caller's client_id to the group name.
- Landed: `Connections/GroupRegistry.cs` (partition-scoped group records +
  membership, reconnect-surviving), `POST /api/groups` (auto-scope,
  idempotent 201/200), `POST/DELETE /api/groups/{g}/members` (live sockets
  joined/evicted immediately via the connection registry), hub now applies
  stored membership at connect; spike demo auto-join removed.
- e2e grew G-1..G-4 + GATE-2b (unknown group → 404): **mock 18/18, real
  13/13** — commit `50772c5` (partitioning) + this follow-up's commit.
- Next action: Phase 2 — broadcast, presence/counts, the §5.3 unicast gap
  (`POST /api/groups` listing can ride the existing `IGroupRegistry`).

**Follow-up 5 — 2026-11-07 (send-to-user / user_channel unicast):**
- `POST /api/users/{sub}/send { data }` live — cross-service unicast per
  owner spec: routes to `user_channel|{sub}` (joined at connect), so `sub`
  alone reaches the user on every device THROUGH ANY service's client_id.
  Same envelope (`data.type` + server-stamped `from` = caller's client_id,
  `sent_at`); publisher policy governs for now (narrower `users:write`
  scope = one-line change later, recorded decision space).
  UserChannelKey bypasses publish partitioning by construction
  (reserved `user_channel` pseudo-partition; client_ids claiming that name
  are boxed into the `server` partition).
- e2e SEND-1..4 + GATE-3/3b: own-token delivery, **cross-service delivery
  (sender `other-service` → user of `spa-demo-client`)**, 400 no-data,
  401 anon. Lanes: **mock 24/24, real 17/17**.
- Remaining §5.3 gap: send-to-connection endpoint only (owner decision;
  hub-side `Clients.Client` already works internally).
- Next action: Phase 2 — broadcast + group listing/counts + presence
  (`IGroupRegistry` already holds the counts source), then §4 contract
  freeze/OpenAPI.

**Follow-up 6 — 2026-11-07 (send-to-connection, partition-scoped):**
- Owner decision: build it, and **caller scoped to its own client_id**.
- `POST /api/connections/{id}/send { data }` live — resolves the socket in
  the connection registry's NEW reverse index
  (`connectionId → physical identity`), decodes the target's partition via
  `IGroupPartitioner.IdentityPartition`, and refuses anything outside the
  caller's partition: **404 unknown / 403 foreign**
  (`{error:"connection_not_in_partition"}`). Same envelope law
  (`from` = caller's client_id, `sent_at`).
- Contrast with `/users/{sub}/send`: that one is deliberately NOT scoped
  (global user channel); this one never crosses the caller's partition —
  two unicast lanes with two different powers.
- e2e: SEND-5/6 (own socket delivered), GATE-4 (unknown→404),
  GATE-4b (second mock connection under `other-service`, sending AT it with
  the `spa-demo-client` token → 403). Lanes: **mock 28/28, real 20/20**.
- Next action remains Phase 2: broadcast + listing/counts + presence, then
  §4 contract freeze/OpenAPI; after that the SPA migration (Phase 4).

**Follow-up 7 — 2026-11-07 (broadcast + group listing):**
- `POST /api/broadcast { groups[], data }`: ONE wire pass
  (`Clients.Groups`) across every caller-partition-scoped group name;
  unknown groups deliver to zero and are REPORTED in `unknown_groups`
  (leaks nothing — only the caller's own partition is inspected).
  `400 groups_required` on empty list.
- `GET /api/groups`: partition-private listing with persisted `members`
  count + `online` (identities holding ≥1 live socket right now) — built
  straight on `IGroupRegistry.List(partition)` (sorted output).
- e2e BC-0..3 + GATE-5 (empty groups → 400) + G-5..G-7 (incl. the
  partition-privacy check: another client's token sees `[]`).
  Lanes: **mock 36/36, real 27/27**.
- Next action: Phase 2 tail — per-group presence feed, `info`, disconnect,
  history edge cases → then §4 contract freeze/OpenAPI before Phase 4.

**Follow-up 8 — 2026-11-07 (presence + disconnect — Phase 2 tail done):**
- `GET /api/groups/{g}/presence`: rows decoded from physical keys
  (`{client_id}|{sub}`) via new `IGroupPartitioner.IdentitySubject`;
  `online` + `connections` read live. Unknown group → 404. Membership
  persists across disconnects (identity row stays, `online` flips false —
  D-4).
- `POST /api/connections/{id}/disconnect`: same partition scoping as
  send-to-connection (404 unknown / 403 foreign). `IHubContext` cannot
  abort sockets → new `Connections/ConnectionAborts.cs` tracks each
  connection's `HubCallerContext` at connect, aborts on demand.
- e2e P-1, D-1 (foreign disconnect → 403), D-2..D-5.
  Lanes: **mock 42/42, real 32/32**.
- Next action: §4 contract freeze/OpenAPI (the Phase 2 exit gate in plan
  §8), then Phase 3 — history ring buffer (opt-in per group), metrics,
  rate limiting, Dockerfile.

**Follow-up 9 — 2026-11-07 (contract freeze — Phase 2 exit):**
- OpenAPI wired (built-in `Microsoft.AspNetCore.OpenApi` 10.0.0):
  `/openapi/v1.json` live machine doc; frozen snapshot
  `docs/openapi-v1.json` (9 paths); **`docs/CONTRACT.md`** = the human
  freeze: security law (bearer client_id = one scope key; publisher scope),
  error envelope, event envelope, 11-endpoint matrix with exact
  req/resp/errors, the partition-vs-global scoping one-liners, hub-side
  placement rules, and the freeze policy (additive → extend; breaking → v2
  at SPA cutover).
- e2e OC-1 anchors the freeze: live paths must equal the snapshot (no
  silent contract drift). Lanes: **mock 43/43, real 33/33**.
  Response schemas; e2e OC-1 anchored. Lanes **mock 43/43, real 33/33**.

**Follow-up 10 — 2026-11-07 (Phase 3: history ring buffer, opt-in):**
- `Connections/HistoryBuffer.cs` — per-partition-group ring (≤10 000,
  default 100) + optional TTL applied lazily on read; publishes/broadcasts
  append the stamped envelope verbatim (cloned node per ring); unicast
  lanes deliberately unrecorded.
- `POST /api/groups` gains `{history, history_size, history_ttl_seconds}`
  (idempotent enable/retune); `GET /api/groups/{g}/history?limit=` returns
  ascending tail + live `total`; DELETE clears → `cleared:n` (disabled → 0).
- e2e H-1..H-6 verified opt-in: demo buffer held exactly the 3 predictable
  publishes (spk-1/spk-3/bc-1), demo2 (no history) held 0.
- **OC-1 earned its keep**: went red when the live doc grew the history
  route before re-freeze → snapshot regenerated; now **10 paths**;
  CONTRACT.md extended (create fields + two history rows).
- Next action: Phase 3 rest — prometheus metrics, per-client_id
  fixed-window rate limiting (the 429 story), Dockerfile + compose;
  then Phase 4 SPA migration against the frozen v1 contract.

**Follow-up 11 — 2026-11-07 (rate limiting — the 429 story + AI reasoning):**
- Middleware-level budgeting, ASP.NET's built-in
  `AddRateLimiter`/`FixedWindowRateLimiter` (no new package):
  **fixed window per `client_id`** partition — the fence gained a third
  behavior: eager best-effort `AuthenticateAsync("ar-idp")` for ANY request
  with an `Authorization` header, so the limiter partitions on the VERIFIED
  client_id before the endpoint runs (auth outcomes unchanged; `[Authorize]`
  still challenges). Anonymous → the `anon` partition.
- **Two lane ceilings in one window** (`RATE_WINDOW_SECONDS`, default 60 s):
  `partitioned` policy = `RATE_MAX_PER_WINDOW` 60 (reads, publish/broadcast,
  group + members ops, disconnect) and `partitioned-send` =
  `RATE_SEND_PER_WINDOW` 600, applied per-endpoint on the two unicast lanes.
  Motivation: **AI reasoning / chain-of-thought streaming** — a backend
  pushing deltas through `users/{sub}/send` per chunk lands hundreds of
  calls/minute; a naive global budget would 429 mid-stream. Endpoint-level
  `EnableRateLimiting` metadata REPLACES the group-level one (verified in
  the ASP.NET source: `GetMetadata` returns the nearest metadata), so lanes
  don't stack limiters.
- 429 story: `{error:"rate_limited", detail}` + `Retry-After` (window
  seconds) — `OnRejected` global handler, `RejectionStatusCode` 429.
- e2e RL-1..4 (run LAST, after OC-1: tripping the window must not poison
  other gates): RL-1 burst publish until 429 + envelope/Retry-After shape;
  RL-2 another client_id still 200 during the exhausted window (mock);
  RL-3 send lane trips its OWN 429 at attempts≈lane budget (596) proving
  lane isolation; RL-4 probe on send lane → 404 (handler answered, not 429).
  Lanes: **mock 53/53, real 41/41**. Snapshot unchanged (no path/schema
  delta) — OC-1 stays green without re-freeze.
- Docs: CONTRACT §2 bullet + new §2.1; TECH config rows + fence §3 +
  surface table; INTEGRATION §4.4 (integrator guidance incl. batching
  deltas, `Retry-After` resume); TODO + run-spike now kills a stale :5000
  listener before boot (bit us twice across lanes today).
- Next action: Phase 3 rest — prometheus metrics (`prometheus-net`,
  `/metrics`), Dockerfile + compose; then Phase 4 SPA migration.

**Follow-up 12 — 2026-11-07 (Prometheus metrics — Phase 3 ops done):**
- `prometheus-net.AspNetCore` 8.2.1 wired: `app.UseHttpMetrics()` (HTTP
  collectors + runtime: `dotnet_collection_count_total`,
  `dotnet_total_memory_bytes`, `http_requests_received_total`,
  `http_request_duration_seconds`) and `app.MapMetrics("/metrics")`.
  Middleware order: after the fence, before UseRateLimiter.
- Placement decision: `/metrics` is OUTSIDE the `/api` group → no bearer,
  no publisher scope, NOT rate-limited (a scraper must never consume a
  client's budget) and deliberately not in the frozen OpenAPI snapshot
  (scrape surface is prometheus-text-versioned, not a JSON-API path) —
  OC-1 stays green without re-freeze; CONTRACT §4 row + exclusion note.
  Deploy: bind the scrape endpoint to the internal network only (Phase 5
  compose).
- e2e MC-1 (before OC-1): 200 + text/plain 0.0.4 + the three stable
  collector families present. Lanes: **mock 51/51, real 42/42** (RL-2/RL-4
  skip on cache-hit tokens — mint-only checks).
- Next action: Dockerfile + docker-compose (phase-5-shaped, so the Phase 4
  SPA migration runs against the containerized server), then Phase 4 SPA
  migration (`@microsoft/signalr`) against the frozen v1 contract.

**Follow-up 13 — 2026-11-07 (containerized shape — Phase 3 exit done):**
- `Dockerfile`: multi-stage (sdk:10.0 build → aspnet:10.0 runtime);
  `prometheus-net.AspNetCore` + `Ar.Auth.OpenId` 1.2.0-local restore via a
  vendored feed: `nuget-feed/` (gitignored, staged by `dev/build-image.sh`
  from the sibling repo's `../local-nuget`) + `docker/NuGet.docker.config`
  override; dependency layer cached off `Realtime.csproj`.
- `docker-compose.yml`: one service (`realtime`), the full 12-factor env
  surface from TECH §5 (rates, TTL, origins, authority), restart policy,
  healthcheck `curl /healthz` (runtime base ships no wget — curl installed
  in the runtime stage; YAML trap: `${VAR:?msg}` with colons must be
  quoted). `CONN_TOKEN_SECRET` is the only secret, compose-required.
- `OC-1 passed UNCHANGED` from inside the container (10 paths — /metrics
  deliberately not a documented API path).
- **Full real-lane e2e against the container: 42/42** (fresh token, real
  authority) — including the RL rate-limit bursts and MC-1 metrics scrape.
- Phase 3 done (history ✓ rate limiting ✓ metrics ✓ container ✓). Next:
  Phase 4 — SPA migration `centrifuge-js` → `@microsoft/signalr`,
  INTEGRATION.md rewrite against the frozen v1 contract; then Phase 5
  deploy compose (Caddy edge) + decommission the wall.

**Follow-up 14 — 2026-11-07 (Phase 4 redirected: client libraries):**
- Owner decision: **NO SPA migration** (`../centrifugo` SPA stays) — Phase 4
  becomes client libraries for easy connection to this microservice, all
  behind Cloudflare → every lane must customize the User-Agent (never
  SDK-default bot-suspicious identities). Three libraries, in-repo:
  `clients/ts` consumer + `clients/dotnet`/`clients/python` publishers
  (owner's role split: JS consumes, .NET/Python publish). Token plumbing
  rides `ar-auth-client-(js/dotnet/python)` — libs take factories/adapters,
  never mint IdP credentials themselves.
- `clients/ts` — `@adolf94/realtime-client` (commit `a8e25ed`): hub
  subscription wrapper; `options.headers` verified in the installed
  @microsoft/signalr source (merged LAST on negotiate, forwarded to the ws
  constructor) so Node lanes override the stock UA on BOTH negotiate and WS;
  browsers keep the native UA + `X-SignalR-User-Agent` override
  (CORS `AllowAnyHeader` already permits it). Envelope router (type-routed
  handlers + onAny + unknown-frame ignore + subscriber-exception
  containment), tokenFactory re-invoked per reconnect; conformance 12 gates.
- `clients/dotnet` — `Ar.Realtime.Client` 0.1.0-local (packed to the local
  feed): full publisher surface (`IRealtimePublisher`), envelopes as
  `JsonObject` w/ client-side `data.type` enforcement, `RealtimeApiException`
  mapping the error envelope verbatim (429 carries `RetryAfterSeconds`),
  `ArAuthAccessTokenProvider` wraps `Ar.Auth.OpenId.ClientCredentialsAsync`
  (cached until ExpiresIn−60 s). xunit conformance 8/8 via the harness.
- **Server fix surfaced by the suite**: `GroupRegistry.Create` always
  returned `created=true` (`GetOrAdd` bool never captured) — e2e mock/real
  lanes re-verified green after the fix (mock 54/54, real 42/42).
- `clients/python` — `ar-realtime-client` (uv project, httpx): publisher
  mirrors dotnet; `ar_auth-coupled` `ArAuthTokenProvider` accepts any
  client_credentials-shaped object (lazy) — pypi `ar-auth-client` does NOT
  exist so the sibling SDK is an editable-install / factory decision for
  the consumer, never a hard dep. Conformance 8/8 via the harness.
- Harness: `dev/test-clients.sh [ts|dotnet|python]` — boots the mock lane
  then runs each lib's suite against it (self-skipping tests stay green
  without a stack).
- Deferred/doc note: the INTEGRATION.md rewrite (original Phase 4 item) is
  pending — docs are under live owner condensation; a client-libraries
  pointer landed in README instead.
- Next: Phase 5 — deploy compose + Caddy single route; decommission the
  wall.
- Phase 4 exit verification (owner fence commit `f16763e` included):
  full client sweep green — ts 12 gates ALL PASS, dotnet 8/8, python 8/8 —
  and both e2e lanes re-verified: mock 54/54, real 42/42. Phase 4 CLOSED.
  (Owner's doc condensation still uncommitted in the tree — left as-is.)

**Follow-up 15 — 2026-11-07 (Phase 5 deploy artifacts + edge rehearsal):**
- `deploy/docker-compose.yml` — prod/gh_webhooks target mirroring the wall's
  deploy shape: `IMAGE=<sha>` required, NO host ports, explicit env map,
  healthcheck, `realtime` internal net + external `proxy` net (central Caddy).
- `deploy/caddy-route.caddy` — ONE public prefix (`/realtime-v2`, plan §7.4)
  preserving the service's canonical paths: single `handle_path` strip +
  explicit deny for `/metrics`, `/openapi`, `/healthz` (ops door internal-only).
  Cloudflare Access scoping warning carried over from the wall compose.
- **Edge rehearsal PROVEN locally** (`dev/edge-rehearsal.sh` — caddy:2 replica
  + PROD-shaped realtime container): the FULL e2e suite drove only the edge
  (SRV=edge, EDGE=/realtime-v2, SRV_OPS=internal ops door) — **54/54 PASSED**,
  incl. WS upgrade, negotiate fence, fan-out, partition isolation, history,
  presence, disconnects, 429 Retry-After; edge surface check → metrics/openapi
  edge-404. `dev/e2e/e2e.mjs` gained `EDGE`/`SRV_OPS` envs (direct mode
  unchanged; metrics+openapi gates always hit the ops door).
- iss discipline lesson: token `iss` is compared VERBATIM against the
  container's `AUTH_AUTHORITY` — the mock IdP now mints with the
  `host.docker.internal:9001/realms/demo` issuer string on all legs.
- TECH §9 extended with the edge/cutover runbook in condensed style.
- Owner-side pieces (NOT mine to run): `.github/workflows/deploy-gh-webhooks.yml`
  (user-authored CI: build→push ghcr + signed webhook) is UNTRACKED — user's
  WIP; actual central-Caddy paste + wall decommission happen on the deploy
  host (apis host). Demo SPA still rides `/websockets/*` → wall cutoff is an
  owner decision after burn-in.
