# CouplesCash API

.NET 10 Minimal API backing the CouplesCash PWA. Implements [`docs/API-CONTRACT.md`](../../docs/API-CONTRACT.md)
on PostgreSQL 16 with Npgsql + Dapper, DbUp migrations, Argon2id passwords, HS256 JWTs with rotating
refresh tokens and Scalar docs. v2 adds Web Push (VAPID), WebAuthn passkeys (Fido2NetLib), an activity feed,
expenses, budgets, savings goals, a to-pay checklist, household settings, the pay-period view, the month report
and an in-process scheduler (autopay, reminders, hygiene). v3 adds SimpleFIN bank sync (encrypted access URLs,
scheduled + manual sync, balance updates), CSV import, the review inbox with a suggestion engine, and auto-matching. v4 adds the
week view (`/v1/week`: planned items per day, left at end of week, overdue), planned one-offs with a `payeeHint` that reconciles
from the bank, weekly budgets + an overall weekly target (`/v1/budgets/week`, `?period=weekly`) and week-summary pushes. v5 adds
the pending lifecycle (a posted transaction with a new id replaces its pending row in place; stale authorizations are dropped),
available balances + pending outflow on linked accounts (spendable uses the available balance), transfer detection with
auto-ignore, learned transfer payees and a transfer report (`/v1/transfers`), per-account transactions
(`/v1/accounts/{id}/transactions`), and matching any transaction to bills from either side (candidates, match, unmatch,
bill from a transaction, bill payments, link). v6 hands scoring to the Python **matcher** service (`apps/matcher`,
[`docs/MATCHER-CONTRACT.md`](../../docs/MATCHER-CONTRACT.md)) with the built-in C# matcher as fallback: bank names per item
(user + learned), `amountVaries`, a household auto-match level (`off | bills | all`), automatic matching on every sync, CSV import,
item change and a scheduler sweep, found bills (`/v1/bill-suggestions`), the pending view (`/v1/pending`), the all-accounts
Transactions feed (`/v1/transactions`) and `/v1/matcher/status`.

```
apps/api/
├─ CouplesCash.slnx
├─ Directory.Build.props          # net10.0, nullable, warnings-as-errors, invariant globalization
├─ Dockerfile / .dockerignore     # multi-stage, multi-arch, non-root (USER app), port 8080
├─ CouplesCash.Api.http           # VS Code REST Client walkthrough
├─ scripts/smoke.sh               # curl + jq end-to-end smoke test
├─ scripts/fake-simplefin.py      # protocol-shaped fake SimpleFIN Bridge for the smoke test (python3, stdlib only)
├─ CouplesCash.Api/
│  ├─ Program.cs                  # host, DI, pipeline, --migrate, --vapid
│  ├─ Endpoints/                  # one static class per area, MapGroup("/v1")
│  ├─ Auth/                       # Argon2idPasswordHasher, TokenService, CoupleCodeService, JwtOptions, WebAuthnOptions
│  ├─ Matching/                   # v6: MatcherClient (typed HttpClient, X-Matcher-Key, chunking), MatchingService (matcher → built-in
│  │                              # fallback, auto-match / auto-categorise, sweeps), MatchingQueue, MatcherStatusTracker, wire models
│  ├─ Domain/                     # pure, unit-tested: RecurrenceEngine, PayPeriodEngine, GoalProjection, SchedulerRules, BudgetMath, Money,
│  │                              # TransactionMatcher (suggestions), Fingerprint (dedupe), BankSyncRules (interval, cooldown, window),
│  │                              # WeekEngine (week bounds, status/variance, left-at-end, weekly budget rows + history, spendable),
│  │                              # PendingReconciler (v5 pending → posted / dropped / superseded), TransferAnalysis (+ PaymentHistory),
│  │                              # v6: BankNames (validation, learning), MatchingRules (levels, built-in detail), RematchRules (debounce,
│  │                              # rate limit), BillSuggestionRules (fingerprint, upsert plan, anchor), PendingGrouping
│  ├─ Data/                       # NpgsqlDataSource, Dapper repositories, UnitOfWork, DbUpMigrator
│  ├─ Services/                   # AuthSessionService, OccurrenceService, SummaryService, BudgetService, GoalService,
│  │                              # ActivityService (+ summaries), PasskeyService (Fido2NetLib), DTO mapping,
│  │                              # BankSyncService (claim/sync/mapping), InboxService (matcher run, auto-match, confirm/ignore/restore),
│  │                              # WeekService (composes /week and /budgets/week; also feeds the week-summary pushes)
│  ├─ SimpleFin/                  # ISimpleFinClient + SimpleFinClient (claim, GET /accounts, URL masking/rewrite), SimpleFinParser
│  ├─ Security/                   # DataProtector: AES-256-GCM for the stored access URL (Security__DataKey)
│  ├─ Push/                       # PushOptions, VapidKeys (--vapid), WebPushSender (Lib.Net.Http.WebPush), PushNotifier queue + dispatcher
│  ├─ Scheduler/                  # SchedulerService hosted service (autopay, reminders, hygiene, bank sync, rematch sweep, found bills,
│  │                              # week summaries) + MatchingWorker (debounced immediate rematch / found bills)
│  ├─ Validation/                 # FluentValidation validators + endpoint filter
│  ├─ Infrastructure/             # ProblemDetails, exception handler, rate limiting, OpenAPI bearer, options, feature filters
│  └─ Migrations/000N_*.sql       # embedded, journaled in public.schema_versions (0001 = v1, 0002 = v2, 0003 = v3 imports, 0004 = v4 week,
│                                 # 0005 = v5 transactions, 0006 = v6 matcher: bank names, auto-match level, match detail, bill_suggestions)
└─ CouplesCash.Api.Tests/         # xunit: recurrence, pay period, goal projection, scheduler rules (+ week summaries), budget math,
                                  # WeekEngine, VAPID keys, activity summaries, WebAuthn config, couple codes, tokens, Argon2,
                                  # DataProtector, Fingerprint, TransactionMatcher (+ one-off tolerances), SimpleFIN parser/client helpers,
                                  # bank sync rules, v3 + v4 validators, PendingReconciler, v5 transfers/candidates/analysis/validators,
                                  # v6 MatcherClient (contract JSON, chunking, 401/413/422/5xx/timeout/disabled fallback), bank names,
                                  # learning, rematch debounce/rate limit, found bills, pending grouping, built-in detail
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
exercises the push subscription flow and the passkey ceremonies; without them it checks the 503 problems. Without a
`Security__DataKey` it checks that claiming a bank link answers `503 bank_sync_not_configured`.

To run the full **bank-sync flow** against the fake Bridge (`scripts/fake-simplefin.py`, started by the smoke script):

```bash
# terminal 1 — API pointed at the fake Bridge, syncing every scheduler tick
export Security__DataKey="$(openssl rand -base64 32)" SimpleFin__BaseUrl=http://127.0.0.1:8765 \
       Sync__IntervalHours=0 Scheduler__IntervalSeconds=5 RateLimiting__GlobalPerMinute=100000
dotnet run --project CouplesCash.Api
# terminal 2
SMOKE_SIMPLEFIN_PORT=8765 SMOKE_SCHEDULER_WAIT=40 scripts/smoke.sh http://localhost:8080
```

That walks: claim (first sync inline) → 2 external accounts → map checking (balance + snapshot) → create-and-map the
credit card (−540.00 → 54000 owed) → inbox: Rent suggested as occurrence ≥ 90, Kroger → Groceries, Netflix →
Subscriptions, payroll → Income, pending coffee → confirm Rent (occurrence paid, `source: bank`) → confirm Kroger as
expense (`deduct=false`) → ignore/restore → CSV import with a duplicate → learned payee rule → bulk confirm-suggested →
(v4) the Bridge posts "JIFFY LUBE #42" −54.20 → manual sync auto-matches the planned $60 "Oil change" one-off through its
`payeeHint` → `/week` shows it cleared at 54.20 with `varianceCents: -580` and the weekly budget rows over/under → 429 +
`Retry-After` → scheduler auto-match of a second rent payment → undo restores the inbox row → > 64 KB CSV body on the import
route → unlink. Before the bank part the smoke also covers the always-on v4 checks: weekly target + weekly category budgets,
`PUT /budgets/{id}?period=weekly` returning a `WeekBudgetRow`, `/week` for the current, a past (no snapshots → `leftAtEndCents`
null) and a future (`projected`) week, and that planning the one-off lowers `leftAtEndCents` by exactly its amount.

**v6 matcher flow.** The smoke always checks the engine-independent v6 surface (bank names + legacy `payeeHint`, `amountVaries`,
`autoMatchLevel`, `/matcher/status`, rematch 202/429, `/pending`, `/transactions`, found-bill routes). With a matcher it replaces the
v3–v5 bank flows (they assert built-in scores) by a fuzzy-matcher flow in a fresh household against the fake Bridge's v6 dataset:

```bash
# terminal 1 — as above, plus the matcher (the smoke starts it itself on the URL's port when it is down and uv is installed,
# and then also stops it to check the built-in fallback)
export Matcher__BaseUrl=http://127.0.0.1:8090 Matcher__Key=devkey-devkey-devkey-devkey-devkey-12
dotnet run --project CouplesCash.Api
# terminal 2
SMOKE_SIMPLEFIN_PORT=8765 SMOKE_SCHEDULER_WAIT=40 SMOKE_MATCHER_URL=http://127.0.0.1:8090 \
  SMOKE_MATCHER_KEY=devkey-devkey-devkey-devkey-devkey-12 scripts/smoke.sh http://localhost:8080
```

It walks: Rent 1850 with bank name "Oakwood" + "ACH DEBIT OAKWOOD PROPERTY MGMT RENT" −1850 → auto-matched (`engine: fuzzy`,
score 100, reason) and "OAKWOOD PROPERTY MGMT RENT" learned → one-off "Oil change" 60 without a bank name + "JIFFY LUBE #42" −54.20 →
only a suggestion (name 30 %) → add bank name "Jiffy Lube" → debounced rematch auto-matches it → `/pending` grouped by account,
`/transactions` (pending pinned on page 1 only, keyset paging, search), `WeekView.pendingOutCents` → four NETFLIX.COM rows over
four months → found bill "Netflix monthly 15.49" → accept → item with the bank name and 4 past payments matched → Spotify dismissed and
never returned → level `off` keeps a perfect match a suggestion, `bills` + rematch applies it → matcher stopped → status
`reachable: false`, a bank-name match still auto-applies with `engine: builtin`.

With `SMOKE_SCHEDULER_WAIT > 0` the bank part also walks **v5** through further fake-Bridge phases (each picked up by the
scheduler, so every step polls for its effect): a pending "THE DINER" −40.00 confirmed as an expense posts under a new id at
−48.00 → one row, `pending: false`, still confirmed, the expense follows to 48.00 (`transaction.posted`); a $1.00 fuel hold
7 days old vanishes → `ignored/dropped`, hidden unless `includeHidden=1`, and the $45.12 fill-up is a separate row; a savings
account appears and is mapped → checking −500 / savings +500 are auto-ignored as a pair (`/v1/transfers` shows the flow);
with `autoIgnoreTransfers` off the next pair stays an 80 suggestion, is paired by hand and un-paired ("Not a transfer");
"TRANSFER TO BROKERAGE" is a 60 suggestion that bulk confirm skips → marked as a one-sided transfer → the next one is
auto-ignored at 85; `/v1/accounts/{id}/transactions` filters, search (parameterised), month, keyset paging, totals and a
manual expense; candidates + match a confirmed-expense row to a bill (expense deleted, occurrence paid) → unmatch; a bill
from a transaction + `/payments`; occurrence-side candidates + link + 409; `/week` and `/payperiod` spendable use
`availableCents`.

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
| `Scheduler__Enabled` | `true` | Run the autopay / reminder / hygiene / bank-sync / week-summary job (set `false` in tests) |
| `Scheduler__IntervalSeconds` | `60` | Tick interval |
| `Scheduler__AutopayLookbackDays` | `3` | Autopay still marks due dates up to this many days back (Pi was off) |
| `Security__DataKey` | empty | base64 of 32 random bytes (`openssl rand -base64 32`; `DATA_KEY` in `.env`). AES-256-GCM key for stored SimpleFIN access URLs. **Secret.** Blank = bank sync disabled: `POST /v1/banks/simplefin/claim` and `/sync` answer `503 bank_sync_not_configured`, `GET /v1/banks` reports `lastError: "encryption key missing"`. A malformed key fails startup. Rotation = re-link |
| `Sync__IntervalHours` | `6` | Scheduler syncs each enabled bank link when its last attempt is older than this (`0` = every tick; tests only) |
| `Sync__ManualCooldownMinutes` | `15` | "Sync now" cooldown per link (429 + `Retry-After`); counts manual syncs only |
| `Sync__ClaimSyncBudgetSeconds` | `20` | Time budget for the inline first sync during claim; on timeout the link is returned and the scheduler retries |
| `SimpleFin__BaseUrl` | empty | Test override: rewrites the scheme/host/port of the claim and access URLs (fake Bridge). Never set in production |
| `Matcher__BaseUrl` | empty | v6 Python matcher, `http://matcher:8090` in compose (`http://localhost:8090` native). Empty (or no key) = the built-in C# matcher is used and found bills are paused |
| `Matcher__Key` | empty | Shared secret sent as `X-Matcher-Key` (= `.env` `MATCHER_KEY`). **Secret**; never logged |
| `Matcher__TimeoutSeconds` | `30` | Per request; a timeout falls back to the built-in matcher |
| `Matcher__RematchMinutes` | `30` | Scheduler sweep over every To-review row per household (`0` = every tick) |
| `Matcher__DebounceSeconds` | `10` | Immediate rematch after an item / bank name / auto level change, and found bills after a sync, wait this long after the last trigger (≤ 60 s) |
| `Matcher__ChunkSize` | `1000` | Transactions per `/v1/match` request (candidates are cut to ±10 days of each chunk, ≤ `Matcher__MaxCandidates` = 2000) |
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
- v3: the SimpleFIN access URL is a credential — stored AES-256-GCM encrypted (`Security/DataProtector.cs`), decrypted
  only inside a sync, masked in logs (`https://***@…`) and never part of any response. Suggestion scoring, confirm/ignore/
  restore semantics, cursor format and every deviation from the contract tables are listed in
  [`docs/API-CONTRACT.md`](../../docs/API-CONTRACT.md) "v3 implementation notes".
- v5: the pending lifecycle, transfer rules, candidate scoring and the deviations from the "# v5 additions" tables are listed in
  [`docs/API-CONTRACT.md`](../../docs/API-CONTRACT.md) "v5 implementation notes (API)".

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

4. **Bank sync (v3)** — every enabled `bank_links` row whose `max(last_synced_at, last_attempt_at)` is older than
   `Sync__IntervalHours`: decrypt the access URL, `GET /accounts` (90 days on the first sync, 30 after, `pending=1`),
   upsert `external_accounts`, insert `imported_transactions` with fingerprint dedupe, run the matcher, **auto-match**
   recurring hits with confidence ≥ 90 whose item has a learned payee rule (`paid_by NULL, auto true, source 'bank'`,
   inbox row confirmed with `auto`, `transaction.automatched` event), push Bridge balances onto mapped accounts
   (credit/loan: negative-when-owed → positive owed, snapshot only when changed), write `bank.synced`, and push
   "Synced Chase · N to review" when N > 0. Failures land in `last_error` + a `bank.error` event and never stop the loop.
   v5: per external account the stored rows are first reconciled with the response (`Domain/PendingReconciler.cs`): same-id
   posts refresh in place, a posted transaction with a new id takes over its absent pending row (confirmation kept, expense /
   payment amount follows, `transaction.posted`), absent authorizations > 5 days old inside the fetch window are dropped
   (confirmation reversed, `transaction.dropped`) — absence is judged only on a response without Bridge errors — and every row
   in the response gets `last_seen_at`. After the matcher, detected transfer pairs and learned transfer payees are auto-ignored
   when the household's `autoIgnoreTransfers` is on (one `transfer.auto` event per sync).

Selection logic is pure (`Domain/SchedulerRules.cs`, `Domain/BankSyncRules.cs`, `Domain/TransactionMatcher.cs`,
unit-tested); the service only does I/O and catches every exception. Logs are under the `CouplesCash.Api.Scheduler.SchedulerService` category (`make scheduler-logs`).

Push delivery runs on a bounded in-memory queue (`PushNotifier` → `PushDispatchService`) so requests never wait
on the push service; 404/410 from the push service delete the subscription immediately, other failures count up.
