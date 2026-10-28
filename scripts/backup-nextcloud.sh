#!/usr/bin/env bash
# `make backup-nextcloud`: compressed pg_dump of the running db container,
# uploaded to a Nextcloud folder over WebDAV, plus a copy of .env next to it
# (couplescash-db-<stamp>.env). The dump alone cannot restore linked banks:
# their credentials are encrypted with DATA_KEY, which lives only in .env.
# Settings come from the environment, else from .env:
#   NEXTCLOUD_URL            https://cloud.example.com (the server, no /remote.php…)
#   NEXTCLOUD_USER           your Nextcloud login name
#   NEXTCLOUD_APP_PASSWORD   Settings → Security → Devices & sessions → Create new app password
#   NEXTCLOUD_DIR            folder in your files (default CouplesCash/backups; created if missing)
#   NEXTCLOUD_KEEP           newest dump + .env pairs kept in that folder (default 42 = a week at every 4 hours)
# Logs carry file names and sizes only — never the password or .env contents.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# One value from .env (last assignment wins; surrounding quotes stripped). The
# file is not sourced: it is compose syntax, not shell.
env_value() {
  local line
  [[ -f .env ]] || return 0
  line="$(grep -E "^$1=" .env | tail -n 1 || true)"
  line="${line#*=}"
  line="${line%\"}"; line="${line#\"}"
  line="${line%\'}"; line="${line#\'}"
  printf '%s' "$line"
}

NEXTCLOUD_URL="${NEXTCLOUD_URL:-$(env_value NEXTCLOUD_URL)}"
NEXTCLOUD_USER="${NEXTCLOUD_USER:-$(env_value NEXTCLOUD_USER)}"
NEXTCLOUD_APP_PASSWORD="${NEXTCLOUD_APP_PASSWORD:-$(env_value NEXTCLOUD_APP_PASSWORD)}"
NEXTCLOUD_DIR="${NEXTCLOUD_DIR:-$(env_value NEXTCLOUD_DIR)}"
NEXTCLOUD_DIR="${NEXTCLOUD_DIR:-CouplesCash/backups}"
NEXTCLOUD_KEEP="${NEXTCLOUD_KEEP:-$(env_value NEXTCLOUD_KEEP)}"
NEXTCLOUD_KEEP="${NEXTCLOUD_KEEP:-42}"
DB_USER="${DB_USER:-couplescash}"
DB_NAME="${DB_NAME:-couplescash}"

log() { printf '%s backup-nextcloud: %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*"; }
die() { log "$*" >&2; exit 1; }

missing=()
[[ -n "$NEXTCLOUD_URL" ]] || missing+=(NEXTCLOUD_URL)
[[ -n "$NEXTCLOUD_USER" ]] || missing+=(NEXTCLOUD_USER)
[[ -n "$NEXTCLOUD_APP_PASSWORD" ]] || missing+=(NEXTCLOUD_APP_PASSWORD)
(( ${#missing[@]} == 0 )) || die "set ${missing[*]} in .env (see .env.example)"
[[ "$NEXTCLOUD_URL" == https://* ]] || die "NEXTCLOUD_URL must start with https://"
[[ "$NEXTCLOUD_KEEP" =~ ^[1-9][0-9]*$ ]] || die "NEXTCLOUD_KEEP must be a positive number"

# A run that is still uploading (slow link) makes the next one skip, not pile up.
mkdir -p backups
chmod 700 backups
exec 9>backups/.nextcloud.lock
flock -n 9 || { log "previous run still in progress — skipping"; exit 0; }

if [[ -z "$(docker compose ps -q db 2>/dev/null)" ]]; then
  die "the db container is not running (make up)"
fi

# Percent-encode one path segment byte by byte (logins can be e-mail addresses).
urlencode() {
  local LC_ALL=C s="$1" out="" c i
  for (( i = 0; i < ${#s}; i++ )); do
    c="${s:i:1}"
    case "$c" in
      [a-zA-Z0-9.~_-]) out+="$c" ;;
      *) out+="$(printf '%%%02X' "'$c")" ;;
    esac
  done
  printf '%s' "$out"
}

# Credentials go to curl on stdin as a config file, so they never show up in
# the process list.
curl_config() {
  local cred="${NEXTCLOUD_USER}:${NEXTCLOUD_APP_PASSWORD}"
  cred="${cred//\\/\\\\}"
  cred="${cred//\"/\\\"}"
  printf 'user = "%s"\n' "$cred"
}

# dav METHOD URL [curl args…] → prints the HTTP status code.
dav() {
  local method="$1" url="$2"
  shift 2
  curl_config | curl --config - --silent --show-error --output /dev/null \
    --write-out '%{http_code}' --connect-timeout 20 --max-time 900 \
    --request "$method" "$@" "$url"
}

base="${NEXTCLOUD_URL%/}/remote.php/dav/files/$(urlencode "$NEXTCLOUD_USER")"

# Create the folder one level at a time (201 = created, 405 = already there).
dir_url="$base"
IFS='/' read -r -a parts <<< "$NEXTCLOUD_DIR"
for part in "${parts[@]}"; do
  [[ -n "$part" ]] || continue
  dir_url+="/$(urlencode "$part")"
  code="$(dav MKCOL "$dir_url/")" || die "could not reach $NEXTCLOUD_URL"
  case "$code" in
    201|405) ;;
    401) die "Nextcloud refused the login (401) — check NEXTCLOUD_USER / NEXTCLOUD_APP_PASSWORD" ;;
    *) die "could not create folder $NEXTCLOUD_DIR (HTTP $code)" ;;
  esac
done

work="$(mktemp -d)"
chmod 700 "$work"
trap 'rm -rf "$work"' EXIT
stamp="couplescash-db-$(date +%Y%m%d-%H%M%S)"
name="$stamp.sql.gz"
dump="$work/$name"

# Same flags as `make backup`, so `make restore FILE=…` takes either dump.
docker compose exec -T db pg_dump -U "$DB_USER" -d "$DB_NAME" \
  --clean --if-exists --no-owner --no-privileges \
  | gzip -9 > "$dump" || die "pg_dump failed — nothing uploaded"
gzip -t "$dump" || die "the dump is corrupt — not uploading"
size="$(du -h "$dump" | cut -f1)"

code="$(dav PUT "$dir_url/$name" --upload-file "$dump" --retry 3 --retry-all-errors)" \
  || die "upload of $name failed (network)"
[[ "$code" == 201 || "$code" == 204 ]] || die "upload of $name failed (HTTP $code)"
log "uploaded $name ($size) to $NEXTCLOUD_DIR"

# .env holds DATA_KEY (bank credentials), JWT_SECRET, VAPID keys, the DB
# password and this script's app password — keep the Nextcloud folder private.
if [[ -f .env ]]; then
  code="$(dav PUT "$dir_url/$stamp.env" --upload-file .env --retry 3 --retry-all-errors)" \
    || die "uploaded $name, but the .env copy failed (network)"
  [[ "$code" == 201 || "$code" == 204 ]] || die "uploaded $name, but the .env copy failed (HTTP $code)"
  log "uploaded $stamp.env (DATA_KEY and the other secrets — keep it with the dump)"
else
  log "no .env found — bank links cannot be restored from the dump alone" >&2
fi

# Rotation: list the folder, keep the newest $NEXTCLOUD_KEEP dumps (the
# timestamp in the name sorts by age) and the .env copies that go with them,
# and leave every other file alone.
listing="$(curl_config | curl --config - --silent --show-error --fail \
  --connect-timeout 20 --max-time 120 --request PROPFIND --header 'Depth: 1' "$dir_url/")" \
  || die "uploaded, but could not list $NEXTCLOUD_DIR to prune old dumps"
mapfile -t files < <(grep -oE 'couplescash-db-[0-9]{8}-[0-9]{6}\.(sql\.gz|env)' <<< "$listing" | sort -u)
mapfile -t kept < <(printf '%s\n' "${files[@]}" | grep -E '\.sql\.gz$' \
  | sort -r | head -n "$NEXTCLOUD_KEEP" | sed 's/\.sql\.gz$//')
pruned=0
for file in "${files[@]}"; do
  base_name="${file%.sql.gz}"
  base_name="${base_name%.env}"
  printf '%s\n' "${kept[@]}" | grep -qxF "$base_name" && continue
  code="$(dav DELETE "$dir_url/$file")" || code=000
  if [[ "$code" == 204 || "$code" == 404 ]]; then
    pruned=$((pruned + 1))
  else
    log "could not delete old backup file $file (HTTP $code)" >&2
  fi
done
(( pruned == 0 )) || log "pruned $pruned old file(s), keeping the newest $NEXTCLOUD_KEEP dump(s) and their .env copies"
