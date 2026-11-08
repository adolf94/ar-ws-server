# Task list — TS publisher SDK (`@adolf94/realtime-publisher`)

> Plan rationale lives in `tasks/plan.md`. Work top-down; every task ends
> buildable. Checkpoints after each phase.

---

## Task 1: Package scaffold + token plumbing

**Description:** Create `clients/ts-publisher` as an ESM TypeScript package
mirroring `clients/ts`'s shape (`package.json` with `publishConfig →
npm.pkg.github.com`, strict tsconfig, `tsc → dist/`, engines `node >=18`,
zero runtime deps). Implement auth plumbing — transport-agnostic, adapter
style: `AccessTokenProvider` interface (`getToken(): Promise<string>`),
`StaticTokenProvider`, `FixedTokenProvider(factory)`, and a
`CachingTokenProvider` wrapper (cache until `expiresIn − 60`, floor 30 s) plus
eager rejection of garbage providers (non-function/non-string-shaped → `TypeError`
up front, python 0.1.1 hardening parity). Export a `PUBLISHER_USER_AGENT`
constant `realtime-client-ts/<version>` applied on Node lanes.

**Acceptance criteria:**
- [ ] `npm run build` compiles clean (ESB, strict, `dist/` emitted, `.d.ts` present)
- [ ] Zero `dependencies` in package.json
- [ ] `StaticTokenProvider` returns its token; `FixedTokenProvider` defers to the factory;
      `CachingTokenProvider` re-mints at expiry − 60 s and is safe on parallel calls
- [ ] Garbage providers reject at construction, not first call

**Verification:**
- [ ] `npm run build` succeeds
- [ ] `node -e` import smoke: `import('@adolf94/realtime-publisher')` exports all providers

**Dependencies:** None

**Files likely touched:** `clients/ts-publisher/package.json`, `tsconfig.json`,
`src/index.ts`, `src/tokens.ts`

**Estimated scope:** Small (3-4 files)

---

## Task 2: Transport — fetch send loop + error mapping

**Description:** `RealtimeApiError` (`status`, `code`, `detail`,
`retryAfterSeconds?`) + `RealtimeValidationError`. Core `send()` on `fetch`:
auth header from the provider per call, JSON body, path params pre-encoded by
callers, honest UA header, `AbortSignal` parity with dotnet's `CancellationToken`
(per-call optional signal). Non-2xx → parse `{error, detail}` verbatim (fallback:
raw text as `detail`); 429 → `Retry-After` integer-seconds, HTTP-date fallback
pinned to now (seconds ≥ 1). Empty body on 2xx → `empty_body`; unparseable JSON
→ `body_invalid`.

**Acceptance criteria:**
- [ ] Non-2xx maps to `code`/`detail` exactly as the server envelopes them (CONTRACT §2)
- [ ] 429 exposes `retryAfterSeconds` for both header formats; non-429 events never carry it
- [ ] 2xx empty/bad-JSON bodies raise `RealtimeApiError` with `empty_body`/`body_invalid`
- [ ] Per-call `AbortSignal` aborts the fetch and surfaces a native abort error

**Verification:**
- [ ] `npm run build` succeeds
- [ ] `npm run test` placeholder-conformance compiles + runs (real gates land in Task 6)

**Dependencies:** Task 1

**Files likely touched:** `src/errors.ts`, `src/transport.ts`

**Estimated scope:** Small (2 files)

---

## Task 3: Write lanes — publish / broadcast / unicast / disconnect

**Description:** Publisher surface half 1, 1:1 with `IRealtimePublisher`:
`publish(group, data)`, `broadcast(groups, data)`,
`sendToUser(sub, data)`, `sendToConnection(id, data)`, `disconnect(id)`.
`ensureEnvelope(data)` — eager `data.type` string validation before any network
(exported again for the coalescer's `offer()` use). Broadcast: dedupe + drop
whitespace-only group names, local `RealtimeValidationError` on empty list
(dotnet parity). Path params URL-encoded. Typed results mirroring
`RealtimeModels.cs`: `PublishResult`, `BroadcastResult` (incl.
`unknownGroups`), `SendResult` (nullable `userId` / `connectionId` /
`connectionPartition`), `DisconnectResult`. Deserialize response bodies into
those types.

**Acceptance criteria:**
- [ ] `data` without a string `type` throws `RealtimeValidationError` before any HTTP request
- [ ] Broadcast with zero valid groups throws local validation error (no request)
- [ ] `sub`/`id`/group names in paths URL-encoded (`%7C` for `|`, spaces, etc.)
- [ ] Result types match the CONTRACT §4 response fields 1:1

**Verification:**
- [ ] `npm run build` succeeds

**Dependencies:** Task 2

**Files likely touched:** `src/publisher.ts`, `src/models.ts`

**Estimated scope:** Small (1-2 files)

---

## Task 4: Group ops — create / members / list / presence / history

**Description:** Publisher surface half 2: `createGroup(name,
{history?, historySize?, historyTtlSeconds?}?)`, `addMember(group, userId)`,
`removeMember(group, userId)`, `listGroups()`, `presence(group)`,
`history(group, limit?)`, `clearHistory(group)`. Optional fields omitted if
undefined (idempotent re-post tunes config only when `history` rides). Typed
results: `GroupCreateResult` (created, partition, historyEnabled),
`MemberAddResult` (member{userId, client_id}, connectionsJoined),
`MemberRemoveResult`, `GroupRow`, `PresenceRow`, `HistoryResult` (ascending
tail, most recent LAST), `HistoryClearResult`. Empty-string identifiers reject
locally (name/user_id required).

**Acceptance criteria:**
- [ ] Omitted optional group-history knobs stay absent from the wire (server omission happy path)
- [ ] Member rows map `member.user_id`/`member.client_id`; remove maps boolean `removed`
- [ ] History result preserves array order (ascending tail) and `total`
- [ ] Empty `name`/`userId` throws `RealtimeValidationError` locally (no request)

**Verification:**
- [ ] `npm run build` succeeds

**Dependencies:** Task 2

**Files likely touched:** `src/publisher.ts`, `src/models.ts`

**Estimated scope:** Small (1-2 files)

---

## Task 5: Coalesced send handle — single-flight worker

**Description:** `CoalescedSendHandle` mirroring dotnet `CoalescedSendHandle` /
python `coalescing.py` semantics. Constructor starts ONE internal worker
(async loop, no timers). API: `offer(data)` (eager `ensureEnvelope` + budget
accounting — never HTTP, sync), `complete()` (drain + propagate last error
exactly once, idempotent afterwards), `snapshot()` (`CoalesceStats`:
offered/flushed/dropped/publishes/lastError). Lanes: `coalescedSendToUser(sub)`
and `coalescedSendToConnection(id)` on the publisher interface. Semantics:
one in-flight publish per handle; chunks offered in flight ride the NEXT flush;
batch self-tunes to generation_rate × RTT (no timers, flush on response land);
`maxPendingChars` (default 64 000) budgets PENDING only, shedding OLDEST chunks
(freshest wins, ≥ 1 always survives even if alone over budget); envelope = flat
`stream.type` + `batch` array (chunks verbatim) + `batch_v: 1`, outer type =
first chunk's type (CONTRACT §3.1); 429 with Retry-After → wait + re-send SAME
batch once, second failure/any other error → drop + record + continue; worker
try/finally resolves the done promise so `complete()` never hangs on a dead lane;
ordering: strictly sequential sends, no batch can reorder (request N+1 gated on
N's response). Deep-copy guarded at the send boundary (plain objects, no
parent-graph hazard, but clone the envelope each attempt anyway).

**Acceptance criteria:**
- [ ] `offer()` returns immediately (no HTTP performed) and is callable while a flush is in flight
- [ ] Batches never mix targets; per-handle ordering preserved across retries
- [ ] 429 → exactly one same-batch retry after `retryAfterSeconds`; second 429 drops batch and continues
- [ ] `complete()` flushes remainder, resolves stats, and surfaces last error exactly once; second call is idempotent
- [ ] A hard worker crash never leaves `complete()` waiting forever
- [ ] Over-budget pending sheds oldest chunks; newest always survives

**Verification:**
- [ ] `npm run build` succeeds
- [ ] `npm run test` placeholder gates for offer/snapshot pass without a server (pure-logic happy paths)

**Dependencies:** Task 3

**Files likely touched:** `src/coalescing.ts`, `src/publisher.ts`, `src/index.ts`

**Estimated scope:** Medium (3 files, logic-dense)

---

## Checkpoint: Parity

- [ ] Full API surface matches the parity checklist in `tasks/plan.md` 1:1 with .NET
- [ ] All member docstrings reference CONTRACT sections (documentation integrity rule)
- [ ] `results` shape round-trips the `docs/openapi-v1.json` snapshot responses

---

## Task 6: Conformance suite + harness wiring

**Description:** `clients/ts-publisher/scripts/conformance.mjs` mirroring the
dotnet gate list, against the running mock lane (gates import from `dist/`
like the consumer suite). Wire into `dev/test-clients.sh` as a new lane
argument `tspub` (keep `ts` consumer and default-all behavior), and make each
lane build before testing. Gate list:
PUB-1 publish round-trip; PUB-2 broadcast incl. `unknown_groups` leak-free;
SEND-1 unicast send-to-user round-trip; SEND-2 send-to-connection incl.
404-unknown/403-foreign scoping negatives; COAL-1 batch envelope wire shape
(`batch_v:1`, outer type, verbatim chunks) seen live as ONE history/rec voltage
row; COAL-2 429 → same-batch retry once → drop+record (drop counter, stream continues);
COAL-3 shed-oldest over budget; COAL-4 complete() error propagation once;
UA-1 Node User-Agent override echoed; ERR-1 `detail` verbatim + provider-prefix
gate reuse of publisher scope mint.

**Acceptance criteria:**
- [ ] `bash dev/test-clients.sh tspub` boots the mock lane and green-lights all gates listed above
- [ ] `bash dev/test-clients.sh` (all) still green as before the change
- [ ] Suite exits non-zero on a failed gate (new gate must be PROVEN once) — flip an assertion temporarily if needed
- [ ] No flake: gates offline-tagged exactly like the consumer suite (SRV/MOCK_AUTH env-pinned)

**Verification:**
- [ ] `bash dev/test-clients.sh tspub`
- [ ] `bash dev/test-clients.sh` (full default lane)

**Dependencies:** Tasks 3, 4, 5

**Files likely touched:** `clients/ts-publisher/scripts/conformance.mjs`,
`dev/test-clients.sh`

**Estimated scope:** Medium (2 files, 9 gate families)

---

## Task 7: README + TODO.md session-state update

**Description:** `clients/ts-publisher/README.md` — package purpose, quickstart
(static + caching provider examples), coalescing semantics (offer/complete,
budget shedding, 429 story), UA behavior on Node vs browser, differences vs
dotnet/python (naming/AbortSignal/object payloads). Update `TODO.md` Phase 4
checklist with the new publisher (mirroring the existing dotnet/python entries'
style: version, suite count, gates) so the session-state doc walks hand-in-hand
with reality.

**Acceptance criteria:**
- [ ] README shows the coalesced send with the correct member names after build
- [ ] TODO.md gains a Phase 4 `clients/ts-publisher` entry with suite/gate numbers matching reality
- [ ] No `file://` style links anywhere; docs reference CONTRACT/INTEGRATION by clean relative path

**Verification:**
- [ ] Manual doc read-back
- [ ] `grep file:// clients/ts-publisher` empty

**Dependencies:** Task 6

**Files likely touched:** `clients/ts-publisher/README.md`, `TODO.md`

**Estimated scope:** Small (2 files)

---

## Checkpoint: Complete

- [ ] All publisher gates green through `dev/test-clients.sh tspub`
- [ ] Consumer `ts` lane unaffected (default harness run green)
- [ ] Parity checklist in `tasks/plan.md` fully checked
- [ ] Human review done
