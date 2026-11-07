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
- [ ] Phase 1 core: registry-driven group membership (connect-time + runtime
  `POST/DELETE /api/groups/{g}/members` by user id); per-service scoping
  (plan R4) for groups AND user identities is **already implemented**:
  `GroupPartitioner` namespaces everything behind the caller's verified
  `client_id` (`Groups/GroupPartitions.cs`; e2e PART-1..4 gate it) —
  membership ops just build on the same partition helper
- [ ] Phase 2: broadcast, presence, info endpoints; full contract + OpenAPI
  — **fold in the plan §5.3 gap surfaced writing `docs/INTEGRATION.md`**:
  add send-to-user (`/api/users/{sub}/send`) and
  send-to-connection (`/api/connections/{id}/send`) to §5.3 (fan-out
  primitives already exist server-side; no HTTP route is planned)
  → there, design the owner's **global user channel** `user_channel|{sub}`
  (server auto-join on connect, cross-`client_id` unicast regardless of
  which service the user is connected through — deferred from the 2026-11-07
  partitioning session; needs its own scope decision on who may fan out)
- [ ] Phase 3: history ring buffer, metrics, rate limiting, Dockerfile
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
