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

**Out of scope**: a fully compromised Pi (root access), a compromised
Cloudflare account, or malware on the phone itself. Keep the OS updated, use
strong passwords on the Pi and Cloudflare, and enable 2FA on Cloudflare.

## Controls

| Threat | Control | Where |
|---|---|---|
| Exposed services on the internet | Only `web` is reachable, and only through the Cloudflare tunnel (outbound connection, no port forwarding). `api` and `db` have **no published ports**. | `compose.yml` (`ports:` only on `web`, bound to `127.0.0.1`), `infra/cloudflared/README.md` |
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
| Supply chain | Pinned base images, Dependabot for npm/NuGet/Docker/Actions weekly, CI builds images from source and publishes to GHCR under your account. | `.github/dependabot.yml`, `.github/workflows/ci.yml` |
| Optional second factor | Cloudflare Access with email one-time codes for exactly your two addresses in front of the hostname. | `infra/cloudflared/README.md` §5 |

## If a phone is lost or stolen

1. From any other device, log in and go to **Settings → Change password**.
   Changing the password (`POST /v1/auth/password`) **revokes every refresh
   token** for your account. The lost phone's 15-minute access token expires
   on its own; after that it cannot obtain a new one.
2. If you suspect the *partner's* account is also at risk, have them do the
   same.
3. If you use Cloudflare Access, also revoke the user's session in **Zero
   Trust → Access → … → Revoke session**.
4. Use the phone vendor's remote wipe (Find My / Find My Device).

There is nothing to do on the Pi; no data is stored on the phone except the
cached app shell (no financial data is precached by the service worker).

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

`make secrets FORCE=1` regenerates **both** secrets; only do that on a fresh
database, because `POSTGRES_PASSWORD` is baked into the Postgres data
directory on first start (see `docs/RASPBERRY-PI.md` → *Changing the database
password*).

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
- `.env` is `chmod 600`; never paste it into chats or issues.
- Time sync matters for token validation (`timedatectl`).

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Email the
maintainer (see the GitHub profile of the repository owner) with a description
and reproduction steps. You will get an acknowledgement within a few days, and
a fix or mitigation as soon as practical. Thank you for helping keep two
people's money private.
