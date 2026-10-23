#!/usr/bin/env bash
# CouplesCash — bootstrap a fresh 64-bit Raspberry Pi OS for the stack.
# Idempotent: safe to re-run. Run as your normal user (it uses sudo as needed).
#
#   curl -fsSL https://raw.githubusercontent.com/tahuffman1s/CouplesCash/main/scripts/pi-install.sh | bash
#   # or, after cloning:   scripts/pi-install.sh
#
# Env overrides: REPO_URL, INSTALL_DIR (default ~/couplescash), BRANCH (main)
set -euo pipefail

REPO_URL="${REPO_URL:-https://github.com/tahuffman1s/CouplesCash.git}"
INSTALL_DIR="${INSTALL_DIR:-$HOME/couplescash}"
BRANCH="${BRANCH:-main}"

step() { printf '\n\033[1;34m==> %s\033[0m\n' "$*"; }
ok()   { printf '    \033[32m✓\033[0m %s\n' "$*"; }
warn() { printf '    \033[33m!\033[0m %s\n' "$*"; }
die()  { printf '\033[31merror:\033[0m %s\n' "$*" >&2; exit 1; }

[[ "$(id -u)" -ne 0 ]] || die "run this as a normal user, not root (it will sudo when needed)"
command -v sudo >/dev/null || die "sudo is required"

# --- 1. Platform checks -----------------------------------------------------
step "Checking platform"
arch="$(uname -m)"
if [[ "$arch" != "aarch64" ]]; then
  die "architecture is '$arch' but the images are arm64/amd64 only. Install the 64-bit Raspberry Pi OS (uname -m must print aarch64)."
fi
ok "arm64 kernel"
if [[ -r /etc/os-release ]]; then
  # shellcheck disable=SC1091
  . /etc/os-release
  ok "${PRETTY_NAME:-unknown OS}"
fi
mem_kb="$(awk '/MemTotal/ {print $2}' /proc/meminfo)"
if (( mem_kb < 1800000 )); then
  warn "only $((mem_kb / 1024)) MB RAM — the stack needs ~700 MB; 2 GB is the minimum, 4 GB recommended"
else
  ok "$((mem_kb / 1024)) MB RAM"
fi

# --- 2. Base packages ---------------------------------------------------------
step "Installing base packages (git, make, curl, openssl)"
need=()
for p in git make curl openssl ca-certificates; do dpkg -s "$p" >/dev/null 2>&1 || need+=("$p"); done
if (( ${#need[@]} )); then
  sudo apt-get update -qq
  sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq "${need[@]}"
  ok "installed: ${need[*]}"
else
  ok "already installed"
fi

# --- 3. Docker ----------------------------------------------------------------
step "Docker"
if ! command -v docker >/dev/null 2>&1; then
  curl -fsSL https://get.docker.com -o /tmp/get-docker.sh
  sudo sh /tmp/get-docker.sh
  rm -f /tmp/get-docker.sh
  ok "Docker installed"
else
  ok "Docker present: $(docker --version)"
fi
docker compose version >/dev/null 2>&1 || sudo apt-get install -y -qq docker-compose-plugin
ok "compose plugin: $(docker compose version --short 2>/dev/null || echo present)"

if ! id -nG "$USER" | tr ' ' '\n' | grep -qx docker; then
  sudo usermod -aG docker "$USER"
  warn "added $USER to the docker group — log out and back in (or run 'newgrp docker') before using docker without sudo"
  RELOGIN=1
else
  ok "$USER is in the docker group"
fi
sudo systemctl enable --now docker >/dev/null 2>&1 && ok "docker service enabled"

# --- 4. Daemon config: log limits, live-restore -----------------------------
step "Docker daemon config"
daemon_json=/etc/docker/daemon.json
if [[ ! -s "$daemon_json" ]]; then
  sudo install -d -m 755 /etc/docker
  sudo tee "$daemon_json" >/dev/null <<'JSON'
{
  "log-driver": "json-file",
  "log-opts": { "max-size": "10m", "max-file": "3" },
  "live-restore": true
}
JSON
  sudo systemctl restart docker
  ok "wrote $daemon_json (log rotation 3×10 MB, live-restore)"
else
  ok "$daemon_json already exists — left untouched"
fi

# --- 5. Memory: swap / zram hint -------------------------------------------
step "Memory"
if swapon --show --noheadings | grep -q .; then
  ok "swap active: $(swapon --show --noheadings --bytes | awk '{s+=$3} END {printf "%d MB", s/1024/1024}')"
else
  warn "no swap. The compose memory limits fit in 2 GB, but a little zram helps on bursts:"
  warn "  sudo apt-get install -y zram-tools && printf 'ALGO=zstd\nPERCENT=25\n' | sudo tee /etc/default/zramswap && sudo systemctl restart zramswap"
fi

# --- 6. Time sync (JWT exp/iat depend on it) --------------------------------
step "Time sync"
if timedatectl show -p NTPSynchronized --value 2>/dev/null | grep -qi yes; then
  ok "NTP synchronised"
else
  sudo timedatectl set-ntp true || true
  warn "NTP not yet synchronised — tokens are time-based; check 'timedatectl' before first login"
fi

# --- 7. Repository ------------------------------------------------------------
step "Repository → $INSTALL_DIR"
if [[ -d "$INSTALL_DIR/.git" ]]; then
  git -C "$INSTALL_DIR" fetch --quiet origin
  git -C "$INSTALL_DIR" checkout --quiet "$BRANCH"
  git -C "$INSTALL_DIR" pull --ff-only --quiet origin "$BRANCH"
  ok "updated to $(git -C "$INSTALL_DIR" rev-parse --short HEAD)"
else
  git clone --quiet --branch "$BRANCH" "$REPO_URL" "$INSTALL_DIR"
  ok "cloned $REPO_URL"
fi

# --- 8. .env ------------------------------------------------------------------
step "Configuration"
if [[ ! -f "$INSTALL_DIR/.env" ]]; then
  cp "$INSTALL_DIR/.env.example" "$INSTALL_DIR/.env"
  chmod 600 "$INSTALL_DIR/.env"
  ( cd "$INSTALL_DIR" && make --no-print-directory secrets )
  ok "created .env with fresh secrets"
else
  ok ".env exists — not modified"
fi
mkdir -p "$INSTALL_DIR/backups" && chmod 700 "$INSTALL_DIR/backups"

# --- Done ---------------------------------------------------------------------
cat <<NEXT

$(printf '\033[1mNext steps\033[0m')
  1. $( [[ "${RELOGIN:-}" == 1 ]] && echo "Log out and back in (or: newgrp docker) so docker works without sudo." || echo "Docker is ready." )
  2. Edit $INSTALL_DIR/.env:
       PUBLIC_URL   → https://cash.yourdomain.com   (must match the tunnel hostname exactly)
       TUNNEL_TOKEN → from Cloudflare Zero Trust (see infra/cloudflared/README.md)
  3. cd $INSTALL_DIR && make pi          # pulls arm64 images, starts db/api/web + tunnel
  4. make ps && make logs               # wait until every service is "healthy"
  5. Open PUBLIC_URL on both phones → Register → Settings → "Install app".

  Backups:  make backup   (./backups/, keeps 14)      Update later:  make pi
  Docs:     docs/RASPBERRY-PI.md
NEXT
