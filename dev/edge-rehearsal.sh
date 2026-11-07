# Edge rehearsal — Phase 5 proof that the ONE-route edge shape works end-to-end
# BEFORE touching the central Caddy (deploy on the server = paste
# deploy/caddy-route.caddy; this local Caddyfile replicates it, http-only).
#
# Pipeline: mock-idp on host :9001 → docker compose (deploy/dev/edge) up:
#   realtime:latest (PROD env shape, no host ports) + caddy:2 → :8080
# The full e2e drives ONLY the edge (SRV=http://localhost:8080, EDGE prefix).
#
# usage: bash dev/edge-rehearsal.sh [-keep]   # optional: keep containers up
set -uo pipefail
cd "$(dirname "$0")/.."

KEEP=false
[ "${1:-}" = "-keep" ] && KEEP=true

docker stop auth-server &>/dev/null || true   # the wall stack squats :9001
fuser -k 9001/tcp &>/dev/null || true
sleep 0.5

# the ISSUER string must match what the CONTAINER trusts (iss is compared
# exactly), so the gateway hostname — not localhost — is the canonical
# authority spelled on all legs (token iss, container env, e2e cache check)
ISSUER="http://host.docker.internal:9001/realms/demo"

(cd dev/mock-idp &&
  KEYS_DIR="$PWD/keys" ISSUER="$ISSUER" node server.js &> /tmp/edge-idp.log &)
sleep 0.7

overall=0
docker compose -f dev/edge/docker-compose.yml up -d --force-recreate || exit 1

# wait for the EDGE (caddy depends_on realtime healthy): the first realtime
# answer through the edge is a 401 on the fence — that IS the arrival signal
up=0
for _ in $(seq 1 60); do
  code=$(curl -sS -o /dev/null -w '%{http_code}' http://localhost:8080/realtime-v2/api/groups 2>/dev/null)
  if [ "$code" = 401 ] || [ "$code" = 200 ]; then
    up=1; break
  fi
  sleep 0.5
done
[ "$up" = 1 ] || { echo "edge did not come up — docker compose -f dev/edge/docker-compose.yml logs"; exit 1; }

# edge sanity: /metrics + /openapi MUST stay internal (404 through the edge)
metrics_code=$(curl -sS -o /dev/null -w '%{http_code}' http://localhost:8080/realtime-v2/metrics)
openapi_code=$(curl -sS -o /dev/null -w '%{http_code}' http://localhost:8080/realtime-v2/openapi/v1.json)
echo "edge surface check: /metrics=$metrics_code /openapi=$openapi_code (want 404/404)"
{ [ "$metrics_code" = 404 ] && [ "$openapi_code" = 404 ]; } || overall=1

echo "── e2e through the edge ──────────────────────────"
(cd dev/e2e &&
  SRV="http://localhost:8080" EDGE="/realtime-v2" SRV_OPS="http://localhost:5001" \
  IDP_MODE=mock AUTH_AUTHORITY="$ISSUER" node e2e.mjs) || overall=1

if $KEEP; then
  echo "(containers left running — 'docker compose -f dev/edge/docker-compose.yml down' when done)"
else
  docker compose -f dev/edge/docker-compose.yml down &>/dev/null
  fuser -k 9001/tcp &>/dev/null || true
fi
exit $overall
