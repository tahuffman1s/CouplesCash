# CouplesCash API

.NET 10 Minimal API backing the CouplesCash PWA. Implements [`docs/API-CONTRACT.md`](../../docs/API-CONTRACT.md)
on PostgreSQL 16 with Npgsql + Dapper, DbUp migrations, Argon2id passwords, HS256 JWTs with rotating
refresh tokens and Scalar docs. v2 adds Web Push (VAPID), WebAuthn passkeys (Fido2NetLib), an activity feed,
expenses, budgets, savings goals, a to-pay checklist, household settings, the pay-period view, the month report
and an in-process scheduler (autopay, reminders, hygiene).

```
apps/api/
├─ CouplesCash.slnx
├─ Directory.Build.props          # net10.0, nullable, warnings-as-errors, invariant globalization
├─ Dockerfile / .dockerignore     # multi-stage, multi-arch, non-root (USER app), port 8080
├─ CouplesCash.Api.http           # VS Code REST Client walkthrough
├─ scripts/smoke.sh               # curl + jq end-to-end smoke test
├─ CouplesCash.Api/
│  ├─ Program.cs                  # host, DI, pipeline, --migrate, --vapid
│  ├─ Endpoints/                  # one static class per area, MapGroup("/v1")
│  ├─ Auth/                       # Argon2idPasswordHasher, TokenService, CoupleCodeService, JwtOptions, WebAuthnOptions
│  ├─ Domain/                     # pure, unit-tested: RecurrenceEngine, PayPeriodEngine, GoalProjection, SchedulerRules, BudgetMath, Money
│  ├─ Data/                       # NpgsqlDataSource, Dapper repositories, UnitOfWork, DbUpMigrator
│  ├─ Services/                   # AuthSessionService, OccurrenceService, SummaryService, BudgetService, GoalService,
│  │                              # ActivityService (+ summaries), PasskeyService (Fido2NetLib), DTO mapping
│  ├─ Push/                       # PushOptions, VapidKeys (--vapid), WebPushSender (Lib.Net.Http.WebPush), PushNotifier queue + dispatcher
│  ├─ Scheduler/                  # SchedulerService hosted service (autopay, reminders, hygiene)
│  ├─ Validation/                 # FluentValidation validators + endpoint filter
│  ├─ Infrastructure/             # ProblemDetails, exception handler, rate limiting, OpenAPI bearer, options, feature filters
│  └─ Migrations/000N_*.sql       # embedded, journaled in public.schema_versions (0001 = v1, 0002 = v2)
└─ CouplesCash.Api.Tests/         # xunit: recurrence, pay period, goal projection, scheduler rules, budget math,
                                  # VAPID keys, activity summaries, WebAuthn config, couple codes, tokens, Argon2
```

## Run locally

Prerequisites: .NET 10 SDK and a PostgreSQL 16 database (`CREATE DATABASE couplescash;` — the
`citext` extension is created by the first migration, so the DB user needs `CREATE` on the database
or the extension must already exist).

```bash
# Development defaults (appsettings.Development.json) expect:
#   Host=localhost;Port=5432;Database=couplescash;Username=couplescash;Password=couplescash
# Override anything with env vars (double underscore = section separator):
export ConnectionStrings__Default="Host=localhost;Port=5432;Database=couplescash;Username=postgres"
export Jwt__Secret="$(openssl rand -base64 48)"       # optional in Development (a dev default is used with a warning)

dotnet run --project CouplesCash.Api                   # migrates on startup, listens on http://localhost:8080
```

- Health: `GET http://localhost:8080/health`
- Docs: `http://localhost:8080/scalar` and `http://localhost:8080/openapi/v1.json` (Development, or `Api__EnableDocs=true`)

## Migrate only

```bash
dotnet run --project CouplesCash.Api -- --migrate      # exit code 0 = schema up to date
# or from the published output / container:
dotnet CouplesCash.Api.dll --migrate
```

Startup migration can be disabled with `Database__MigrateOnStartup=false` (then run `--migrate` as a one-off job).

## Generate VAPID keys (Web Push)

```bash
dotnet run --project CouplesCash.Api -- --vapid
# or from the published output / container:
dotnet CouplesCash.Api.dll --vapid
```

Prints exactly two lines — `Push__VapidPublicKey=…` and `Push__VapidPrivateKey=…` — and exits 0. It runs before
any configuration is read (no database, no `Jwt__Secret`, read-only filesystem is fine), so compose/installer can
call it on a fresh image. Put both in the environment (plus `Push__Subject`, a `mailto:` or https URL); until
then every `/v1/push/*` route answers `503 push_not_configured` and the web hides the notification UI.

## Test, format, build

```bash
dotnet build -warnaserror
dotnet test
dotnet format --verify-no-changes
scripts/smoke.sh http://localhost:8080                 # needs a running API + curl + jq
SMOKE_SCHEDULER_WAIT=70 scripts/smoke.sh               # also creates an autopay bill due today and waits for the scheduler
```

The smoke script adapts to the server: with VAPID keys / an https (or `http://localhost`) WebAuthn origin it
exercises the push subscription flow and the passkey ceremonies; without them it checks the 503 problems.

## Configuration

| Setting (env form) | Default | Notes |
|---|---|---|
| `ConnectionStrings__Default` | — (required) | Npgsql connection string |
| `Jwt__Secret` | — | ≥ 32 bytes. **Required outside Development**; startup fails otherwise |
| `Jwt__AccessMinutes` | `15` | Access token lifetime |
| `Jwt__RefreshDays` | `30` | Refresh token lifetime |
| `Api__EnableDocs` | `false` | Serve `/scalar` + `/openapi/v1.json` (always on in Development) |
| `Api__AllowedOrigins` | empty | CORS origins, comma-separated or indexed (`Api__AllowedOrigins__0`). Empty = CORS off (the BFF talks server-to-server) |
| `Database__MigrateOnStartup` | `true` | Apply pending migrations before listening |
| `RateLimiting__AuthPerMinute` | `10` | Fixed window per IP for credential endpoints and `/v1/couple/join` |
| `RateLimiting__GlobalPerMinute` | `120` | Fixed window per IP for everything else |
| `Push__VapidPublicKey` | empty | base64url P-256 public key from `--vapid`. Blank (compose passes `""` when unset) = push disabled (503 `push_not_configured`) |
| `Push__VapidPrivateKey` | empty | base64url P-256 private key from `--vapid`. **Secret**; never logged, never served |
| `Push__Subject` | `https://couplescash.app` | VAPID contact: `mailto:you@example.com` or an https URL |
| `WebAuthn__Origin` | `Api__PublicUrl` | Public origin of the PWA. Passkeys are enabled only for https — or `http://localhost[:port]` for native dev; anything else → 503 `passkeys_not_configured` |
| `WebAuthn__RelyingPartyId` | host of origin | WebAuthn rpId (override only for sub-domain setups) |
| `WebAuthn__RelyingPartyName` | `CouplesCash` | Shown by the authenticator |
| `Api__PublicUrl` | empty | Public URL of the app (`PUBLIC_URL`); default WebAuthn origin |
| `Scheduler__Enabled` | `true` | Run the autopay / reminder / hygiene job (set `false` in tests) |
| `Scheduler__IntervalSeconds` | `60` | Tick interval |
| `Scheduler__AutopayLookbackDays` | `3` | Autopay still marks due dates up to this many days back (Pi was off) |
| `ASPNETCORE_URLS` | `http://+:8080` | Listening address |
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Development` enables docs, readable logs and the dev JWT secret |

Logging is Serilog to stdout: human-readable in Development, compact JSON (one event per line) otherwise.
Log levels come from the `Serilog` section of `appsettings.json`.

## Docker

```bash
docker build -t couplescash-api apps/api                       # context = apps/api
docker run --rm -p 8080:8080 \
  -e ConnectionStrings__Default="Host=db;Database=couplescash;Username=couplescash;Password=..." \
  -e Jwt__Secret="$(openssl rand -base64 48)" \
  couplescash-api
```

The image runs as the non-root `app` user, needs no writable filesystem (`read_only: true` is fine)
and has no `HEALTHCHECK` (the runtime image ships without curl/wget); use a compose healthcheck
against `GET /health`.

## Behaviour notes

- Household scoping on every data route comes from the `hh` JWT claim; after `POST /v1/couple/code`
  (which creates the household on first use) the client must call `/v1/auth/refresh`. `join` and
  `leave` return a fresh `AuthResult` directly.
- `/v1/couple/*` reads membership from the database so it works with a stale token.
- Unknown or foreign `{id}` → `404` problem; validation → `400` with `errors: { field: [msg] }`;
  rate limited → `429` with `Retry-After`.
- Couple codes: `code` = first 40 bits of SHA-256(payload bytes) in Crockford base32, `XXXX-XXXX`.
  Both `sha256(payload bytes)` and `sha256("XXXXXXXX")` (upper-case, no dash) are stored; typed input
  is normalised (case, dashes/spaces, `O→0`, `I/L→1`).
- `PUT /recurring/{id}/occurrences/{date}/paid` returns 404 unless `{date}` is produced by the
  recurrence engine for that item (month-end clamping included).
- Summary: `dueThisMonth/paid/remaining` cover bills + subscriptions due in the month (v2: `due` and `paid` also
  add the month's expenses); `monthly*`, `byCategory`, `byKind` are monthly equivalents of items active in the
  month (`byCategory` adds expenses).
- Every mutation writes an `activity_events` row in the same transaction (`UnitOfWork`), then — after commit —
  queues a "<Actor> <summary>" push to the other member if they have `activityEnabled`. Summaries are plain text.
- Money is integer hundredths of the household `currency` for every currency; the API never converts.

## Scheduler

`SchedulerService` (hosted service, `Scheduler/`) ticks every `Scheduler__IntervalSeconds` once the app is
listening:

1. **Autopay** — for each household, "today" in its `timezone`; each active `autoPay` item with an occurrence due
   today (or up to `Scheduler__AutopayLookbackDays` back) and no payment gets a payment row with `auto = true`,
   `paid_by NULL`, an `occurrence.autopaid` activity event (`actor_id NULL`) and a push to members with
   `activityEnabled`. **Undo guard:** un-marking an auto-paid occurrence writes `autopay_skips`, so the scheduler
   leaves it alone; marking it paid by hand clears the skip.
2. **Reminders** — for each user with a push subscription whose local hour (household tz) equals `hourLocal`
   and `remindersEnabled`: unpaid bills/subscriptions due on `today + d` for each `d` in `daysBefore` → one push
   per occurrence ("Electric is due tomorrow · $142.50", url `/calendar?m=YYYY-MM&d=YYYY-MM-DD`), logged in
   `reminder_log` so it is sent once per (item, due date, user).
3. **Hygiene** — expired `webauthn_challenges` and push subscriptions with ≥ 5 failures are deleted.

Selection logic is pure (`Domain/SchedulerRules.cs`, unit-tested); the service only does I/O and catches every
exception. Logs are under the `CouplesCash.Api.Scheduler.SchedulerService` category (`make scheduler-logs`).

Push delivery runs on a bounded in-memory queue (`PushNotifier` → `PushDispatchService`) so requests never wait
on the push service; 404/410 from the push service delete the subscription immediately, other failures count up.
