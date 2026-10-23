#!/usr/bin/env bash
# `make backup`: compressed pg_dump of the running db container into ./backups.
# Usage: scripts/backup.sh [KEEP=14]   — keeps the newest $KEEP dumps.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

KEEP="${KEEP:-14}"
BACKUP_DIR="${BACKUP_DIR:-backups}"
DB_USER="${DB_USER:-couplescash}"
DB_NAME="${DB_NAME:-couplescash}"
stamp="$(date +%Y%m%d-%H%M%S)"
out="${BACKUP_DIR}/couplescash-${stamp}.sql.gz"

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

# Rotation: keep the newest $KEEP dumps.
mapfile -t old < <(find "$BACKUP_DIR" -maxdepth 1 -name 'couplescash-*.sql.gz' -printf '%T@ %p\n' \
  | sort -rn | cut -d' ' -f2- | tail -n +"$((KEEP + 1))")
if (( ${#old[@]} > 0 )); then
  rm -f "${old[@]}"
  echo "pruned ${#old[@]} old backup(s), keeping $KEEP"
fi
