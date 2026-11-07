# realtime

Self-hosted real-time server (one .NET process): **SignalR hub + minimal-API
server API**, OAuth-secured by the platform IdP via `Ar.Auth.OpenId`.

Replaces the Centrifugo + proxy-wall stack. Design + decisions:
[`../centrifugo/docs/PLAN-OWN-SERVER.md`](../centrifugo/docs/PLAN-OWN-SERVER.md)
(owner decisions: negotiate-issued connection tokens, SignalR, single node).

## Start here

* **`docs/TECH.md`** — architecture, trust model, components, configuration,
  running/testing, roadmap. This is the technical entry point.
* **`TODO.md`** — live status: what's done, in progress, next up.
* Run the spike: `bash dev/run-spike.sh` (real IdP; e2e creds sourced from
  `../centrifugo/.env`, never printed). See `docs/TECH.md` §6 for details.
