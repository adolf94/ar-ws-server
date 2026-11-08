# Implementation Plan — TypeScript publisher SDK (`@adolf94/realtime-publisher`)

## Overview

A TypeScript publisher SDK mirroring `clients/dotnet` (`Ar.Realtime.Client`) and
`clients/python` (`ar-realtime-client`) at feature parity: the full server-API
publisher surface of `docs/CONTRACT.md` v1 (frozen), plus the coalesced
single-flight streaming lane (CONTRACT §3.1 `batch_v: 1`) so AI reasoning-stream
producers batchify without re-implementation.

Lives in `clients/ts-publisher` as a new zero-runtime-dependency ESM package
using global `fetch` (Node ≥ 18 + browsers). Parity constants, semantics, and
gate lists are taken verbatim from the .NET implementation; the conformance
suite rides the existing `dev/test-clients.sh` mock-lane harness.

## Architecture Decisions

- **Separate package, not a module in `@adolf94/realtime-client`** (owner
  confirmed). Mirrors how .NET/Python publishers stand alone; the consumer lib
  keeps its zero publishing state; no SignalR dep here.
- **`fetch` everywhere** — no undici/axios. Node 18+ global fetch; in browsers
  `User-Agent` is a forbidden header and is ignored (same caveat pattern as the
  existing consumer lib: our UA overrides on Node lanes only).
- **Auth: adapter, never a mint** (owner spec, TECH/INTEGRATION): the transport
  takes an `AccessTokenProvider` (`getToken(): Promise<string>`); sibling
  `ar-auth-client-js` wiring is the consumer's factory decision — no hard dep,
  exactly like python's `CallableTokenProvider` stance. Ship `StaticTokenProvider`,
  `FixedTokenProvider`, and a cache-until-expiry wrapper; validate providers up
  front (python 0.1.1 hardening: garbage raw dict/str providers raise eagerly).
- **Events as plain objects** (`Record<string, unknown>`), not a JSON builder —
  the .NET `JsonObject` graph-parenting bug (retry envelope reuse) is
  structurally impossible here; python's dict model is the parity reference.
- **Coalescing is a worker loop, not timers** (CONTRACT §3.1 "flush fires on
  response land"): one in-flight publish per handle, batch ≈ generation_rate ×
  RTT; `offer()` = eager validate + append only, never HTTP; budget is on
  PENDING chars (`maxPendingChars`, default 64_000) shedding the OLDEST chunks;
  429 → honor `Retry-After`, re-send the SAME batch once, second failure →
  drop + record, stream continues; `complete()` ≡ python `drain()` ≡ dotnet
  `CompleteAsync()` (flush remainder + propagate last error exactly once);
  dead-lane try/finally guarantees `complete()` never hangs.
- **Version 0.1.0**, `publishConfig → npm.pkg.github.com`, `files: [dist, README]`,
  engines `node >= 18` — mirrors the consumer package's shape.

## Wire surface (parity checklist, from `IRealtimePublisher`)

| SDK member | Endpoint (CONTRACT §4) |
|---|---|
| `publish(group, data)` | POST `/api/publish` |
| `broadcast(groups, data)` | POST `/api/broadcast` |
| `sendToUser(sub, data)` | POST `/api/users/{sub}/send` (global lane, NOT partition-scoped) |
| `sendToConnection(id, data)` | POST `/api/connections/{id}/send` (404/403 scoping) |
| `disconnect(id)` | POST `/api/connections/{id}/disconnect` |
| `createGroup(name, {history?, historySize?, historyTtlSeconds?}?)` | POST `/api/groups` (idempotent 200/201) |
| `addMember(group, userId)` | POST `/api/groups/{g}/members` |
| `removeMember(group, userId)` | DELETE `/api/groups/{g}/members?user_id=` |
| `listGroups()` | GET `/api/groups` |
| `presence(group)` | GET `/api/groups/{g}/presence` |
| `history(group, limit?)` | GET `/api/groups/{g}/history?limit=` |
| `clearHistory(group)` | DELETE `/api/groups/{g}/history` |
| `coalescedSendToUser(sub, opts?)` | user lane handle |
| `coalescedSendToConnection(id, opts?)` | per-socket lane handle |

Envelope law (§3): `data.type` is the ONLY client-side duty (eager validation,
fail before the wire); `from`/`sent_at` are server-stamped. Error law (§2):
non-2xx → `RealtimeApiError {status, code, detail, retryAfterSeconds?}` mapping
`{error, detail}` verbatim; 429 `Retry-After` (seconds, and HTTP-date fallback)
→ `retryAfterSeconds` (≥ 1). Path params URL-encoded. Optional fields omitted
when `undefined` (JSON.stringify already drops them).

## Task List

### Phase 1 — Foundation

- [ ] Task 1: Package scaffold + token plumbing (`clients/ts-publisher`)

### Checkpoint: Foundation
- [ ] `npm run build` compiles clean (strict TS)

### Phase 2 — Transport + publisher surface

- [ ] Task 2: Transport: fetch send loop + error mapping
- [ ] Task 3: Write lanes: publish / broadcast / unicast / disconnect
- [ ] Task 4: Group ops: create / members / list / presence / history
- [ ] Task 5: Coalesced send handle (single-flight worker)

### Checkpoint: Parity
- [ ] API surface matches the parity checklist above, 1:1 with .NET

### Phase 3 — Conformance + docs

- [ ] Task 6: Conformance suite + `dev/test-clients.sh` wiring
- [ ] Task 7: README + TODO.md session-state update

### Checkpoint: Complete
- [ ] Full publisher conformance gate list green against the mock lane
- [ ] `dev/test-clients.sh` still green for the existing ts consumer suite

## Risks and Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| JS worker-lane rejections escape unhandled | Coalescer hangs or crashes the process | try/finally around the whole loop resolving a `done` promise; `lastError` recorded, never rethrown outside `complete()` |
| 429 `Retry-After` arrives as HTTP-date | Wrong backoff | Parse integer-seconds first, fall back to HTTP-date (≥ 1 s), parity with dotnet `MapFailure` |
| Reused envelope on retry mutates caller chunks | Data corruption | Serialize/copy at send boundary; chunks ride verbatim but never aliased into the request body twice without a clone |
| Browser can't set `User-Agent` | UA gates can't assert there | Node-only UA capture gate (echo server); browser keeps native UA by design — document it |
| Harness runs against stale `dist/` | Phantom green | Conformance entry in `dev/test-clients.sh` runs `npm run build` before the suite (fix the same latent gap in the consumer lane) |
| `client_id` partition confusion (AGENTS rule) | Cross-service test flakiness | Mock mints publisher token with publisher scope + one `client_id`; unicast gate uses a fresh `sub`, never a body-carried partition |

## Open Questions

- None blocking. (Icon/branding snippet of README can reuse the consumer lib's tone.)

## References

- `docs/CONTRACT.md` — the wire law (§2 error envelope, §2.1 rate limiting, §3 envelope, §3.1 batch, §4 endpoints)
- `clients/dotnet/src/Ar.Realtime.Client/` — reference semantics (`IRealtimePublisher.cs`, `CoalescedSendHandle.cs`, `AccessTokenProviders.cs`)
- `clients/python/src/ar_realtime_client/publisher.py` — python parity (worker/lane rules)
- `clients/ts/` — package shape, UA pattern, harness conventions
- `dev/test-clients.sh` — conformance harness to extend
