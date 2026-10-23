#!/usr/bin/env bash
# `make dev`: Postgres in Docker, API (dotnet watch) + web (vite dev) natively,
# both in the foreground with prefixed output. Ctrl-C stops api + web; the
# database keeps running (stop it with `make down`).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

COMPOSE_DEV=(docker compose -f compose.yml -f compose.dev.yml)
API_DIR="apps/api/CouplesCash.Api"
WEB_DIR="apps/web"

bold() { printf '\033[1m%s\033[0m\n' "$*"; }
log()  { printf '\033[90m[dev]\033[0m %s\n' "$*"; }

for tool in docker dotnet pnpm; do
  command -v "$tool" >/dev/null 2>&1 || { echo "dev.sh: '$tool' not found in PATH (run make setup)" >&2; exit 1; }
done

# 1. Secrets & derived env ---------------------------------------------------
if [[ ! -f .env ]]; then
  cp .env.example .env && chmod 600 .env
  log "created .env from .env.example"
fi
if grep -qE '^(JWT_SECRET|POSTGRES_PASSWORD)=\s*$' .env; then
  log "filling empty secrets in .env"
  make --no-print-directory secrets >/dev/null
fi
scripts/dev-env.sh >/dev/null
set -a
# shellcheck disable=SC1091
source ./.env.dev
set +a

# 2. Database ----------------------------------------------------------------
log "starting postgres (127.0.0.1:5432)…"
"${COMPOSE_DEV[@]}" up -d db >/dev/null

db_cid="$("${COMPOSE_DEV[@]}" ps -q db)"
for _ in $(seq 1 60); do
  status="$(docker inspect -f '{{.State.Health.Status}}' "$db_cid" 2>/dev/null || echo starting)"
  [[ "$status" == healthy ]] && break
  sleep 1
done
if [[ "${status:-}" != healthy ]]; then
  echo "dev.sh: postgres did not become healthy; see: docker compose logs db" >&2
  exit 1
fi
log "postgres is healthy"

# 3. API + web in parallel ---------------------------------------------------
cleanup() {
  trap '' INT TERM EXIT
  echo
  log "stopping api + web…"
  # Everything we spawned shares this process group; ignore the signal
  # ourselves (trap above) and let the children die.
  kill 0 2>/dev/null || true
  wait 2>/dev/null || true
  log "stopped. Postgres is still running — 'make down' to stop it."
}
trap cleanup INT TERM EXIT

prefix() { # prefix <label> <colour>
  local label="$1" colour="$2"
  sed -u "s/^/$(printf '\033[%sm[%s]\033[0m ' "$colour" "$label")/"
}

(
  cd "$API_DIR"
  exec dotnet watch run --no-launch-profile --non-interactive
) 2>&1 | prefix api 34 &

(
  cd "$WEB_DIR"
  exec pnpm dev --host
) 2>&1 | prefix web 35 &

echo
bold "  web   → ${ORIGIN:-http://localhost:5173}"
bold "  api   → ${ASPNETCORE_URLS:-http://localhost:8080}  (docs: /scalar, health: /health)"
bold "  db    → postgres://couplescash@127.0.0.1:5432/couplescash"
echo "  Ctrl-C to stop."
echo

wait -n 2>/dev/null || true   # if either process exits, tear the other down too
