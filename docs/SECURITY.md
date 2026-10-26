# Security

CouplesCash holds a couple's bank balances and bills. It is a small,
self-hosted app — but "small" is not an excuse for sloppy. This document
explains, in plain language, what we protect against, how, and what to do
when something goes wrong.

## Threat model

**Who might attack, and what would they want?**

1. **Internet background noise** — bots scanning for login pages, default
   credentials, exposed databases. They want *any* foothold.
2. **Someone who finds or steals a phone** with the app installed and
   unlocked. They want to read or change your finances.
3. **Someone who guesses or leaks a password** (reuse from a breached site).
4. **A malicious web page** open in the same browser trying to make requests
   to the app with your cookies (CSRF) or inject script (XSS).
5. **A person on your Wi-Fi/LAN** (guest, compromised IoT device) looking for
   open ports on the Pi.
6. **A bug in our own code** — a query that forgets to filter by household and
   leaks another couple's data.
7. **The push and passkey plumbing** (v2) — a push service or a network observer
   reading reminders; someone replaying a captured passkey response; a leaked
   VAPID private key letting a stranger send notifications "from" your server.
8. **The bank-sync credential** (v3) — a copy of the database (backup, stolen SD
   card) or of `.env` that lets someone read your bank transactions through the
   SimpleFIN Bridge; a sync error silently hiding a dead connection; an inbox
   action that creates or marks something in the wrong household.
9. **The fuzzy matcher** (v6) — a second service that sees transaction
   descriptions: someone on the compose network calling it, it leaking
   descriptions into logs, or an automatic match nobody can see or undo.

**Out of scope**: a fully compromised Pi (root access), a compromised
Cloudflare account, or malware on the phone itself. Keep the OS updated, use
strong passwords on the Pi and Cloudflare, and enable 2FA on Cloudflare.

## Controls

| Threat | Control | Where |
|---|---|---|
| Exposed services on the internet | Only `web` is reachable, and only through the Cloudflare tunnel (outbound connection, no port forwarding). `api`, `matcher` and `db` have **no published ports**. | `compose.yml` (`ports:` only on `web`, bound to `127.0.0.1`), `infra/cloudflared/README.md` |
| Open ports on the LAN | `web` binds `127.0.0.1:3000` on the Pi unless you opt in with `WEB_BIND=0.0.0.0`. | `.env.example`, `compose.yml` |
| Password guessing / credential stuffing | Argon2id hashing (64 MB, 3 iterations); fixed-window rate limit **10 req/min/IP** on `/v1/auth/*` and `/v1/couple/join`, 120 req/min globally; identical error for wrong email vs wrong password. | API `Auth/`, rate-limiter middleware in `Program.cs`; contract: `docs/API-CONTRACT.md` |
| Weak passwords | Minimum 10 characters enforced server-side (FluentValidation). | API validators |
| Stolen access token | Access JWTs live **15 minutes**, HS256 with a ≥ 256-bit `JWT_SECRET` from the environment, issuer/audience `couplescash`. | API `TokenService`, `Jwt__Secret` env |
| Stolen refresh token | Opaque 256-bit refresh tokens, **SHA-256 hashed at rest**, **rotated on every use**, 30-day expiry, grouped into a *family*; **reuse of an old token revokes the whole family** (detects theft). | API `refresh_tokens` table + `/v1/auth/refresh` |
| Tokens in the browser | The browser never sees tokens. SvelteKit (BFF) stores them in `httpOnly; Secure; SameSite=Lax` cookies and talks to the API server-side. | `apps/web/src/lib/server/api.ts`, `hooks.server.ts` |
| CSRF | SvelteKit form actions check `Origin` against `ORIGIN` (= `PUBLIC_URL`); client-side fetches must go through `/api/[...path]` with header `X-Requested-With: CouplesCash`; cookies are `SameSite=Lax`. | web `hooks.server.ts`, `routes/api/[...path]`, `compose.yml` `ORIGIN` |
| XSS / clickjacking / sniffing | CSP, HSTS, `X-Content-Type-Options: nosniff`, `Referrer-Policy`, `Permissions-Policy` (`camera=(self)` only for the QR scanner), `X-Frame-Options: DENY`. Svelte escapes output by default. | web `hooks.server.ts` |
| Reading another couple's data | Every query is `WHERE household_id = @HouseholdId` with the id taken from the **JWT `hh` claim**, never from the request. Unknown *or foreign* ids return **404** (never 403) to avoid enumeration. | API repositories, endpoint filters; `CLAUDE.md` conventions |
| Couple-code brute force | 20 random bytes; the typed code is 8 Crockford-base32 chars (40 bits) hashed at rest, **10-minute TTL, single use**, and `/couple/join` is rate-limited to 10/min/IP → ~6 000 guesses/10 min vs 2⁴⁰ ≈ 10¹² possibilities. | API `CoupleCodes` |
| Oversized / malformed requests | `MaxRequestBodySize` 64 KB on the API; `BODY_SIZE_LIMIT=512K` on web; FluentValidation on every body; RFC 9457 problem details without stack traces in production. | API `Program.cs`, `compose.yml` |
| Container escape / privilege escalation | Non-root users in all images, `read_only: true` root filesystems with explicit `tmpfs`, `no-new-privileges`, memory limits, `init: true`. | `compose.yml` `x-common`, Dockerfiles |
| Secrets in the repo / images | Secrets only via environment (`.env`, git-ignored, `chmod 600`); `make secrets` generates them; images contain no config. | `.gitignore`, `Makefile`, `.env.example` |
| Leaking API docs | Scalar/OpenAPI only served when `Api__EnableDocs=true`; default `false` in production. | `.env.example` → `API_ENABLE_DOCS` |
| Log leakage | Logs capped at 3 × 10 MB per container; no tokens or passwords are logged. | `compose.yml` logging, `/etc/docker/daemon.json` |
| Supply chain | Pinned base images and lockfiles (`pnpm-lock.yaml`, NuGet, `uv.lock`), Dependabot for npm/NuGet/uv/Docker/Actions weekly, CI builds images from source and publishes to GHCR under your account. | `.github/dependabot.yml`, `.github/workflows/ci.yml` |
| Optional second factor | Cloudflare Access with email one-time codes for exactly your two addresses in front of the hostname. | `infra/cloudflared/README.md` §5 |
| Push payloads read in transit or by the push service | Payloads are encrypted end-to-end to the device (RFC 8291) and signed with VAPID (RFC 8292); they contain **only the item name, the amount, a tag and a relative URL** — never tokens, balances or account numbers. Push services (Apple/Google/Mozilla) see ciphertext and an endpoint. | API `Push/`, `docs/NOTIFICATIONS.md` |
| Leaked VAPID private key (strangers could push to subscribed phones) | Key pair generated locally by the api image (`make vapid`), stored only in `.env` (`chmod 600`) and the api container environment; never logged or returned by the API (only the **public** key is served). **Rotate**: `make vapid FORCE=1 && make up` — every device must re-enable notifications. Subscriptions are per user; the server only sends to the subscriber's own household events. | `.env.example`, `Makefile`, `compose.yml` `x-api-env` |
| Dead or hijacked push endpoints | Subscriptions are removed after 5 consecutive failures, and immediately on 404/410 from the push service; disabling notifications on a device deletes its subscription. | API scheduler hygiene |
| Passkey phishing / replay | WebAuthn credentials are bound to the rpId = host of `PUBLIC_URL`, so another origin cannot use them; the browser also checks the origin. Challenges are random, **expire after 5 minutes and are single-use**; the sign counter is checked against clones. `userVerification: required` (biometric/PIN). Login routes share the `auth` rate limit (10/min/IP). Passkeys are refused (503) unless the origin is https or localhost. | API `Auth/` (Fido2NetLib), `webauthn_challenges`, `docs/PASSKEYS.md` |
| Background job touching the wrong household | The scheduler iterates households and runs every statement with an explicit `household_id`, exactly like a request; it has no "all households" mutation. Autopay writes `paid_by = NULL`, `auto = true`, so system actions are distinguishable from human ones and undo-able. | API `Scheduler/` |
| "Who changed this?" / tampering by a partner | Every mutation writes an `activity_events` row in the same transaction (actor, kind, entity, summary, data) — an append-only audit trail both partners see on `/activity`. System actions have `actor_id = NULL`. | API `Data/`, migration `0002_v2.sql` |
| Bank access URL read from a stolen database / backup | The SimpleFIN access URL (a read-only credential) is stored only as **AES-256-GCM ciphertext** (`access_url_enc`: nonce + tag + ciphertext) under `DATA_KEY`, a 32-byte key that lives **only in `.env`** (`chmod 600`) and the api container environment — never in the database, images or logs. The plaintext is never logged (logs carry link names and counts) and no route returns it. Without the key the claim endpoint is disabled (`503 bank_sync_not_configured`). The setup token is single-use and claimed server-side, so the browser never holds the credential either. | API `Security/DataProtector.cs`, `SimpleFin/`, `.env.example` `DATA_KEY`, `compose.yml` `x-api-env` |
| Lost or rotated `DATA_KEY` | Fail closed: links that cannot be decrypted are reported as errors on the link card and in the activity feed; nothing falls back to plaintext. `make secrets FORCE=1` prints a loud warning before rotating; `make backup` copies `.env` next to the dump so a restore can decrypt. Recovery is always *re-link*, never *recover the key*. | `Makefile` `secrets`, `scripts/backup.sh`, `docs/BANK-SYNC.md` |
| Bank connection quietly broken | The Bridge's per-connection `errors` and any sync failure are surfaced, not swallowed: `last_error` on the link, a `bank.error` activity row, and the error text on *Settings → Linked banks*. The job continues with the next link. | API `Scheduler/` bank-sync phase, web `/settings/banks` |
| Fuzzy matcher (`matcher`, v6) misused or leaking | **Internal-only**: no `ports:`, reachable only as `http://matcher:8090` on the compose network, never behind the tunnel. Every route except `/health` needs `X-Matcher-Key` = `MATCHER_KEY` (64 hex chars from `openssl rand -hex 32`, shared with `api` as `Matcher__Key`, compared in constant time); an empty key makes the container **refuse to start**. **Stateless**: no database, no disk writes (read-only FS), nothing kept between requests; it never sees household ids, tokens or balances — only the transactions and items the API sends for one batch. Logs carry **counts and timings only, never descriptions, payees or names**; validation errors never echo input. 1 MB body / 5000 transactions / 2000 candidates limits (413), 96 MB memory limit, non-root. If it is down the API falls back to its built-in matcher. Automatic matches are `paid_by NULL, auto = true`, carry their reason, write an activity row and are undo-able. Rotate: clear `MATCHER_KEY=` in `.env`, `make secrets` (fills only empty values — never use `FORCE=1` for this, it also rotates `DATA_KEY`) and `make up`; nothing stored depends on it. | `apps/matcher/`, `compose.yml` `matcher` + `x-api-env`, `docs/BANK-SYNC.md` "Automatic matching" |
| Inbox action touching the wrong household / untraceable | Inbox rows, links, external accounts, payee rules and CSV imports carry `household_id` and every statement filters on the JWT `hh` claim (foreign ids → 404). Confirm / ignore / restore / bulk / auto-match each write an `activity_events` row (`transaction.*`, `bank.*`, `import.csv`); auto-matched payments are `paid_by NULL, auto = true` and undo-able like autopay. | API `Endpoints/InboxEndpoints.cs`, `Data/`, migration `0003_imports.sql` |

## If a phone is lost or stolen

1. From any other device, log in and go to **Settings → Change password**.
   Changing the password (`POST /v1/auth/password`) **revokes every refresh
   token** for your account. The lost phone's 15-minute access token expires
   on its own; after that it cannot obtain a new one.
2. **Settings → Passkeys**: delete the lost phone's passkey — a password change
   does not disable passkeys, and a passkey would let the holder sign in again
   if they can unlock the phone. **Settings → Notifications**: remove the lost
   device so reminders stop going to it.
3. If you suspect the *partner's* account is also at risk, have them do the
   same.
4. If you use Cloudflare Access, also revoke the user's session in **Zero
   Trust → Access → … → Revoke session**.
5. Use the phone vendor's remote wipe (Find My / Find My Device).

There is nothing to do on the Pi; no data is stored on the phone except the
cached app shell (no financial data is precached by the service worker).

## If the Pi (or a backup, or `.env`) is lost or stolen

The database holds password hashes, hashed refresh tokens and the **encrypted**
bank access URLs; `.env` holds the keys. Assume whoever has both can read your
bank transactions through the Bridge until you act:

1. **SimpleFIN Bridge → your app's access → Delete access.** This revokes the
   token server-side at the Bridge; the stolen credential is now useless even
   if decrypted. Do this first — it needs nothing from the Pi.
2. On the replacement Pi, **rotate `DATA_KEY` and `JWT_SECRET`** (`make secrets
   FORCE=1` does both, plus the DB password — fine on a fresh database), restore
   the dump, then re-link each bank with a *new* setup token
   (`docs/BANK-SYNC.md`).
3. Both partners change their passwords (revokes refresh tokens) and delete the
   passkeys registered from the old installation.
4. If the Cloudflare tunnel token was in the stolen `.env`, delete that tunnel in
   Zero Trust and create a new one.

## Rotating `JWT_SECRET`

Rotate if the secret may have leaked (e.g. a copy of `.env` was shared) or as
routine hygiene. Rotation invalidates all access tokens instantly; refresh
tokens continue to work, so users are transparently re-issued new access
tokens on their next request — in practice nobody notices.

```bash
cd ~/couplescash
sed -i "s|^JWT_SECRET=.*|JWT_SECRET=$(openssl rand -base64 48 | tr -d '\n')|" .env
make up          # recreates api (and migrate) with the new secret
```

To force everyone to log in again *as well*, change your passwords (revokes
refresh tokens) or, drastically: `docker compose exec db psql -U couplescash
-c "DELETE FROM refresh_tokens;"`.

`make secrets FORCE=1` regenerates **all three** secrets (`JWT_SECRET`,
`POSTGRES_PASSWORD`, `DATA_KEY`); only do that on a fresh database, because
`POSTGRES_PASSWORD` is baked into the Postgres data directory on first start
(see `docs/RASPBERRY-PI.md` → *Changing the database password*) and a new
`DATA_KEY` makes every linked bank unreadable (below).

## Rotating `DATA_KEY`

`DATA_KEY` encrypts the SimpleFIN access URLs. There is no re-encryption path
on purpose: the ciphertext is tied to the key, so **rotating the key means
re-linking every bank** with a fresh setup token. Rotate if `.env` or a backup
leaked, and then also revoke the old tokens at the Bridge (*Delete access*),
because the key only protects the copy on your disk — the Bridge would still
honour a leaked plaintext URL.

```bash
cd ~/couplescash
make backup                                            # dump + current .env, in case
sed -i "s|^DATA_KEY=.*|DATA_KEY=$(openssl rand -base64 32 | tr -d '\n')|" .env
make up                                                # api now reports every link as undecryptable
# Settings → Linked banks: unlink each bank, then link it again with a new setup token
```

`make secrets FORCE=1` does the same (and warns), but also rotates the other
two secrets. Never paste `DATA_KEY` anywhere; it is never logged or served.

## Rotating the VAPID keys

Rotate if `.env` leaked, or if notifications arrive that nobody sent. The new
key pair invalidates every push subscription: each phone has to turn
notifications off and on again (*Settings → Notifications*).

```bash
cd ~/couplescash
make vapid FORCE=1   # new P-256 pair via the api image (or dotnet natively)
make up              # recreates api with the new keys
```

Never share `VAPID_PRIVATE_KEY`; the API only ever exposes the public key
(`GET /v1/push/vapid-public-key`). `make secrets FORCE=1` does **not** rotate
VAPID keys — they have their own target because the blast radius is different.

## Why the matcher has a key even though it has no port

`matcher` is only reachable from containers on the compose network, so the key
is defence in depth: a compromised `web` or `cloudflared` container (or any
future service on that network) still cannot feed it data or probe it without
`MATCHER_KEY`, which only `api` and `matcher` receive. The key protects no
stored data, so rotating it is free: clear `MATCHER_KEY=` in `.env`, run
`make secrets` (it fills only empty values) and `make up` restarts both with
the new value.

## Why the API has no public port

The browser only ever talks to the SvelteKit server. SvelteKit holds the
tokens in httpOnly cookies and calls the API over the private Docker network
at `http://api:8080`. Consequences:

- The attack surface exposed to the internet is one Node process serving HTML
  and a handful of form actions — not the authentication API.
- There is no CORS configuration to get wrong; cross-origin requests to the
  API are impossible because it has no address outside the compose network.
- Tokens never exist in JavaScript-readable storage, so an XSS bug cannot
  exfiltrate them.
- Even on the Pi itself `curl localhost:8080` fails; use `docker compose exec`.

## Operational hygiene

- Keep the Pi updated: `sudo apt-get update && sudo apt-get upgrade`, and
  `make pi` to pick up new images (Dependabot keeps base images fresh).
- Back up (`make backup`) and store copies off the Pi; dumps contain all
  financial data — treat them like the database (`chmod 600`, encrypted disk).
  The backup folder now also holds a copy of `.env` per dump (it carries
  `DATA_KEY`, without which linked banks cannot be restored) — it is a secret
  like the dump itself.
- `.env` is `chmod 600`; never paste it into chats or issues.
- Time sync matters for token validation, VAPID tokens and reminder timing
  (`timedatectl`); the scheduler uses the household time zone from Settings.
- `make scheduler-logs` shows what the background job did; the `/activity`
  feed is the user-visible audit trail.

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Email the
maintainer (see the GitHub profile of the repository owner) with a description
and reproduction steps. You will get an acknowledgement within a few days, and
a fix or mitigation as soon as practical. Thank you for helping keep two
people's money private.
