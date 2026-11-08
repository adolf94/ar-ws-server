# realtime

Self-hosted real-time server (one .NET process): **SignalR hub + minimal-API
server API**, OAuth-secured by the platform IdP via `Ar.Auth.OpenId`.
Replaces the former Centrifugo + proxy-wall stack; design + owner decisions:
[`../centrifugo/docs/PLAN-OWN-SERVER.md`](../centrifugo/docs/PLAN-OWN-SERVER.md)
(negotiate-issued connection tokens, SignalR, single node).

## Start here

* **`docs/TECH.md`** — the technical entry point: architecture, trust model,
  components, configuration, running/testing, roadmap.
* **`docs/CONTRACT.md`** — the frozen API wire contract (v1); schema snapshot
  `docs/openapi-v1.json`, live doc at `/openapi/v1.json`.
* **`docs/INTEGRATION.md`** — client/backend-facing wire contract: how SPAs,
  mobile, and app backends connect and publish.
* **`TODO.md`** — live status: what's done, in progress, next up.
* **Client libraries** (`clients/`): TypeScript consumer
  (`@adolf94/realtime-client` — hub subscription, Cloudflare-safe UA) ·
  .NET publisher (`Ar.Realtime.Client`) ·
  Python publisher (`ar-realtime-client`). All three publish via CI
  (npm/dotnet → GitHub Packages, python → GitHub Releases); the .NET
  package also rebuilds locally against the local NuGet feed.
  Both publishers ship **coalesced streaming** — single-flight unicast
  sending with self-tuning batches and 429 retry handling (CONTRACT §3.1)
  — so AI reasoning-stream producers batchify instead of stalling on RTT.
  Conformance: `bash dev/test-clients.sh [ts|dotnet|python]`.
* Run the spike: `bash dev/run-spike.sh` (real IdP; e2e creds sourced from
  `../centrifugo/.env`, never printed). See `docs/TECH.md` §6 for details.
