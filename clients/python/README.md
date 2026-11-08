# ar-realtime-client

Publisher client for the **realtime** microservice — the HTTP server API
door (CONTRACT v1 — `../../docs/CONTRACT.md`): publish/broadcast, the
unicast lanes (reasoning-stream addresses), group + members ops, history,
presence, force-disconnect.

The hub/bus side is read-only per contract — SPA/Node **consumers** belong
to `@adolf94/realtime-client` (`clients/ts`); .NET publishers mirror this
API in `clients/dotnet` (Ar.Realtime.Client).

## Quick start

```python
from ar_realtime_client import RealtimePublisher, RealtimePublisherOptions, StaticTokenProvider

with RealtimePublisher(RealtimePublisherOptions(
    base_url="https://realtime.example.com",           # no /api suffix
    token_provider=StaticTokenProvider(get_idp_token()),
    user_agent_suffix="my-service",
)) as publisher:

    publisher.create_group("approvals")
    publisher.add_member("approvals", user_sub)         # live join + persists

    # reasoning stream: batch deltas per chunk (CONTRACT §2.1 window: 600/min)
    publisher.send_to_user(user_sub, {"type": "ai_emission", "delta": chunk})
```

`data.type` is enforced client-side; `from`/`sent_at` are server-stamped —
do NOT set them.

## Tokens (owner spec: depend on ar-auth-client-*)

No IdP minting lives here — the transport stays auth-agnostic:

* simplest: pass any `callable()[-> str]` (a token factory) —
  `RealtimePublisherOptions(base_url=…, token_provider=lambda: my_token())`;
* sibling-SDK adapter:

  ```bash
  uv pip install -e ../../ar-auth/ar-auth-client-python     # ar_auth package
  ```

  ```python
  from ar_auth import ArAuthClient                 # sibling SDK (../ar-auth)
  from ar_realtime_client import ArAuthTokenProvider

  ArAuthTokenProvider(ArAuthClient(Authority="https://auth.adolfrey.com/api", ClientId=…, ClientSecret=…),
                      scope="api://ws-server/app:publisher")
  ```
  (cached until `expires_in − 60 s`, re-requesting thereafter — mirrors
  `Ar.Realtime.Client`).

## Coalesced streaming (single-flight publishes)

Reasoning-token lanes batch on their own: `offer()` is lock+append only, and the
next flush fires the instant the in-flight response lands — batch size
self-tunes to `generation_rate × RTT`, ordering guaranteed (request N+1 never
sends before N's response):

```python
with publisher.coalesced_send_to_user(user_sub) as chunk_timeline:
    for chunk in model_stream():          # NEVER awaits HTTP
        chunk_timeline.offer({"type": "reclassifyThinking", "args": {...}})
# context exit flushes the remainder; the last error propagates exactly once
```

Per-TAB CoT streams ride the **connection lane** the same way —
`coalesced_send_to_connection(connection_id)` (`offer`/`drain` semantics
identical; independent single-flight windows per tab so each TAB's ordering is
its own). `connection_id` is the server-minted LOGICAL id (CONTRACT §2.2/§5):
the SPA captures it from the server-published `connection_registered`
envelope — NOT SignalR's transport socket id — and passes it in the job
request that starts the stream; `?instance=<stable client tag>` on the SPA's
hub url keeps it stable across reconnects (untagged = fresh per-socket id).
A connection 404 (`connection_not_connected` / `connection_not_found`) or 403
surfaces as the non-429 drop path — the batch drops, the stream continues,
`drain()` raises it once; a 404 is the cue to fall back to `send_to_user`.

Explicit form when you want the stats: `handle.drain()` → it re-raises the
last batch failure once (typically `RealtimeApiError`; then the same stats,
`offered`/`flushed`/`dropped`/`publishes`/`last_error`, are re-served
idempotently). A first 429 waits out `Retry-After` (60 s fallback if the
header is absent) and re-sends the SAME batch once; a second failure drops
the batch (counted in `.dropped`) and the stream continues. The wire contract
is CONTRACT §3.1: one request per flush carrying `{type, batch, batch_v: 1}`.
`coalesced_send_to_user(user_sub, max_pending_chars=64_000)` bounds queued-but-
unflushed memory by shedding the OLDEST chunks — freshest reasoning survives a
burst.

## Errors & rate limiting

Non-2xx → `RealtimeApiError(status, code, detail, retry_after)` — the
envelope verbatim. A 429 (`rate_limited`, CONTRACT §2.1) carries
`retry_after` seconds: **pause input work and retry at that deadline**;
a spinning retry re-draws from the same exhausted partition. Validation
failures (`data.type` missing, empty ids) raise `RealtimeValidationError`
before any wire traffic.

## User-Agent (Cloudflare)

`ar-realtime-client/0.1.0 (suffix)` rides every request; set
`user_agent_suffix` for traceability in Cloudflare logs.

## Conformance

```bash
SRV=http://localhost:5000 MOCK_AUTH=http://localhost:9001 uv run --extra dev pytest tests -q
```

(`dev/test-clients.sh python` does this with the stack up.)

## Install

```bash
uv pip install -e clients/python          # from the realtime repo
```
