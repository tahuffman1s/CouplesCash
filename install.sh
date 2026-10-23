#!/usr/bin/env bash
# CouplesCash one-line installer — any 64-bit Linux (Raspberry Pi OS, Debian, Ubuntu,
# Fedora, RHEL/Rocky/Alma, Arch, Alpine, openSUSE).
#
#   curl -fsSL https://raw.githubusercontent.com/tahuffman1s/CouplesCash/main/install.sh | bash
#
# What it does (idempotent — re-run it to update):
#   1. checks the CPU is 64-bit and finds your package manager
#   2. installs curl, git, openssl (+ ca-certificates) if missing
#   3. installs Docker Engine + the compose plugin, starts it, adds you to the docker group
#   4. clones (or updates) the repo into ~/couplescash
#   5. writes .env with fresh secrets and asks for your public URL + Cloudflare tunnel token
#   6. pulls the arm64/amd64 images (or builds them if none are published)
#   7. generates the Web Push (VAPID) key pair with the api image, then starts the stack
#
# Options (flags or environment variables):
#   --dir DIR            COUPLESCASH_DIR      install location          (default ~/couplescash)
#   --branch NAME        COUPLESCASH_BRANCH   git branch to install     (default main)
#   --public-url URL     PUBLIC_URL           e.g. https://cash.example.com
#   --tunnel-token TOK   TUNNEL_TOKEN         Cloudflare tunnel token (omit for LAN-only mode)
#   --email ADDR         VAPID_EMAIL          contact for push services (VAPID_SUBJECT; default admin@<host of PUBLIC_URL>)
#   --pi                 COUPLESCASH_PI=1     Raspberry Pi mode: require arm64, set up zram swap, check NTP,
#                                             and refuse to build images locally (pull only). Auto-detected
#                                             on real Pi hardware; the flag forces it (e.g. in a VM/container).
#   --lan                COUPLESCASH_LAN=1    skip the tunnel; serve http://<this-host>:3000 on the LAN
#   --no-start           COUPLESCASH_NO_START=1   install and configure, but don't start containers
#   --build              COUPLESCASH_BUILD=1  build images locally instead of pulling (slow on a Pi)
#   -y, --yes            NONINTERACTIVE=1     never prompt; use flags/env/defaults
#   -h, --help
set -euo pipefail

REPO_URL="${REPO_URL:-https://github.com/tahuffman1s/CouplesCash.git}"
INSTALL_DIR="${COUPLESCASH_DIR:-$HOME/couplescash}"
BRANCH="${COUPLESCASH_BRANCH:-main}"
PUBLIC_URL="${PUBLIC_URL:-}"
TUNNEL_TOKEN="${TUNNEL_TOKEN:-}"
VAPID_EMAIL="${VAPID_EMAIL:-}"
PI_MODE="${COUPLESCASH_PI:-0}"
LAN_MODE="${COUPLESCASH_LAN:-0}"
NO_START="${COUPLESCASH_NO_START:-0}"
BUILD_LOCAL="${COUPLESCASH_BUILD:-0}"
NONINTERACTIVE="${NONINTERACTIVE:-0}"

usage() {
  if [[ -f "$0" ]]; then sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'
  else printf 'CouplesCash installer. Options: --pi --lan --dir DIR --branch NAME --public-url URL --tunnel-token TOK --email ADDR --no-start --build -y\n'; fi
}

while (( $# )); do
  case "$1" in
    --dir) INSTALL_DIR="$2"; shift 2 ;;
    --branch) BRANCH="$2"; shift 2 ;;
    --public-url) PUBLIC_URL="$2"; shift 2 ;;
    --tunnel-token) TUNNEL_TOKEN="$2"; shift 2 ;;
    --email) VAPID_EMAIL="$2"; shift 2 ;;
    --pi) PI_MODE=1; shift ;;
    --lan) LAN_MODE=1; shift ;;
    --no-start) NO_START=1; shift ;;
    --build) BUILD_LOCAL=1; shift ;;
    -y|--yes) NONINTERACTIVE=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) printf 'unknown option: %s\n\n' "$1" >&2; usage >&2; exit 2 ;;
  esac
done

# --- output helpers -----------------------------------------------------------
if [[ -t 1 ]]; then
  BOLD=$'\033[1m'; BLUE=$'\033[1;34m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; RED=$'\033[31m'; RESET=$'\033[0m'
else
  BOLD=''; BLUE=''; GREEN=''; YELLOW=''; RED=''; RESET=''
fi
step() { printf '\n%s==> %s%s\n' "$BLUE" "$*" "$RESET"; }
ok()   { printf '    %s✓%s %s\n' "$GREEN" "$RESET" "$*"; }
warn() { printf '    %s!%s %s\n' "$YELLOW" "$RESET" "$*"; }
die()  { printf '%serror:%s %s\n' "$RED" "$RESET" "$*" >&2; exit 1; }

# Prompts read from the terminal even when the script itself arrives on stdin via curl | bash.
can_prompt() { [[ "$NONINTERACTIVE" != 1 && -r /dev/tty && -w /dev/tty ]]; }
ask() { # ask VAR "Question" "default"
  local -n _out="$1"; local q="$2" def="${3:-}" ans=""
  if can_prompt; then
    if [[ -n "$def" ]]; then printf '%s [%s]: ' "$q" "$def" >/dev/tty; else printf '%s: ' "$q" >/dev/tty; fi
    IFS= read -r ans </dev/tty || true
  fi
  _out="${ans:-$def}"
}
ask_secret() { # ask_secret VAR "Question"
  local -n _out="$1"; local ans=""
  if can_prompt; then
    printf '%s (input hidden, Enter to skip): ' "$2" >/dev/tty
    IFS= read -rs ans </dev/tty || true; printf '\n' >/dev/tty
  fi
  _out="$ans"
}

# --- privilege helper ---------------------------------------------------------
if [[ "$(id -u)" -eq 0 ]]; then
  SUDO=""
  TARGET_USER="${SUDO_USER:-root}"
elif command -v sudo >/dev/null 2>&1; then
  SUDO="sudo"
  TARGET_USER="$USER"
elif command -v doas >/dev/null 2>&1; then
  SUDO="doas"
  TARGET_USER="$USER"
else
  die "need root privileges: install sudo (or doas), or run this script as root"
fi
if [[ -n "$SUDO" ]]; then
  printf 'This installer uses %s to install packages and Docker.\n' "$SUDO"
  $SUDO -v || die "could not obtain root privileges"
fi

# --- 1. platform --------------------------------------------------------------
step "Checking platform"
ARCH="$(uname -m)"
case "$ARCH" in
  x86_64|amd64) ok "architecture: amd64" ;;
  aarch64|arm64) ok "architecture: arm64" ;;
  armv7l|armv6l) die "32-bit ARM ($ARCH) detected. CouplesCash needs a 64-bit OS: reinstall Raspberry Pi OS (64-bit). 'uname -m' must print aarch64." ;;
  *) die "unsupported architecture: $ARCH (need x86_64 or aarch64)" ;;
esac

OS_ID="" OS_LIKE="" OS_NAME="Linux"
if [[ -r /etc/os-release ]]; then
  # shellcheck disable=SC1091
  . /etc/os-release
  OS_ID="${ID:-}"; OS_LIKE="${ID_LIKE:-}"; OS_NAME="${PRETTY_NAME:-$OS_ID}"
fi
ok "$OS_NAME"

PKG=""
if   command -v apt-get >/dev/null; then PKG=apt
elif command -v dnf     >/dev/null; then PKG=dnf
elif command -v yum     >/dev/null; then PKG=yum
elif command -v pacman  >/dev/null; then PKG=pacman
elif command -v apk     >/dev/null; then PKG=apk
elif command -v zypper  >/dev/null; then PKG=zypper
fi
[[ -n "$PKG" ]] || die "no supported package manager found (apt, dnf, yum, pacman, apk, zypper)"
ok "package manager: $PKG"

MEM_MB=$(( $(awk '/MemTotal/ {print $2}' /proc/meminfo) / 1024 ))
if (( MEM_MB < 1800 )); then
  warn "${MEM_MB} MB RAM — the stack needs ~700 MB; 2 GB is the minimum, 4 GB recommended"
else
  ok "${MEM_MB} MB RAM"
fi

if grep -qi raspberry /proc/device-tree/model 2>/dev/null; then
  PI_MODE=1; ok "$(tr -d '\0' </proc/device-tree/model)"
elif [[ "$PI_MODE" == 1 ]]; then
  ok "Raspberry Pi mode forced with --pi"
fi
if [[ "$PI_MODE" == 1 ]]; then
  [[ "$ARCH" == aarch64 || "$ARCH" == arm64 ]] || die "--pi needs a 64-bit ARM OS (got $ARCH). Flash Raspberry Pi OS (64-bit)."
  if [[ "$BUILD_LOCAL" == 1 ]]; then
    warn "--build on a Pi 4 takes 15–30 minutes and ~2 GB of RAM; pulling prebuilt images is strongly preferred"
  fi
fi

# --- 2. base packages ---------------------------------------------------------
step "Base packages"
pkg_install() {
  case "$PKG" in
    apt)    $SUDO apt-get update -qq; $SUDO env DEBIAN_FRONTEND=noninteractive apt-get install -y -qq "$@" ;;
    dnf)    $SUDO dnf install -y -q "$@" ;;
    yum)    $SUDO yum install -y -q "$@" ;;
    pacman) $SUDO pacman -Sy --noconfirm --needed "$@" ;;
    apk)    $SUDO apk add --no-cache "$@" ;;
    zypper) $SUDO zypper --non-interactive install "$@" ;;
  esac
}
missing=()
command -v curl    >/dev/null || missing+=(curl)
command -v git     >/dev/null || missing+=(git)
command -v openssl >/dev/null || missing+=(openssl)
case "$PKG" in apt|dnf|yum|zypper|apk) missing+=(ca-certificates) ;; esac
if (( ${#missing[@]} )); then
  pkg_install "${missing[@]}" >/dev/null
  ok "installed: ${missing[*]}"
else
  ok "curl, git, openssl present"
fi

# --- 3. docker ----------------------------------------------------------------
step "Docker"
install_docker() {
  case "$OS_ID $OS_LIKE" in
    *arch*|*manjaro*)
      pkg_install docker docker-compose docker-buildx ;;
    *alpine*)
      pkg_install docker docker-cli-compose docker-cli-buildx ;;
    *suse*|*sles*)
      pkg_install docker docker-compose docker-buildx ;;
    *)
      # Debian, Ubuntu, Raspberry Pi OS, Fedora, RHEL family: Docker's official convenience script
      local tmp; tmp="$(mktemp)"
      curl -fsSL https://get.docker.com -o "$tmp"
      $SUDO sh "$tmp" >/dev/null
      rm -f "$tmp" ;;
  esac
}
if command -v docker >/dev/null 2>&1; then
  ok "Docker present: $(docker --version 2>/dev/null || echo installed)"
else
  install_docker
  ok "Docker installed"
fi

if ! docker compose version >/dev/null 2>&1 && ! $SUDO docker compose version >/dev/null 2>&1; then
  case "$PKG" in
    apt) pkg_install docker-compose-plugin >/dev/null ;;
    dnf|yum) pkg_install docker-compose-plugin >/dev/null ;;
    pacman) pkg_install docker-compose >/dev/null ;;
    apk) pkg_install docker-cli-compose >/dev/null ;;
    zypper) pkg_install docker-compose >/dev/null ;;
  esac
fi
ok "compose: $($SUDO docker compose version --short 2>/dev/null || echo present)"

# start + enable the daemon (systemd or OpenRC); harmless if already running
if command -v systemctl >/dev/null 2>&1 && [[ -d /run/systemd/system ]]; then
  if $SUDO systemctl enable --now docker >/dev/null 2>&1; then ok "docker service enabled"; else warn "could not enable docker via systemd"; fi
elif command -v rc-update >/dev/null 2>&1; then
  $SUDO rc-update add docker default >/dev/null 2>&1 || true
  if $SUDO rc-service docker start >/dev/null 2>&1; then ok "docker service started (OpenRC)"; else warn "could not start docker via OpenRC"; fi
else
  warn "no systemd/OpenRC found — make sure the docker daemon is running"
fi

RELOGIN=0
if [[ "$TARGET_USER" != root ]] && ! id -nG "$TARGET_USER" | tr ' ' '\n' | grep -qx docker; then
  $SUDO usermod -aG docker "$TARGET_USER" && RELOGIN=1
  warn "added $TARGET_USER to the docker group (takes effect after you log out and back in)"
fi

# Docker command for the rest of this run: plain if it works, otherwise via sudo.
DOCKER_OK=1
if docker info >/dev/null 2>&1; then
  DOCKER=(docker)
elif $SUDO docker info >/dev/null 2>&1; then
  DOCKER=("$SUDO" docker)
else
  if [[ "$NO_START" == 1 ]]; then
    warn "docker daemon not reachable — continuing because --no-start was given"
    DOCKER=(docker); DOCKER_OK=0
  else
    die "docker daemon is not running. Start it (e.g. 'sudo systemctl start docker') and re-run this script."
  fi
fi

# log rotation + live-restore so the Pi's SD card doesn't fill up (only when no config exists yet)
DAEMON_JSON=/etc/docker/daemon.json
if [[ ! -s "$DAEMON_JSON" ]]; then
  $SUDO install -d -m 755 /etc/docker
  printf '{\n  "log-driver": "json-file",\n  "log-opts": { "max-size": "10m", "max-file": "3" },\n  "live-restore": true\n}\n' | $SUDO tee "$DAEMON_JSON" >/dev/null
  if command -v systemctl >/dev/null 2>&1 && [[ -d /run/systemd/system ]]; then $SUDO systemctl restart docker >/dev/null 2>&1 || true; fi
  ok "wrote $DAEMON_JSON (log rotation, live-restore)"
fi

# --- 4. Raspberry Pi mode -----------------------------------------------------
if [[ "$PI_MODE" == 1 ]]; then
  step "Raspberry Pi setup"
  # Login tokens are time-based; a Pi has no RTC, so make sure NTP is on.
  if command -v timedatectl >/dev/null 2>&1; then
    if timedatectl show -p NTPSynchronized --value 2>/dev/null | grep -qi yes; then
      ok "clock synchronised via NTP"
    else
      $SUDO timedatectl set-ntp true >/dev/null 2>&1 || true
      warn "clock not yet NTP-synchronised; check 'timedatectl' if logins fail"
    fi
  fi
  # Compressed swap in RAM: cheap insurance against OOM on a 2 GB Pi, and no SD-card wear.
  if swapon --show --noheadings 2>/dev/null | grep -q .; then
    ok "swap active: $(swapon --show --noheadings --bytes 2>/dev/null | awk '{s+=$3} END {printf "%d MB", s/1024/1024}')"
  elif [[ "$PKG" == apt ]]; then
    pkg_install zram-tools >/dev/null
    printf 'ALGO=zstd\nPERCENT=25\n' | $SUDO tee /etc/default/zramswap >/dev/null
    $SUDO systemctl enable --now zramswap >/dev/null 2>&1 || $SUDO systemctl restart zramswap >/dev/null 2>&1 || true
    ok "zram swap enabled (zstd, 25% of RAM)"
  else
    warn "no swap configured — consider enabling zram for your distro"
  fi
  # Image pulls are much faster than local builds on a Pi 4; make the fallback explicit.
  PI_PULL_ONLY=1
fi

# --- 5. repository ------------------------------------------------------------
step "Repository → $INSTALL_DIR"
if [[ -d "$INSTALL_DIR/.git" ]]; then
  git -C "$INSTALL_DIR" fetch --quiet origin "$BRANCH"
  git -C "$INSTALL_DIR" checkout --quiet "$BRANCH" 2>/dev/null || git -C "$INSTALL_DIR" checkout --quiet -b "$BRANCH" "origin/$BRANCH"
  git -C "$INSTALL_DIR" pull --ff-only --quiet origin "$BRANCH"
  ok "updated to $(git -C "$INSTALL_DIR" rev-parse --short HEAD) ($BRANCH)"
else
  git clone --quiet --depth 1 --branch "$BRANCH" "$REPO_URL" "$INSTALL_DIR"
  ok "cloned $REPO_URL ($BRANCH)"
fi
mkdir -p "$INSTALL_DIR/backups" && chmod 700 "$INSTALL_DIR/backups"
cd "$INSTALL_DIR"

# --- 6. configuration ---------------------------------------------------------
step "Configuration (.env)"
ENV_FILE="$INSTALL_DIR/.env"
set_env() { # set_env KEY VALUE  — replace or append, keeping the template's comments
  local key="$1" val="$2"
  if grep -qE "^#?\s*${key}=" "$ENV_FILE"; then
    # use a delimiter that cannot appear in base64/hex/URLs/tokens
    sed -i "s|^#\?\s*${key}=.*|${key}=${val}|" "$ENV_FILE"
  else
    printf '%s=%s\n' "$key" "$val" >>"$ENV_FILE"
  fi
}
get_env() { grep -E "^$1=" "$ENV_FILE" 2>/dev/null | head -n1 | cut -d= -f2- || true; }

if [[ ! -f "$ENV_FILE" ]]; then
  cp .env.example "$ENV_FILE"
  chmod 600 "$ENV_FILE"
  ok "created .env from .env.example"
fi
[[ -n "$(get_env JWT_SECRET)" ]]        || { set_env JWT_SECRET "$(openssl rand -base64 48 | tr -d '\n')"; ok "generated JWT_SECRET"; }
[[ -n "$(get_env POSTGRES_PASSWORD)" ]] || { set_env POSTGRES_PASSWORD "$(openssl rand -hex 24)"; ok "generated POSTGRES_PASSWORD"; }

# the first non-loopback IPv4 of this host, for LAN mode
host_ip() { hostname -I 2>/dev/null | awk '{print $1}' || ip -4 route get 1.1.1.1 2>/dev/null | awk '{print $7; exit}'; }

existing_url="$(get_env PUBLIC_URL)"; [[ "$existing_url" == "https://cash.example.com" ]] && existing_url=""
existing_tok="$(get_env TUNNEL_TOKEN)"

if [[ "$LAN_MODE" != 1 && -z "$TUNNEL_TOKEN" && -z "$existing_tok" ]]; then
  if can_prompt; then
    printf '\n%sHow will you reach CouplesCash?%s\n' "$BOLD" "$RESET" >/dev/tty
    printf '  1) Through a Cloudflare Tunnel (recommended: works from anywhere, no port forwarding)\n' >/dev/tty
    printf '  2) On my home network only (http://%s:3000)\n' "$(host_ip)" >/dev/tty
    choice=""; ask choice "Choose" "1"
    [[ "$choice" == 2 ]] && LAN_MODE=1
  else
    LAN_MODE=1
    warn "no tunnel token given and no terminal to ask — configuring LAN-only mode"
  fi
fi

if [[ "$LAN_MODE" == 1 ]]; then
  PUBLIC_URL="${PUBLIC_URL:-${existing_url:-http://$(host_ip):3000}}"
  set_env PUBLIC_URL "$PUBLIC_URL"
  set_env WEB_BIND "0.0.0.0"
  sed -i 's|^COMPOSE_PROFILES=.*|# COMPOSE_PROFILES=tunnel|' "$ENV_FILE"
  ok "LAN mode: $PUBLIC_URL (no HTTPS — fine for testing; use the tunnel for daily use)"
  warn "push notifications and passkeys need HTTPS: the app hides them until you configure a tunnel (infra/cloudflared/README.md)"
else
  if [[ -z "$PUBLIC_URL" ]]; then
    PUBLIC_URL="$existing_url"
    while [[ -z "$PUBLIC_URL" ]]; do
      ask PUBLIC_URL "Public URL, exactly as typed in the browser (e.g. https://cash.example.com)" ""
      [[ -n "$PUBLIC_URL" ]] || { can_prompt || die "PUBLIC_URL is required (pass --public-url or set PUBLIC_URL)"; }
    done
  fi
  PUBLIC_URL="${PUBLIC_URL%/}"
  [[ "$PUBLIC_URL" =~ ^https?:// ]] || die "PUBLIC_URL must start with https:// (got '$PUBLIC_URL')"
  if [[ -z "$TUNNEL_TOKEN" ]]; then
    TUNNEL_TOKEN="$existing_tok"
    if [[ -z "$TUNNEL_TOKEN" ]]; then
      printf '\n  Create the tunnel in Cloudflare Zero Trust → Networks → Tunnels → Create → Cloudflared.\n' >/dev/tty 2>/dev/null || true
      printf '  Public hostname: %s  →  Service: http://web:3000\n  Copy the token from the "Install connector" command (the long string after --token).\n\n' "${PUBLIC_URL#*://}" >/dev/tty 2>/dev/null || true
      ask_secret TUNNEL_TOKEN "Cloudflare tunnel token"
    fi
  fi
  set_env PUBLIC_URL "$PUBLIC_URL"
  set_env WEB_BIND "127.0.0.1"
  if [[ -n "$TUNNEL_TOKEN" ]]; then
    set_env TUNNEL_TOKEN "$TUNNEL_TOKEN"
    set_env COMPOSE_PROFILES "tunnel"
    ok "tunnel mode: $PUBLIC_URL"
  else
    warn "no tunnel token yet — the stack will start without cloudflared. Add TUNNEL_TOKEN to .env and re-run."
  fi
fi
set_env TZ "$(timedatectl show -p Timezone --value 2>/dev/null || cat /etc/timezone 2>/dev/null || echo UTC)"

# Web Push contact (VAPID subject). Push services (Apple, Google, Mozilla) may use it to
# reach the server operator; it is never shown to users. Defaults to admin@<public host>.
existing_subj="$(get_env VAPID_SUBJECT)"; [[ "$existing_subj" == "mailto:you@example.com" ]] && existing_subj=""
if [[ -n "$VAPID_EMAIL" || -z "$existing_subj" ]]; then
  public_host="${PUBLIC_URL#*://}"; public_host="${public_host%%[:/]*}"
  email="$VAPID_EMAIL"
  [[ -n "$email" ]] || ask email "Contact email for push notifications (only sent to Apple/Google push services)" "admin@${public_host:-localhost}"
  set_env VAPID_SUBJECT "mailto:${email#mailto:}"
  ok "VAPID_SUBJECT=mailto:${email#mailto:}"
fi
chmod 600 "$ENV_FILE"

# --- 7. images ----------------------------------------------------------------
if [[ "$NO_START" == 1 ]]; then
  step "Skipping image pull and start (--no-start)"
  printf '\n  Start it later with:  cd %s && make up      # or: make tunnel-up\n' "$INSTALL_DIR"
else
  step "Images"
  if [[ "$BUILD_LOCAL" == 1 ]]; then
    warn "building images locally — on a Pi 4 this takes 15–30 minutes"
    "${DOCKER[@]}" compose build
  elif ! "${DOCKER[@]}" compose --profile tunnel pull 2>/dev/null; then
    if [[ "${PI_PULL_ONLY:-0}" == 1 ]]; then
      die "could not pull the images. Either CI has not published them yet, or the GHCR packages are private (GitHub → your profile → Packages → couplescash-api/web → Package settings → Change visibility → Public). Alternatively 'docker login ghcr.io' first, or re-run with --build to compile locally (15–30 min on a Pi)."
    fi
    warn "no published images found for this branch/arch — building locally"
    "${DOCKER[@]}" compose build
  else
    ok "images pulled"
  fi
fi

# --- 8. Web Push keys (VAPID) ---------------------------------------------------
# The api image generates the P-256 key pair (`dotnet CouplesCash.Api.dll --vapid`); no
# database is needed. Without keys the app still works — push is just hidden (503).
step "Web Push keys (VAPID)"
if [[ -n "$(get_env VAPID_PUBLIC_KEY)" && -n "$(get_env VAPID_PRIVATE_KEY)" ]]; then
  ok "VAPID keys already present"
elif [[ "$DOCKER_OK" != 1 ]]; then
  warn "docker daemon not reachable — skipped. Run 'make vapid' once Docker is up to enable push reminders."
else
  api_image="$("${DOCKER[@]}" compose config --images api 2>/dev/null | head -n1 || true)"
  have_api_image=0
  if [[ -n "$api_image" ]] && "${DOCKER[@]}" image inspect "$api_image" >/dev/null 2>&1; then
    have_api_image=1
  elif [[ "$BUILD_LOCAL" == 1 && "$NO_START" == 1 ]]; then
    warn "api image not built yet (--no-start --build) — skipped. Run 'make build && make vapid' later."
  elif "${DOCKER[@]}" compose pull api >/dev/null 2>&1; then
    have_api_image=1; ok "pulled ${api_image:-api image}"
  else
    warn "could not pull the api image — skipped. Run 'make vapid' after 'make pull' (or 'make build') to enable push reminders."
  fi
  if (( have_api_image )); then
    vapid_out="$("${DOCKER[@]}" compose run --rm --no-deps -T api dotnet CouplesCash.Api.dll --vapid 2>/dev/null || true)"
    vapid_pub="$(printf '%s\n' "$vapid_out" | sed -n 's/^Push__VapidPublicKey=//p' | tail -n1)"
    vapid_priv="$(printf '%s\n' "$vapid_out" | sed -n 's/^Push__VapidPrivateKey=//p' | tail -n1)"
    if [[ -n "$vapid_pub" && -n "$vapid_priv" ]]; then
      set_env VAPID_PUBLIC_KEY "$vapid_pub"
      set_env VAPID_PRIVATE_KEY "$vapid_priv"
      ok "generated VAPID key pair"
    else
      warn "the api image did not print VAPID keys (older image?) — run 'make vapid' after the next update. Push stays disabled until then."
    fi
  fi
fi
chmod 600 "$ENV_FILE"

# --- 9. start -----------------------------------------------------------------
if [[ "$NO_START" != 1 ]]; then
  step "Starting CouplesCash"
  "${DOCKER[@]}" compose up -d --remove-orphans
  printf '    waiting for services to become healthy'
  for _ in $(seq 1 60); do
    unhealthy="$("${DOCKER[@]}" compose ps --format '{{.Service}} {{.Health}}' 2>/dev/null | awk '$1!="migrate" && $2!="healthy" {print $1}' | tr '\n' ' ')"
    if [[ -z "${unhealthy// /}" ]]; then break; fi
    printf '.'; sleep 3
  done
  printf '\n'
  "${DOCKER[@]}" compose ps
  "${DOCKER[@]}" image prune -f >/dev/null 2>&1 || true
fi

# --- done ---------------------------------------------------------------------
printf '\n%s✓ CouplesCash is installed in %s%s\n\n' "$GREEN" "$INSTALL_DIR" "$RESET"
if [[ "$NO_START" != 1 ]]; then
  printf '  Open %s%s%s on both phones, register, link with a Couple Code, then\n' "$BOLD" "$PUBLIC_URL" "$RESET"
  printf '  Settings → "Install app" to add it to the home screen.\n'
  if [[ "$LAN_MODE" != 1 ]]; then
    printf '  Then Settings → "Notifications" for bill reminders (iOS: only from the installed app)\n'
    printf '  and Settings → "Passkeys" to sign in with Face ID / fingerprint.\n'
  fi
  printf '\n'
fi
(( RELOGIN )) && printf '  Log out and back in (or run: newgrp docker) to use docker without sudo.\n\n'
printf '  Update:   curl -fsSL https://raw.githubusercontent.com/tahuffman1s/CouplesCash/%s/install.sh | bash\n' "$BRANCH"
printf '  Status:   cd %s && make ps        Logs: make logs\n' "$INSTALL_DIR"
printf '  Backup:   make backup                 Stop: make down\n'
printf '  Docs:     docs/RASPBERRY-PI.md, docs/NOTIFICATIONS.md, docs/PASSKEYS.md, infra/cloudflared/README.md\n\n'
