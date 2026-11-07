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

    # reasoning stream: batch deltas per chunk (CONTACT §2.1 window: 600/min)
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
