# CouplesCash — Build Plan

> A mobile-first PWA for two people to see their shared money at a glance: bank balances, bills, subscriptions, a calendar, and a pie chart. Replaces "notes app → screenshot → text message".

This plan is optimized for **vibe coding**: every section is small, self-contained, and names the files it owns, so a human or an agent can pick up any box and run with it. The API contract lives in [API-CONTRACT.md](./API-CONTRACT.md) and is the source of truth between the frontend and backend.

---

## 1. Goals & non-goals

**Goals**
- One shared "household" per couple, linked with a fun, secure **Couple Code** (a styled QR + 8-character typed fallback).
- Manual bank accounts (name, institution, type, balance). No bank linking.
- Recurring bills, subscriptions and income with real recurrence rules; mark occurrences paid.
- Calendar view of everything that is due; pie chart breakdown by category; dashboard with net worth and "what's coming up".
- Light/dark that follows the device, with a manual override.
- Installable PWA on iOS Safari and Android Chrome. No app stores, no Apple developer account.
- Runs well on a **Raspberry Pi 4 (arm64)** with Docker Compose, exposed through a **Cloudflare Tunnel** (no port forwarding).
- Secure by default: Argon2id passwords, short-lived JWTs with rotating refresh tokens, household-scoped queries, rate limiting, strict headers, non-root containers, API never exposed to the internet.

**Non-goals (v1)**
- Bank sync (Plaid etc.), transactions import, budgets/envelopes, multi-currency, more than two members, push notifications (planned for v2).

---

## 2. Stack (pinned to latest stable as of Oct 2026)

| Layer | Choice | Why |
|---|---|---|
| Frontend | SvelteKit 3 + Svelte 5 (runes), TypeScript, `adapter-node` | SSR + form actions = fast on a Pi, progressive enhancement, tiny client bundles |
| Styling | Tailwind CSS 4 (`@tailwindcss/vite`) + daisyUI 5 | Themeable components, `prefersdark` theme support out of the box |
| PWA | SvelteKit built-in service worker (`src/service-worker/index.ts`) + `static/manifest.webmanifest` | No extra dependency; precache app shell, network-first for data |
| QR | `qr-code-styling` (render) + `jsqr` (scan via `getUserMedia`) | Styled "Couple Code" with rounded dots and a heart in the middle; scanner works in iOS PWA standalone |
| Charts | Hand-rolled SVG donut (Svelte component) | Zero deps, theme-aware colours, ~2 KB |
| E2E | Playwright | Mobile viewports (iPhone 14, Pixel 7), API mocked with `page.route` |
| API | .NET 10 Minimal APIs, `Microsoft.AspNetCore.OpenApi` + **Scalar** | Native AOT-friendly, fast startup on arm64, Scalar docs at `/scalar` (dev only) |
| Data access | Npgsql + Dapper | Explicit SQL, no ORM magic, trivial to audit for household scoping |
| Migrations | **DbUp** (`dbup-postgresql`), embedded `.sql` scripts, run by the API on startup (`--migrate` also runs standalone) | Simple, idempotent, journaled |
| DB | PostgreSQL 16 (`postgres:16-alpine`) | arm64 image, low memory footprint |
| Auth | Argon2id (Konscious), JWT HS256 access tokens (15 min), opaque refresh tokens (30 days, rotated, SHA-256 hashed at rest, reuse detection) | Industry standard, no external IdP |
| Edge | `cloudflare/cloudflared` container with a tunnel token | Zero port forwarding, free TLS, optional Cloudflare Access in front |
| Containers | Multi-stage Dockerfiles, `compose.yml`, non-root, read-only FS, memory limits | Pi-friendly |
| Dev | VS Code workspace, `launch.json` (compound API + web), `tasks.json`, `extensions.json`, `Makefile`, `.env.example`, Linux-first scripts | Clone → `make dev` → coding |
| CI | GitHub Actions: lint + unit + e2e + dotnet test; multi-arch image publish to GHCR on `main` | Pi pulls prebuilt arm64 images instead of compiling |

---

## 3. Architecture

```
            ┌──────────────┐   HTTPS (Cloudflare Tunnel)
 phones ───▶│  cloudflared │──────────────┐
            └──────────────┘              ▼
                                   ┌──────────────┐  server-side fetch   ┌──────────────┐      ┌────────────┐
                                   │  web (Kit)   │ ───────────────────▶ │  api (.NET)  │ ───▶ │  postgres  │
                                   │  :3000       │   http://api:8080    │  :8080       │      │  :5432     │
                                   └──────────────┘                      └──────────────┘      └────────────┘
                                   BFF: owns the httpOnly cookies         JWT bearer auth,       only on the
                                   and talks to the API on the            household-scoped       internal network
                                   internal docker network                SQL, rate limits
```

**BFF (backend-for-frontend) pattern.** The browser only ever talks to SvelteKit. SvelteKit's server (`src/lib/server/api.ts`) holds the access/refresh tokens in `httpOnly; Secure; SameSite=Lax` cookies, forwards requests to the API with a bearer token, and transparently refreshes on 401. Benefits:
- The API is **not reachable from the internet** at all — only `web` is published through the tunnel.
- No tokens in `localStorage`, no CORS surface, SvelteKit's built-in origin check protects form actions from CSRF.
- Client-side fetches (calendar month navigation, etc.) go through `/api/[...path]` which is a thin authenticated proxy requiring the `X-Requested-With: CouplesCash` header.

**Money** is always integer cents (`amountCents: number` / `bigint`). **Dates** are ISO `YYYY-MM-DD` strings; timestamps are ISO 8601 UTC. **IDs** are UUID v7.

---

## 4. Repository layout

```
CouplesCash/
├─ apps/
│  ├─ api/                      # .NET 10 solution
│  │  ├─ CouplesCash.Api/       # Minimal API host (Program.cs, Endpoints/, Auth/, Data/, Migrations/*.sql)
│  │  ├─ CouplesCash.Api.Tests/ # xunit: recurrence engine, couple-code, token service, endpoint auth tests
│  │  ├─ Dockerfile
│  │  └─ CouplesCash.slnx
│  └─ web/                      # SvelteKit app
│     ├─ src/routes/            # see §6
│     ├─ src/lib/               # components/, server/, stores/, utils/
│     ├─ e2e/                   # Playwright
│     ├─ static/                # manifest, icons
│     └─ Dockerfile
├─ infra/
│  ├─ cloudflared/              # README + example config
│  └─ postgres/                 # postgresql.conf tuned for Pi 4
├─ docs/                        # PLAN.md, API-CONTRACT.md, RASPBERRY-PI.md, SECURITY.md
├─ .vscode/                     # settings, launch (compound), tasks, extensions
├─ .github/workflows/ci.yml
├─ compose.yml                  # web + api + db (+ cloudflared via `--profile tunnel`)
├─ compose.dev.yml              # hot reload overrides
├─ Makefile                     # make dev / make up / make pi / make test
├─ .env.example
├─ CLAUDE.md                    # vibe-coding cheat-sheet for agents
└─ README.md
```

---

## 5. Data model (PostgreSQL)

```
users                 id uuid pk, email citext unique, display_name, password_hash, created_at
households            id uuid pk, name, created_at
household_members     household_id fk, user_id fk unique, joined_at   -- CHECK ≤ 2 enforced in SQL trigger
couple_codes          id uuid pk, household_id fk, created_by fk, code_hash bytea unique, expires_at, redeemed_at null, redeemed_by null
refresh_tokens        id uuid pk, user_id fk, token_hash bytea unique, family_id uuid, expires_at, revoked_at null, user_agent, created_at
categories            id uuid pk, household_id fk, name, color, icon, sort_order, is_default
accounts              id uuid pk, household_id fk, name, institution, type (checking|savings|credit|cash|investment|loan), balance_cents bigint, color, icon, note, created_by, created_at, updated_at, archived_at null
account_balance_snapshots  id, account_id fk, balance_cents, recorded_at, recorded_by
recurring_items       id uuid pk, household_id fk, name, kind (bill|subscription|income), amount_cents bigint, category_id fk null, account_id fk null, frequency (weekly|biweekly|monthly|quarterly|yearly|once), anchor_date date, end_date date null, auto_pay bool, notes, url, created_by, created_at, updated_at, archived_at null
occurrence_payments   recurring_item_id fk, due_date date, paid_at, paid_by, amount_cents   pk(recurring_item_id, due_date)
```

Every table with `household_id` is only ever queried with `WHERE household_id = @HouseholdId` where the id comes from the **JWT claim**, never from the request body.

**Recurrence rule**: occurrences are generated from `anchor_date` stepping by `frequency`; monthly/quarterly/yearly clamp day-of-month to month end (31st → Feb 28/29). `once` yields a single occurrence. The engine is a pure function `RecurrenceEngine.Expand(item, from, to)` with unit tests.

---

## 6. Frontend screens (mobile-first, daisyUI)

| Route | What |
|---|---|
| `/login`, `/register` | Email + password, form actions, friendly errors |
| `/` Dashboard | Net worth hero, "Due in the next 14 days" list, mini donut for this month, quick-add FAB |
| `/couple` | If unlinked: **Show my Couple Code** (styled QR + typed code + countdown) / **Scan partner's code** (camera) / **Type a code**. If linked: both avatars, "linked since", leave |
| `/accounts` | Cards grouped by type with coloured icons, totals; `/accounts/new`, `/accounts/[id]` edit + quick balance update + sparkline history |
| `/bills` | Recurring items list, filter chips (bills / subscriptions / income), monthly-equivalent total; `/bills/new`, `/bills/[id]` |
| `/calendar` | Month grid with dots per due item, swipe/arrow month nav, day drawer listing items, tap to mark paid |
| `/insights` | Donut by category (monthly equivalent), by kind, top subscriptions |
| `/settings` | Theme (System / Light / Dark), profile, sign out, "Install app" hint |

UX rules: bottom `dock` nav with 5 items (Home, Accounts, Bills, Calendar, More), 44px tap targets, safe-area insets, `prefers-reduced-motion` respected, optimistic UI for "mark paid", empty states with a single clear CTA, money formatted with `Intl.NumberFormat`.

Theme: daisyUI themes `couplescash` (light) and `couplescash-dark` (dark, `prefersdark: true`); `data-theme` set from a `theme` cookie so SSR renders the right theme with no flash; "System" = no attribute.

---

## 7. Security checklist (see docs/SECURITY.md)

- [x] Argon2id (64 MB, 3 iterations, parallelism 1 — Pi-friendly but still slow for attackers)
- [x] Access JWT 15 min, HS256 with ≥ 256-bit secret from env; refresh 30 days, opaque 256-bit, hashed at rest, rotated on every use, **family revoked on reuse**
- [x] Rate limiting: `/auth/*` and `/couple/join` fixed window 10 req/min per IP; global 120 req/min
- [x] Couple codes: 20 random bytes → 8 Crockford-base32 chars shown, full 160-bit payload in QR; 10 min TTL; single use; hashed at rest
- [x] All queries scoped by household from the JWT; ownership checked on every `/{id}` route (404, never 403, to avoid enumeration)
- [x] FluentValidation on every request body; `MaxRequestBodySize` 64 KB
- [x] ProblemDetails (RFC 9457) errors, no stack traces in prod
- [x] Web: CSP, HSTS, `X-Content-Type-Options`, `Referrer-Policy`, `Permissions-Policy` (camera=self), cookies `httpOnly Secure SameSite=Lax`
- [x] Containers run as non-root with read-only root FS; secrets only via env; Scalar disabled unless `Api__EnableDocs=true`
- [x] API has no published port in `compose.yml`; only `web` is reachable via the tunnel
- [x] Optional: put Cloudflare Access (free for ≤ 50 users) in front for a second factor

---

## 8. Raspberry Pi 4 (see docs/RASPBERRY-PI.md)

- Requires 64-bit Raspberry Pi OS (arm64). 2 GB model works; 4 GB recommended.
- Images are multi-arch (`linux/amd64`, `linux/arm64`) published to GHCR by CI; the Pi just `docker compose pull && docker compose up -d`.
- Memory budget: postgres 256 MB (`shared_buffers=64MB`), api 192 MB (`DOTNET_GCHeapHardLimit`, workstation GC, `TieredPGO` on), web 160 MB, cloudflared 64 MB.
- `compose.yml` sets `mem_limit`, `restart: unless-stopped`, healthchecks, and logs capped at 10 MB.
- Postgres data on a bind mount; `make backup` dumps to `./backups/`.

---

## 9. Execution plan (parallel agents)

| # | Agent | Owns | Done when |
|---|---|---|---|
| A | **API** | `apps/api/**` | `dotnet build` + `dotnet test` green; migrations apply to a fresh Postgres; every endpoint in API-CONTRACT.md implemented; Scalar renders in dev |
| B | **Web** | `apps/web/**` | `pnpm check`, `pnpm lint`, `pnpm build` green; Playwright e2e passes with mocked API on mobile viewports; Lighthouse PWA installable |
| C | **Infra & DevX** | `compose*.yml`, `Makefile`, `.env.example`, `.vscode/**`, `infra/**`, `.github/**`, `docs/RASPBERRY-PI.md`, `docs/SECURITY.md`, `README.md`, `CLAUDE.md` | `docker compose config` valid; README walks a new Pi from zero to a tunnelled URL in ≤ 10 steps |
| — | **Integrator** (main thread) | glue | API runs against local Postgres, web talks to the real API, smoke test register → link → add account → add bill → calendar → paid |

Phases after v1 (not in this pass): push reminders (Web Push works on iOS 16.4+ PWAs), CSV import, budgets, transaction logging, Cloudflare Access setup guide.

---

## 10. Status (2026-10-23)

v1 is built and verified:
- API: `dotnet build -warnaserror` clean, 70 unit tests, migrations applied to PostgreSQL 16, curl smoke script green.
- Web: `pnpm check` 0 errors, lint clean, 21 unit tests, 18 Playwright e2e tests on iPhone 14 + Pixel 7, passing against both the mock API and the real API + Postgres.
- Infra: compose validated, scripts shellcheck-clean, CI publishes multi-arch images to GHCR.
- Not yet exercised here: a real `docker compose up` (no Docker daemon in the build sandbox) and a Cloudflare tunnel. Follow README → "Deploy to a Raspberry Pi".
