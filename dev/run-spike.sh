#!/usr/bin/env bash
# ── Phase-0 spike runner ─────────────────────────────────────────────────────
#   IDP_MODE=real (default)  server trusts https://auth.adolfrey.com/api ( issuer-only);
#                            e2e mints REAL client_credentials tokens — credentials
#                            sourced from ../centrifugo/.env, never printed.
#   IDP_MODE=mock            offline lab — mock IdP on :9001 (kept as fallback).
set -euo pipefail
cd "$(dirname "$0")/.."

IDP_MODE="${IDP_MODE:-real}"
SRV_URL="${SRV_URL:-http://localhost:5000}"

if [[ "$IDP_MODE" == "mock" ]]; then
  AUTH_AUTHORITY="${AUTH_AUTHORITY:-http://localhost:9001/realms/demo}"
  echo "▶ mock IdP mode — starting mock on :9001 …"
  fuser -k 9001/tcp 2>/dev/null || true
  sleep 0.5
  (cd dev/mock-idp && KEYS_DIR="$PWD/keys" ISSUER="$AUTH_AUTHORITY" node server.js &> /tmp/realtime-mock-idp.log &)
  sleep 0.7
else
  AUTH_AUTHORITY="${AUTH_AUTHORITY:-https://auth.adolfrey.com/api/}"
  echo "▶ real IdP authority: $AUTH_AUTHORITY"
fi

echo "▶ starting realtime server on :5000 …"
fuser -k 5000/tcp 2>/dev/null || true   # stale server from a previous lane would squat the health check
sleep 0.5
(cd src/Realtime &&
    CONN_TOKEN_SECRET="${CONN_TOKEN_SECRET:-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef}" \
    AUTH_AUTHORITY="$AUTH_AUTHORITY" \
    ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_URLS="$SRV_URL" \
    dotnet run &> /tmp/realtime-server.log &)

echo "  waiting for health …"
healthy=""
for _ in $(seq 1 90); do
  if healthy=$(curl -fsS "$SRV_URL/healthz" 2>/dev/null); then break; fi
  sleep 0.5
done
if [[ -z "$healthy" ]]; then
  echo "server did not come up — logs: /tmp/realtime-server.log"; tail -25 /tmp/realtime-server.log; exit 1
fi
echo "  health: $healthy"

echo "▶ running e2e ($IDP_MODE mode) …"
E2E_ENV="IDP_MODE=$IDP_MODE AUTH_AUTHORITY=$AUTH_AUTHORITY SRV=$SRV_URL"
if [[ "$IDP_MODE" == "real" ]]; then
  CENTRIFUGO_ENV="${CENTRIFUGO_ENV:-/mnt/lfs/Users/adolf/source/repos/centrifugo/.env}"
  if [[ ! -f "$CENTRIFUGO_ENV" ]]; then echo "real mode needs $CENTRIFUGO_ENV with AUTH_SERVICE_* credentials"; exit 1; fi
  while IFS='=' read -r key value; do
    case "$key" in
      # export both the raw AUTH_SERVICE_* names and the e2e-familiar IDP_*
      # aliases so node e2e.mjs can mint fresh tokens when the cache misses
      AUTH_SERVICE_CLIENT_ID)    export "$key=$value" IDP_CLIENT_ID="$value" ;;
      AUTH_SERVICE_CLIENT_SECRET) export "$key=$value" IDP_CLIENT_SECRET="$value" ;;
      AUTH_SERVICE_SCOPES)       export "$key=$value" IDP_CLIENT_SCOPES="$value" ;;
    esac
  done < "$CENTRIFUGO_ENV"
  E2E_ENV="$E2E_ENV IDP_CLIENT_ID=$AUTH_SERVICE_CLIENT_ID"
fi
cd dev/e2e
env $E2E_ENV node e2e.mjs
