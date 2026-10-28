# Running CouplesCash on a Raspberry Pi 4

The whole stack — Postgres, the .NET API, the SvelteKit web app, the Python
matcher and the Cloudflare tunnel — fits comfortably on a Pi 4 and idles at
roughly 650–750 MB of RAM and a few percent CPU. This guide takes a Pi from a blank SD card to a
public `https://` URL.

## 1. Hardware & OS

| | Minimum | Recommended |
|---|---|---|
| Board | Raspberry Pi 4, 2 GB | Pi 4 / Pi 5, 4 GB |
| Storage | 16 GB A2-class microSD | USB 3 SSD (boot from USB) |
| OS | **Raspberry Pi OS Lite (64-bit)**, Bookworm or newer | same |
| Network | Ethernet or Wi-Fi with internet access (outbound only) | Ethernet |
| Power | Official 5 V/3 A PSU | same |

**64-bit is mandatory.** The container images are built for `linux/arm64` and
`linux/amd64` only; the 32-bit (`armv7l`) OS cannot run them. Check with:

```bash
uname -m        # must print: aarch64
```

If it prints `armv7l`, reflash with Raspberry Pi Imager → *Raspberry Pi OS
(other)* → *Raspberry Pi OS Lite (64-bit)*. In Imager's settings enable SSH,
set a user name/password and your Wi-Fi.

### SD card or SSD?

Postgres writes continuously (WAL, checkpoints, autovacuum). A microSD card
works — the config in `infra/postgres/postgresql.conf` turns on WAL compression
and spaces out checkpoints to be gentle on it — but cards wear out and are
slow to restore from. For a "set and forget" install, boot from a USB 3 SSD:
it is 5–10× faster for the database and far more durable. If you do stay on a
card, buy an A2-rated one from a reputable brand and take backups
(`make backup`) regularly.

## 2. Bootstrap the Pi

SSH in and run the installer. It is idempotent — re-running it is safe and is
also how you update.

```bash
curl -fsSL https://raw.githubusercontent.com/tahuffman1s/CouplesCash/main/install.sh | bash -s -- --pi
```

`--pi` is auto-detected on real Pi hardware, so the plain one-liner works too; pass it
explicitly to force Pi mode (for example in a VM). What `install.sh` does (it works on any
64-bit Linux, not just the Pi):

1. Verifies a 64-bit CPU, RAM, and detects the package manager (apt, dnf, pacman, apk, zypper).
2. Installs `curl git openssl ca-certificates` if missing.
3. Installs Docker Engine + Compose plugin (via `get.docker.com` on Debian/Ubuntu/Fedora/RHEL
   families, distro packages elsewhere), adds your user to the `docker` group, enables the service.
4. Writes `/etc/docker/daemon.json` with log rotation (3 × 10 MB per
   container) and `live-restore` — only if the file does not already exist.
5. In Pi mode: requires arm64, makes sure NTP is on, enables **zram swap** (zstd, 25 % of RAM)
   if there is no swap, and refuses to fall back to a slow local image build unless `--build` is given.
6. Clones the repo to `~/couplescash` (or `git pull`s it if present).
7. Creates `.env`, generates `JWT_SECRET`, `POSTGRES_PASSWORD` and `DATA_KEY` (the key that
   encrypts bank-sync credentials — `docs/BANK-SYNC.md`), asks whether you want a
   **Cloudflare Tunnel** (enter `PUBLIC_URL` + `TUNNEL_TOKEN`) or **LAN-only** mode
   (`http://<pi-ip>:3000`, web bound to the whole LAN; `--port N` or `WEB_PORT` in `.env` changes the port), and sets the push contact
   `VAPID_SUBJECT=mailto:<--email or admin@your-host>`.
8. Pulls the images (falls back to building locally).
9. Generates the **Web Push (VAPID) key pair** with the api image
   (`docker compose run --rm --no-deps api dotnet CouplesCash.Api.dll --vapid`) so bill
   reminders work out of the box, then starts the stack and waits for health. With
   `--no-start` and no Docker daemon this step is skipped with a warning; `make vapid` fills
   the keys later.

Flags: `--pi`, `--lan`, `--public-url URL`, `--tunnel-token TOK`, `--email ADDR`, `--no-start`,
`--build`, `--branch NAME`, `--dir DIR`, `-y` (non-interactive). Each also has an
environment-variable form; `install.sh --help` lists them.

If it added you to the `docker` group, **log out and back in** (or `newgrp
docker`) before continuing.

## 3. Configure

```bash
cd ~/couplescash
nano .env
```

Set two things:

- `PUBLIC_URL=https://cash.yourdomain.com` — the exact address users will type.
  (For a LAN-only trial without a tunnel use `http://<pi-ip>:3000` and set
  `WEB_BIND=0.0.0.0`.)
- `TUNNEL_TOKEN=…` — from Cloudflare; follow `infra/cloudflared/README.md`.

Optional, for push reminders and passkeys (v2):

- `VAPID_PUBLIC_KEY` / `VAPID_PRIVATE_KEY` — Web Push signing pair. Empty = push disabled
  (the app hides the Notifications settings). Fill with `make vapid` (needs the api image:
  `make pull` first). `VAPID_SUBJECT=mailto:you@example.com` is the contact push services
  may use.
- Passkeys need no extra variable: the API derives the WebAuthn origin and rpId from
  `PUBLIC_URL`. **Keep the hostname stable** — changing it invalidates every passkey
  (`docs/PASSKEYS.md`).
- Both need `https://`, i.e. the tunnel. They are hidden in LAN mode.
- `SCHEDULER_ENABLED=false` pauses the in-api background job (autopay, reminders, bank sync).

Optional, for bank sync (v3, `docs/BANK-SYNC.md`):

- `DATA_KEY` — base64, 32 random bytes; AES-256-GCM key for the SimpleFIN access URLs stored
  in the database. `install.sh` and `make secrets` fill it (`openssl rand -base64 32`). Empty =
  bank sync hidden in the app (API `503 bank_sync_not_configured`); CSV import still works.
  **Losing or changing it means re-linking every bank** — keep `.env` in your backups
  (`make backup` copies it next to the dump).
- `SYNC_INTERVAL_HOURS=6` — how often linked banks are pulled. The Bridge caches, so lower
  values gain nothing. "Sync now" in the app is limited to once per 15 minutes per link.
- Bank sync does **not** need `https://`; it works in LAN mode. The Pi needs outbound HTTPS
  to `beta-bridge.simplefin.org`.

Automatic matching (v6, `docs/BANK-SYNC.md` → "Automatic matching"):

- `MATCHER_KEY` — 64 hex chars (`openssl rand -hex 32`) shared by `api` and the internal
  `matcher` container. `install.sh` and `make secrets` fill it (and `make up` fills it if it is
  still empty). Empty = the matcher refuses to start and the API matches with its built-in
  rules. Nothing stored depends on it, so changing it is harmless.

Leave `ASPNETCORE_ENVIRONMENT=Production` and `API_ENABLE_DOCS=false`.

## 4. First boot

```bash
make pi
```

`make pi` = `make pull` (download the prebuilt arm64 images from GHCR) +
`make tunnel-up` (start everything with the `tunnel` profile) + prune old
images. Startup order is enforced by compose: `db` becomes healthy → `migrate`
runs the DbUp migrations and exits 0 → `matcher` starts → `api` starts →
`web` starts → `cloudflared` connects. `api` only waits for `matcher` to be
*started*, not healthy: if the matcher is down the API still runs and matches
with its built-in rules.

First pull is ~300 MB. On a 2 GB Pi the first start takes about a minute.

### Verify

```bash
make ps
```

Every service (including `matcher`) should show `Up … (healthy)` (`migrate` shows `Exited (0)`,
`cloudflared` has no healthcheck). Then:

```bash
curl -s http://localhost:3000/login -o /dev/null -w '%{http_code}\n'   # 200
make logs S=api                                                       # "Now listening on: http://[::]:8080"
make logs S=matcher                                                   # "matcher 1.0.0 ready", then one line per batch
make logs S=cloudflared                                               # "Registered tunnel connection"
make scheduler-logs                                                   # background job: autopay / reminders ticks
```

The API has no published port on purpose. To poke it from the Pi:

```bash
docker compose exec web node -e "fetch('http://api:8080/health').then(r=>r.text()).then(console.log)"
```

Open `PUBLIC_URL` on each phone, register, link with the Couple Code, and use
*Settings → Install app* to add it to the home screen. Then, from the
**installed** app, *Settings → Notifications → Enable on this device* (iOS only
delivers push to Home Screen apps — `docs/NOTIFICATIONS.md`) and *Settings →
Passkeys → Add passkey* for Face ID / fingerprint sign-in (`docs/PASSKEYS.md`).
To stop typing balances in, *Settings → Linked banks* with a SimpleFIN Bridge setup token
(`docs/BANK-SYNC.md`), or *More → Import CSV* for a statement.

## 5. Day-two operations

| Task | Command |
|---|---|
| Update to the latest images | `make pi` (pull + recreate changed containers; data is kept) |
| Status / health | `make ps` |
| Logs (all / one service) | `make logs` / `make logs S=web` / `make logs S=matcher` (or `make matcher-logs`) |
| Stop everything | `make down` |
| Start again | `make tunnel-up` (or `make up` without the tunnel) |
| Backup | `make backup` → `backups/couplescash-YYYYmmdd-HHMMSS.sql.gz` **+** `couplescash-YYYYmmdd-HHMMSS.env` (the `.env` copy carries `DATA_KEY`; keeps 14 pairs) |
| Backup to Nextcloud | `make backup-nextcloud` (one dump now) · `make backup-nextcloud-cron` (every 4 h; `EVERY=6` to change, `EVERY=0` to stop) — see below |
| Restore | `make restore FILE=backups/couplescash-….sql.gz`; if the current `.env` differs from the backup's copy it tells you — copy that `.env` over and `make up` so linked banks decrypt |
| Rotate the JWT secret (signs everyone out) | edit `JWT_SECRET` in `.env`, then `make up`. `make secrets FORCE=1` rotates **all** secrets incl. the DB password and `DATA_KEY` (**breaks every bank link**) — see below and `docs/SECURITY.md` |
| Generate / rotate Web Push keys | `make vapid` (fills empty keys) · `make vapid FORCE=1` (rotate; every phone re-enables notifications), then `make up` |
| Enable bank sync on an older install | `make secrets` (fills an empty `DATA_KEY`, leaves set values alone) then `make up`; *Settings → Linked banks* appears |
| Watch the background job (autopay, reminders, bank sync) | `make scheduler-logs` |
| Enable automatic matching on an older install | `make pull && make up` — `make up` fills an empty `MATCHER_KEY` and starts the new `matcher` container |

### Automatic nightly backups

```bash
( crontab -l 2>/dev/null; echo '15 3 * * * cd $HOME/couplescash && make backup >> backups/backup.log 2>&1' ) | crontab -
```

Copy the `backups/` folder somewhere else occasionally (`rsync` to a laptop, a
NAS, or `rclone` to cloud storage). A backup on the same SD card as the
database is not a backup. The folder contains `.env` copies (secrets, incl.
`DATA_KEY`), so the destination should be encrypted or private storage.

### Database backups to Nextcloud (every 4 hours)

Uploads a compressed `pg_dump` of the database plus a copy of `.env` next to it
(`couplescash-db-<stamp>.sql.gz` + `couplescash-db-<stamp>.env`) to a folder in
your Nextcloud over WebDAV, and keeps the newest 42 pairs there (a week).

1. In Nextcloud: *Settings → Security → Devices & sessions → Create new app
   password* (name it "CouplesCash backups"). Use it instead of your login
   password; you can revoke it there any time.
2. On the Pi, fill in `.env`:
   ```bash
   NEXTCLOUD_URL=https://cloud.example.com     # your server, no /remote.php…
   NEXTCLOUD_USER=your-login
   NEXTCLOUD_APP_PASSWORD=xxxxx-xxxxx-xxxxx-xxxxx-xxxxx
   # NEXTCLOUD_DIR=CouplesCash/backups         # optional, created if missing
   # NEXTCLOUD_KEEP=42                         # optional
   ```
3. Try one: `make backup-nextcloud` → `uploaded couplescash-db-….sql.gz (…) to CouplesCash/backups`
   and `uploaded couplescash-db-….env`.
4. Schedule it: `make backup-nextcloud-cron` (adds `17 */4 * * *` to your
   crontab; re-run with `EVERY=6` to change it, `EVERY=0` to remove it). Each
   run appends to `backups/nextcloud.log`; a run that finds the previous one
   still uploading skips.

To restore, download a dump **and its `.env`** from Nextcloud into `backups/`
and run `make restore FILE=backups/couplescash-db-….sql.gz`. If that `.env`
differs from the Pi's, restore tells you — copy it over `.env` and `make up` so
linked banks decrypt (they need the `DATA_KEY` they were linked with). On a new
Pi, put the `.env` copy in place before the first `make up`.

The folder now holds everything needed to read your data *and* decrypt your
bank links (`DATA_KEY`), plus your secrets and the Nextcloud app password
itself: keep it private, don't share it, and use Nextcloud's encryption if
you have it. If the folder leaks, rotate the secrets (`docs/SECURITY.md`),
revoke the app password and re-link the banks.

### Changing the database password

`POSTGRES_PASSWORD` is only read by Postgres on **first** initialisation.
To rotate it later:

```bash
docker compose exec db psql -U couplescash -c "ALTER ROLE couplescash PASSWORD 'new-password';"
sed -i 's|^POSTGRES_PASSWORD=.*|POSTGRES_PASSWORD=new-password|' .env
make up
```

### Moving Docker data to an SSD later

Postgres data lives in the named volume `couplescash_pgdata` under Docker's
data root (`/var/lib/docker`). To move *all* of Docker to an SSD mounted at
`/mnt/ssd`:

```bash
make down
sudo systemctl stop docker
sudo rsync -aHAX /var/lib/docker/ /mnt/ssd/docker/
echo '{ "data-root": "/mnt/ssd/docker", "log-driver": "json-file", "log-opts": { "max-size": "10m", "max-file": "3" }, "live-restore": true }' | sudo tee /etc/docker/daemon.json
sudo systemctl start docker
make tunnel-up
```

### Memory budget

| Service | `mem_limit` | Typical use |
|---|---|---|
| db | 256 MB | 60–120 MB |
| api | 192 MB (GC heap hard limit 128 MB) | 90–140 MB |
| web | 160 MB | 60–90 MB |
| matcher | 96 MB | 50–60 MB idle, ≈ 80 MB during a 5000-transaction batch |
| cloudflared | 64 MB | 20–30 MB |
| **total** | **768 MB** | ~350 MB |

A 2 GB Pi has plenty of headroom; the limits exist so a bug cannot take the
whole Pi down.

## 6. Building on the Pi instead of pulling

Not recommended, but possible: `make build` compiles the .NET solution and the
SvelteKit app *on the Pi*. Expect 10–25 minutes on a Pi 4 and a few GB of
disk; a 2 GB model may need swap/zram to finish the `pnpm install`. Pulling
CI-built images (`make pull`) takes a minute. If you fork the repo, point
`IMAGE_PREFIX` in `.env` at your own GHCR namespace and let CI build for you.

## 7. Troubleshooting

**`no matching manifest for linux/arm64/v8`** — the image was built without
arm64 support. Either you are pulling an image tag that was not published by
CI (check `IMAGE_PREFIX`/`IMAGE_TAG` in `.env`), or CI has not yet run on
`main`. Workaround: `make build`.

**`exec format error`** — you are on 32-bit Raspberry Pi OS (`uname -m` says
`armv7l`). Reflash with the 64-bit image.

**`api` keeps restarting / `Exited (137)`** — 137 = killed by the OOM killer.
Check `docker stats`. The `DOTNET_GCHeapHardLimit` and `mem_limit` in
`compose.yml` are tuned for the stock app; if you raised `max_connections` or
added heavy features, raise `api`'s `mem_limit` to `256m`. Add zram
(`install.sh --pi` sets this up automatically).

**`Your kernel does not support memory limit capabilities or the cgroup is not mounted`**
— Raspberry Pi OS disables the memory cgroup by default, so the `mem_limit`
values in `compose.yml` are ignored (the app still runs). `install.sh --pi`
adds `cgroup_enable=memory cgroup_memory=1` to `/boot/firmware/cmdline.txt`;
reboot once and the warning disappears. To do it by hand:
`sudo sed -i '1 s/$/ cgroup_enable=memory cgroup_memory=1/' /boot/firmware/cmdline.txt && sudo reboot`.

**`docker compose pull` says `denied` / `unauthorized`** — the GHCR packages that
CI publishes are **private by default**. One-time fix on GitHub: your profile →
*Packages* → `couplescash-api`, `couplescash-web` and `couplescash-matcher` → *Package settings* →
*Change visibility* → Public. (Or `docker login ghcr.io` on the Pi with a
read-only personal access token.) Images are published from the repository's
default branch and from `v*` tags.

**`migrate` exits non-zero** — `make logs S=migrate`. The usual cause is an
unreachable database (wrong `POSTGRES_PASSWORD` after a manual change) or a
broken migration script. The `api` service will not start until `migrate`
succeeds, by design.

**Login works, then "session expired" immediately / tokens rejected** — the
Pi's clock is wrong. JWTs carry `exp`/`iat` and the API rejects tokens from
the future. `timedatectl` should say `System clock synchronized: yes`; if not:
`sudo timedatectl set-ntp true`. The Pi has no battery-backed RTC, so a Pi
that was powered off for a while needs network time before the first login.

**Form submit → 403** — `PUBLIC_URL` in `.env` does not match the address in
the browser exactly. See `infra/cloudflared/README.md` §3.

**No "Notifications" or "Passkeys" in Settings** — either you are in LAN/http
mode (both need `https://`, i.e. the tunnel), or `VAPID_*` are empty in `.env`
(push only): `make vapid && make up`. Details and the iOS Home Screen
requirement: `docs/NOTIFICATIONS.md`, `docs/PASSKEYS.md`.

**Reminders arrive at the wrong time or not at all** — the scheduler runs inside
`api` and evaluates the hour in the *household* time zone (Settings → Household),
so (1) check that time zone, (2) check the Pi's clock is NTP-synchronised
(`timedatectl`; a Pi has no RTC), (3) `make scheduler-logs`. Reminders are not
replayed for hours the stack was down.

**No "Linked banks" in Settings / "Bank sync is not configured"** — `DATA_KEY`
is empty in `.env` (installs from before v3): `make secrets && make up`. Only
empty values are filled; nothing else changes. Full guide: `docs/BANK-SYNC.md`.

**Linked banks shows "cannot decrypt" / every link errors after a restore or
reinstall** — the database was written with a different `DATA_KEY` than the one
in `.env`. Copy the `couplescash-<stamp>.env` that matches the dump over `.env`
and `make up`. If that key is gone for good, unlink and re-link each bank.

**A bank shows "connection needs attention"** — the SimpleFIN Bridge lost its
link to that bank (password change, re-authorisation). Fix it on the Bridge's
site; nothing to do on the Pi.

**Passkeys stopped working after changing the domain** — passkeys are bound to
the hostname in `PUBLIC_URL`. Sign in with the password and register new ones;
the old ones can be deleted in Settings → Passkeys.

**`web` is unhealthy but `api` is healthy** — `docker compose logs web`. A
common cause is `ORIGIN` missing (compose refuses to start without
`PUBLIC_URL`, but an empty string passes) — set it and `make up`.

**The api healthcheck never turns healthy although logs look fine** — the
check greps `/proc/net/tcp*` for a listener on port 8080 (`0x1F90`). It needs
`sh` and `grep` inside the image, which the Debian/Alpine `aspnet` images have
but *chiseled* images do not. If the Dockerfile switches to a chiseled base,
replace the healthcheck with a tiny health probe binary or drop it.

**`matcher` keeps restarting** — `make logs S=matcher`. "refusing to start: MATCHER_KEY is
empty" → `make secrets && make up` (fills only empty values). `Exited (137)` = OOM: a single
very large CSV import can approach the 96 MB limit; raise `mem_limit` for `matcher` to `128m`.
While it is down, matching silently uses the API's built-in rules (rows say `builtin`).

**Slow first page load after a restart** — .NET tiered compilation warming
up; it settles after a few requests. Not a Pi problem.

**Everything is slow and `iowait` is high** — SD card wear or a cheap card.
`make backup`, then migrate to an SSD (§1, §5).
