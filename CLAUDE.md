# CLAUDE.md — CouplesCash cheat-sheet for coding agents

Couples' shared-finance PWA, **tailored in v7 to one household's Friday routine** (see `docs/PLAN-V7.md`: the
pay-week plan "available − bills − fun money − one-offs = left on Thursday", payday checklist, savings sweep). Two users per household; manual accounts, recurring
bills/subscriptions/income, calendar, donut chart; v2 adds push reminders, autopay
auto-marking, activity feed, expenses + budgets, pay-period view, savings goals,
month report, to-pay checklist, household settings and passkeys; v3 adds bank sync
(SimpleFIN Bridge), CSV import and a review inbox; v4 adds the week view, planned one-offs
and weekly budgets; v5 adds the pending lifecycle, transfer detection/analysis, per-account
transactions and matching any transaction to bills. Runs on a Raspberry Pi 4 via Docker
Compose behind a Cloudflare Tunnel. Read `docs/PLAN.md` (v1), `docs/PLAN-V2.md` (v2) and
`docs/PLAN-V3.md` (v3), `docs/PLAN-V4.md` (v4), `docs/PLAN-V5.md` (v5), `docs/PLAN-V6.md` (v6), `docs/PLAN-V7.md` (v7 — read this first) for the why.

## Source of truth
- **`docs/API-CONTRACT.md` defines every route, shape and error** (v2 routes under
  "# v2 additions", v3 under "# v3 additions", v4 under "# v4 additions", v5 under "# v5 additions", v6 under "# v6 additions", v7 under "# v7 additions"). Web and API are built against it
  independently. Change the contract first, then both sides.
- `docs/PLAN.md` §5 + `docs/PLAN-V2.md` "Data model additions" + `docs/PLAN-V3.md` "Data
  model" are the data model; `docs/PLAN.md` §7 the security checklist (non-negotiable).

## Stack
- `apps/web` — SvelteKit 3, **Svelte 5 runes only**, TypeScript, Tailwind 4 + daisyUI 5,
  `adapter-node`, Playwright e2e. BFF: browser → SvelteKit → API. Tokens live in
  httpOnly cookies set by `src/lib/server/api.ts`; browser JS never sees them.
- `apps/api` — .NET 10 Minimal APIs, Npgsql + Dapper (hand-written SQL), DbUp
  migrations (`CouplesCash.Api/Migrations/000N_name.sql`, embedded), FluentValidation,
  JWT HS256 + rotating refresh tokens, Scalar at `/scalar` when `Api__EnableDocs=true`.
  v2: Web Push (VAPID) sender, WebAuthn via Fido2NetLib, `SchedulerService` hosted
  service (60 s tick: autopay, reminders, hygiene, v3 bank sync) inside the api process — no
  extra container. v3: SimpleFIN client, AES-256-GCM `DataProtector`, `TransactionMatcher`.
- `apps/matcher` — Python 3.13, FastAPI + RapidFuzz, `uv` (`uv.lock`), pytest + ruff. Stateless, internal-only on
  `:8090` (no `ports:`), one uvicorn worker, 96 MB limit. Fuzzy bill/one-off matching + found-bills detection.
  Contract: `docs/MATCHER-CONTRACT.md`. The API falls back to its built-in C# matcher on any matcher error.
- Postgres 16. Compose/Make/CI/docs at repo root (`compose.yml`, `Makefile`, `infra/`, `.github/`).

## Where things live
| What | Path |
|---|---|
| Endpoints | `apps/api/CouplesCash.Api/Endpoints/*Endpoints.cs` (one file per resource, `MapXxx(this RouteGroupBuilder)`) |
| SQL / repositories | `apps/api/CouplesCash.Api/Data/` |
| Auth, tokens, Argon2, couple codes, **passkeys (Fido2)** | `apps/api/CouplesCash.Api/Auth/` |
| Pure engines (unit-tested, no I/O) | `apps/api/CouplesCash.Api/Domain/` — `RecurrenceEngine`, `PayPeriodEngine`, `GoalProjection`, `SchedulerRules`, `BudgetMath`, **`WeekEngine`** (week bounds, left-at-end, variance, weekly budget rows/history), **`TransactionMatcher`** (+ `BankSyncRules`, `Fingerprint`: suggestions, dedupe fingerprints, payee normalisation) |
| Web Push (VAPID keys, sender, payloads, `--vapid` CLI) | `apps/api/CouplesCash.Api/Push/` |
| **SimpleFIN client** (claim token, fetch accounts; protocol-shaped DTOs) | `apps/api/CouplesCash.Api/SimpleFin/` |
| **Encryption at rest** (AES-256-GCM, `Security__DataKey`) | `apps/api/CouplesCash.Api/Security/DataProtector.cs` |
| **Banks / inbox / CSV endpoints** | `Endpoints/BankEndpoints.cs`, `Endpoints/InboxEndpoints.cs`, `Endpoints/ImportEndpoints.cs` |
| Background job (autopay, reminders, hygiene, **bank sync + auto-match**) | `apps/api/CouplesCash.Api/Scheduler/` (`SchedulerService` hosted service; selection logic lives in `Domain/SchedulerRules.cs`) |
| Migrations | `apps/api/CouplesCash.Api/Migrations/000N_name.sql` (`0002_v2.sql` = v2, `0003_imports.sql` = v3, `0004_week.sql` = v4, `0005_transactions.sql` = v5) |
| API tests | `apps/api/CouplesCash.Api.Tests/` (xunit) |
| Routes (pages + form actions) | `apps/web/src/routes/**/+page.svelte`, `+page.server.ts` |
| API client + types | `apps/web/src/lib/server/api.ts`, `apps/web/src/lib/types.ts` |
| Settings routes (profile, notifications, passkeys, household) | `apps/web/src/routes/(app)/settings/**`, `(app)/settings/household` |
| v2 feature routes | `(app)/activity`, `expenses`, `todos`, `goals`, `payperiod`, `insights/report` |
| **v5** pending reconciliation, transfers, matching | API `Domain/PendingReconciler.cs`, `GET /accounts/{id}/transactions`, `/inbox/{id}/candidates|match|transfer|unmatch`, `/recurring/from-transaction`, `/recurring/{id}/payments`, `/transfers`; web `TransactionSheet.svelte` (the one place a transaction is acted on), account Transactions tab, `(app)/transfers`, bill Payments |
| **v4 routes** (week view) | `(app)/week` (+ `WeekCard` on the dashboard, "Plan a one-off" in the quick-add sheet, weekly budgets on `/week` and Insights) |
| **v3 routes** (linked banks, review inbox, CSV import) | `(app)/settings/banks`, `(app)/inbox`, `(app)/imports/csv`; client-side CSV parsing/mapping in `src/lib/utils/csv.ts` |
| **API ↔ matcher** (client, fallback, rematch queue, found bills) | `apps/api/CouplesCash.Api/Matching/`, `Scheduler/MatchingWorker.cs`, `Services/BillSuggestionService.cs`, `Domain/BankNames.cs` + `MatchingRules.cs`; endpoints `MatchingEndpoints`, `TransactionEndpoints`, `BillSuggestionEndpoints`; migration `0006_matcher.sql` |
| **Matcher service** (scoring, found bills, normalisation) | `apps/matcher/src/matcher/scoring.py`, `recurring.py`, `normalize.py`; tests `apps/matcher/tests/` |
| Service worker (push display + click → `url`) | `apps/web/src/service-worker/index.ts` |
| Components | `apps/web/src/lib/components/` |
| Browser proxy to API | `apps/web/src/routes/api/[...path]/+server.ts` (needs `X-Requested-With: CouplesCash`) |
| e2e | `apps/web/e2e/*.e2e.ts` (run against the in-memory mock API in `e2e/mock-api.ts`; also pass against the real API) |

## Commands
```bash
make setup            # one-time: tools check, .env + secrets, restore deps
make dev              # db in Docker, api (dotnet watch :8080) + web (vite :5173)
make dev-db | dev-api | dev-web
make up | down | logs S=api | ps        # full stack in Docker (prod-like)
make scheduler-logs                      # api logs filtered to the background job
make vapid                               # fill empty VAPID_* in .env (part of make secrets; FORCE=1 rotates)
make secrets                             # fill empty JWT_SECRET / POSTGRES_PASSWORD / DATA_KEY (FORCE=1 rotates — breaks bank links)
make backup                              # DB dump + .env copy (DATA_KEY) → backups/
make test | lint | fmt                   # all languages (incl. matcher)
make dev-matcher | test-matcher | matcher-logs
cd apps/matcher && uv sync && uv run pytest && uv run ruff check . && uv run ruff format --check .
make pi | backup | restore FILE=…        # Raspberry Pi ops
cd apps/web && pnpm dev | check | lint | format | build | exec playwright test
cd apps/api && dotnet build CouplesCash.slnx -warnaserror && dotnet test CouplesCash.slnx
cd apps/api/CouplesCash.Api && dotnet run -- --migrate     # apply migrations and exit
cd apps/api/CouplesCash.Api && dotnet run -- --vapid       # print Push__VapidPublicKey=… / Push__VapidPrivateKey=… and exit 0
```
Native dev env comes from `.env.dev` (generated by `scripts/dev-env.sh` from `.env`).

## Environment (api)
`ConnectionStrings__Default`, `Jwt__Secret` (required) · `Push__VapidPublicKey`,
`Push__VapidPrivateKey`, `Push__Subject` (mailto:; all empty → push endpoints 503
`push_not_configured`) · `WebAuthn__Origin` (= `PUBLIC_URL`; passkeys 503 unless https or
localhost), `WebAuthn__RelyingPartyId` (optional, derived from origin host),
`WebAuthn__RelyingPartyName` (`CouplesCash`) · `Scheduler__Enabled` (default true) ·
`Security__DataKey` (base64 32 bytes = `.env` `DATA_KEY`; empty → bank sync disabled, claim
503 `bank_sync_not_configured`, existing links report decrypt errors) · `Sync__IntervalHours`
(6, = `SYNC_INTERVAL_HOURS`) · `Sync__ManualCooldownMinutes` (15) · `SimpleFin__BaseUrl` (tests only) ·
`Matcher__BaseUrl` (`http://matcher:8090`; dev `http://localhost:8090`), `Matcher__Key` (= `.env` `MATCHER_KEY`, also given
to the matcher container; empty → the matcher refuses to start and the API uses the built-in matcher).
Compose maps `.env` → these in `compose.yml` `x-api-env`; `scripts/dev-env.sh` does the same
for native dev. `--migrate` and `--vapid` must exit before any config validation.

## Conventions (do not bend)
- **Money is integer cents** (`long` in C#, `number` in TS, `*_cents bigint` in SQL). Never floats.
- **Dates are `YYYY-MM-DD` strings** (`DateOnly` in C#); timestamps ISO 8601 UTC (`DateTimeOffset`). IDs UUID v7.
- **Household scoping**: the household id comes from the JWT `hh` claim, never from the body
  or query. Every SQL statement on a household table has `WHERE household_id = @HouseholdId`.
  Foreign or missing ids → **404**, never 403.
- **Migrations are append-only**: add `000N_name.sql`; never edit a shipped script.
- Errors are RFC 9457 problem details; validation errors go in `errors: { field: [msg] }`.
- SvelteKit 3 specifics: there is **no `svelte.config.js`** (Kit config lives in `vite.config.ts`);
  import from `#lib/...` **with file extensions** (`#lib/utils/money.ts`, `#lib/components/X.svelte`),
  not `$lib`; env vars are declared in `src/env.ts` and imported from `$app/env/private|public`;
  the service worker lives in `src/service-worker/index.ts`; use `$app/state`, not `$app/stores`.
  Server-seeded uncontrolled inputs use `defaultValue=`/`defaultChecked=`.
- Svelte: runes (`$state`, `$derived`, `$props`, `$effect`) — no `export let`, no stores for
  component state. **Form actions first**, progressive enhancement with `use:enhance`;
  client fetch only for things that cannot be a form (calendar month nav) via `/api/...`.
- Tailwind + daisyUI classes; theme via `data-theme` from the `theme` cookie; 44 px tap targets.
- Secrets only via environment (`Jwt__Secret`, `ConnectionStrings__Default`, `Push__VapidPrivateKey`).
  Nothing in appsettings. The API serves only the VAPID *public* key, never logs the private one.
- **Scheduler**: every statement is household-scoped like a request; "today"/"hour" are
  evaluated in the household time zone; autopay writes `paid_by NULL, auto true`; reminders
  are idempotent via `reminder_log`; an undone autopay is not re-marked the same day.
  Log under category `*Scheduler*` so `make scheduler-logs` can grep it.
- **Push payloads**: `{ title, body, url (relative), tag, icon }` — names and amounts only.
- **Passkeys**: challenges 5-min TTL, single-use; rpId = host of `WebAuthn__Origin`.
- **Activity**: every mutation writes `activity_events` in the same transaction (system
  actions `actor_id NULL`).
- **Bank credentials** only via `Security/DataProtector.cs` (AES-256-GCM, `Security__DataKey`);
  **never log or return an access URL** or a setup token — logs carry link names and counts.
  Rotating the key = re-link; no re-encryption path. Bridge `errors` are surfaced (link
  `last_error` + `bank.error` activity), never swallowed.
- **Friday plan (v7)**: the week is the **pay week** (`households.payday_weekday` → day before next payday). Only the
  account with `role = 'bills'` counts: available = bank `availableCents` else `balanceCents − pendingOutCents` (never
  sum other checking accounts). Fun-money accounts (`role = 'fun'`, one owner each) never enter To review. Fun money is
  `kind = 'transfer'` items (from/to accounts) auto-paid by matching transfers; the savings sweep is frozen at payday
  00:00 from last week's left-at-end and auto-done by a bills→savings transfer; until ticked it is subtracted from this
  week's left-at-end (`sweepOutCents`, "− Move to savings"). The web has three tabs (This week ·
  Transactions · Bills) + Settings; removed screens redirect to `/week` — don't re-add them without a plan change.
- **Week view**: a *planned item* is any occurrence due in the week (incl. `frequency: once` one-offs);
  `varianceCents = paid.amountCents − amountCents`; left-at-end = spendable (checking+cash) + unpaid income
  − unpaid planned outflow for the rest of the week; weekly budgets are separate `budgets` rows with
  `period = 'weekly'` plus `households.weekly_spend_cents`; variance `+` = over. One-offs match bank
  transactions within ±7 days / max(15 %, $10); `payeeHint` seeds an item payee rule.
- **Pending lifecycle**: a posted transaction with a new id replaces the absent pending row **in place**
  (±1/+7 days, max(25 %, $5), description overlap) keeping its status and confirmation; pending rows absent
  inside the fetch window for > 5 days become `ignored/dropped` and their confirmation is reversed. Spendable
  uses `availableCents ?? balanceCents`.
- **Transfers**: detected pairs are auto-ignored (`ignore_reason='transfer'`, peers linked) when
  `households.auto_ignore_transfers`; keyword one-sided transfers are suggestions (60, never bulk); learned
  `payee_rules.is_transfer` auto-applies. Transfers never count as spending, budgets or income.
- **Inbox**: confirm-as-expense creates the expense with **`deduct = false`** (the synced balance
  already includes it); confirm-as-occurrence marks it paid with the bank amount; auto-match
  writes `paid_by NULL, auto true` and unpaying restores the inbox row. Bulk confirm only takes
  confidence ≥ 70. `credit`/`loan` balances from the Bridge are stored as positive owed.
- **Auto-match**: `households.auto_match_level` off|bills|all (default all); every auto action writes `match_detail.reason`
  and is undoable; a row a person sends back to review is `auto_blocked` and never auto-applied again. Confirmed matches
  learn the normalised description into `bank_names_learned` (max 10, user `bank_names` never evicted).
- **Matcher**: every call except `/health` sends `X-Matcher-Key`; it never logs descriptions; the API treats 401/413/422/5xx/
  timeouts as "use the built-in matcher" and never fails a sync because of it.
- Containers: non-root, read-only FS, listen on 8080 (api) / 3000 (web) / 8090 (matcher, internal). Don't add `ports:` to api/db.
- Commit messages: conventional (`feat(api): …`, `fix(web): …`, `chore(infra): …`).

## Adding an endpoint end-to-end
1. **Migration** `apps/api/CouplesCash.Api/Migrations/000N_<thing>.sql` — table has `household_id` FK + index.
2. **Repository** in `Data/` — Dapper SQL, parameters only, `household_id` filter on every statement.
3. **Endpoint** in `Endpoints/<Thing>Endpoints.cs` — `group.MapPost(...)`, `.RequireAuthorization()`,
   read `HouseholdId` from claims, return `TypedResults`.
4. **Validator** — FluentValidation class for the request record; register it; test it.
5. **Contract** — add the route, body and responses to `docs/API-CONTRACT.md`.
6. **Web type** — update `apps/web/src/lib/types.ts` and a method in `src/lib/server/api.ts`.
7. **Route** — `+page.server.ts` load/action + `+page.svelte`; add an e2e with mocked API.
8. **Tests** — xunit for the engine/validator, Playwright for the flow.

## Definition of done
- `cd apps/api && dotnet build CouplesCash.slnx -warnaserror && dotnet test CouplesCash.slnx` ✅
  (pure engines — recurrence, pay period, goal projection, autopay/reminder selection — have unit tests)
- `cd apps/web && pnpm check && pnpm lint && pnpm build && pnpm exec playwright test` ✅
- `docker compose config -q` ✅ if you touched compose/env; `bash -n` + `shellcheck -S style`
  for `install.sh scripts/*.sh`; `make -n <target>` for Makefile changes.
- Push/passkey UI hidden when the API answers 503 (not configured / insecure context);
  Linked banks shows a "not configured" state on 503 `bank_sync_not_configured`.
- Contract updated if the API surface changed; `CLAUDE.md`/docs updated if commands or paths moved.
- Touching `apps/matcher`: `uv run pytest`, `uv run ruff check .`, `uv run ruff format --check .` ✅
- No secrets, no `console.log`/`Console.WriteLine` debugging left behind, no new `ports:` on api/db.
