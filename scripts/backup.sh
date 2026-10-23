#!/usr/bin/env bash
# `make backup`: compressed pg_dump of the running db container into ./backups,
# plus a copy of .env next to it (couplescash-<stamp>.env, mode 600). The dump
# alone cannot restore linked banks: their SimpleFIN credentials are encrypted
# with DATA_KEY, which lives only in .env.
# Usage: scripts/backup.sh [KEEP=14]   — keeps the newest $KEEP dump+env pairs.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

KEEP="${KEEP:-14}"
BACKUP_DIR="${BACKUP_DIR:-backups}"
DB_USER="${DB_USER:-couplescash}"
DB_NAME="${DB_NAME:-couplescash}"
stamp="$(date +%Y%m%d-%H%M%S)"
out="${BACKUP_DIR}/couplescash-${stamp}.sql.gz"
env_out="${BACKUP_DIR}/couplescash-${stamp}.env"

mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"

if [[ -z "$(docker compose ps -q db 2>/dev/null)" ]]; then
  echo "backup.sh: the db container is not running (make up / make dev-db)" >&2
  exit 1
fi

tmp="${out}.part"
trap 'rm -f "$tmp"' ERR INT TERM
# --clean/--if-exists make the dump restorable over an existing schema;
# --no-owner keeps it portable between role names.
docker compose exec -T db pg_dump -U "$DB_USER" -d "$DB_NAME" \
  --clean --if-exists --no-owner --no-privileges \
  | gzip -9 > "$tmp"
mv "$tmp" "$out"
chmod 600 "$out"

size="$(du -h "$out" | cut -f1)"
echo "wrote $out ($size)"

# .env holds DATA_KEY (bank credentials), JWT_SECRET, VAPID keys and the DB
# password — without it a restored dump has unreadable bank links. Same
# permissions as the dump; treat the folder as secret.
if [[ -f .env ]]; then
  install -m 600 .env "$env_out"
  echo "wrote $env_out (DATA_KEY, JWT_SECRET, VAPID keys — keep it with the dump)"
else
  echo "backup.sh: no .env found — bank links cannot be restored from the dump alone" >&2
fi

# Rotation: keep the newest $KEEP dumps, then drop .env copies whose dump is gone.
mapfile -t old < <(find "$BACKUP_DIR" -maxdepth 1 -name 'couplescash-*.sql.gz' -printf '%T@ %p\n' \
  | sort -rn | cut -d' ' -f2- | tail -n +"$((KEEP + 1))")
if (( ${#old[@]} > 0 )); then
  rm -f "${old[@]}"
  echo "pruned ${#old[@]} old backup(s), keeping $KEEP"
fi
for env_copy in "$BACKUP_DIR"/couplescash-*.env; do
  [[ -f "$env_copy" && ! -f "${env_copy%.env}.sql.gz" ]] && rm -f "$env_copy"
done
