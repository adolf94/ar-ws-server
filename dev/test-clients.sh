# ── client-library conformance harness ──────────────────────────────────────
#   Boots the mock lane (offline IdP + server) and runs each client lib's
#   conformance suite against it. Common orchestration; per-lib suites are
#   scripts/conformance.mjs (ts) etc., wired per follow-up.
#
#   usage: bash dev/test-clients.sh [ts]   # dotnet/python land later
set -uo pipefail
cd "$(dirname "$0")/.."

REPO_ROOT="$PWD"
SRV_URL="${SRV_URL:-http://localhost:5000}"
AUTH_AUTHORITY="${AUTH_AUTHORITY:-http://localhost:9001/realms/demo}"

start_stack() {
  docker stop auth-server >/dev/null 2>&1 || true   # wall stack squats :9001
  fuser -k 9001/tcp >/dev/null 2>&1 || true
  fuser -k 5000/tcp >/dev/null 2>&1 || true
  sleep 0.5
  (cd "$REPO_ROOT/dev/mock-idp" &&
    KEYS_DIR="$PWD/keys" ISSUER="$AUTH_AUTHORITY" node server.js &> /tmp/realtime-clients-idp.log &)
  sleep 0.7
  (cd "$REPO_ROOT/src/Realtime" &&
    CONN_TOKEN_SECRET="0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" \
    AUTH_AUTHORITY="$AUTH_AUTHORITY" ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_URLS="$SRV_URL" \
    dotnet run &> /tmp/realtime-clients-server.log &)
  for _ in $(seq 1 60); do
    if curl -fsS "$SRV_URL/healthz" >/dev/null 2>&1; then return 0; fi
    sleep 0.5
  done
  echo "server did not come up — /tmp/realtime-clients-server.log"; return 1
}

stop_stack() {
  fuser -k 5000/tcp >/dev/null 2>&1 || true
  fuser -k 9001/tcp >/dev/null 2>&1 || true
}

overall=0
run_ts() {
  echo "── clients/ts ────────────────────────────────"
  (cd "$REPO_ROOT/clients/ts" &&
    SRV="$SRV_URL" MOCK_AUTH="http://localhost:9001" node scripts/conformance.mjs)
}

wantTs=true; wantDotnet=false; wantPython=false
case "${1:-all}" in
  ts) wantDotnet=false; wantPython=false ;;
  dotnet) wantTs=false; wantDotnet=true ;;
  python) wantTs=false; wantPython=true ;;
esac
start_stack || exit 1

if $wantTs; then
  run_ts || overall=1
fi
if $wantDotnet; then
  echo "── clients/dotnet ────────────────────────────"
  (cd "$REPO_ROOT/clients/dotnet" &&
    SRV="$SRV_URL" MOCK_AUTH="http://localhost:9001" dotnet test tests/Ar.Realtime.Client.Tests -v minimal) || overall=1
fi
if $wantPython; then
  echo "── clients/python ────────────────────────────"
  (cd "$REPO_ROOT/clients/python" &&
    SRV="$SRV_URL" MOCK_AUTH="http://localhost:9001" uv run --extra dev pytest tests -q) || overall=1
fi

stop_stack
exit $overall
