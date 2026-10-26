# CouplesCash · web

Mobile-first SvelteKit 3 / Svelte 5 PWA for the CouplesCash API. The browser only ever talks to
this app; the SvelteKit server (BFF) holds the auth cookies and proxies to the .NET API.

## Scripts

| Command          | What it does                                                                                                      |
| ---------------- | ----------------------------------------------------------------------------------------------------------------- |
| `pnpm dev`       | Vite dev server on http://localhost:5173 (expects the API on 8080)                                                |
| `pnpm build`     | Production build with `adapter-node` into `build/`                                                                |
| `pnpm preview`   | Serve the production build on http://localhost:4173                                                               |
| `pnpm start`     | `node build` – run the built server (needs `ORIGIN`, `API_URL`)                                                   |
| `pnpm check`     | `svelte-check` (TypeScript + Svelte)                                                                              |
| `pnpm lint`      | Prettier check + ESLint                                                                                           |
| `pnpm format`    | Prettier write                                                                                                    |
| `pnpm test:unit` | Vitest unit tests (`src/**/*.spec.ts`: money, dates, pay period, push, codes, CSV, week, transactions, quick add) |
| `pnpm test:e2e`  | Playwright on iPhone 14 + Pixel 7 viewports against a mock API                                                    |
| `pnpm icons`     | Regenerate `static/icons/*.png` from the inline SVG (uses `sharp`)                                                |

## Environment variables

Declared in `src/env.ts` (`defineEnvVars`). Copy `.env.example` to `.env` for local dev.

| Variable          | Default                 | Notes                                                                                                                              |
| ----------------- | ----------------------- | ---------------------------------------------------------------------------------------------------------------------------------- |
| `API_URL`         | `http://localhost:8080` | Base URL of the .NET API as seen from the SvelteKit **server**. `/v1` is appended. In Docker Compose: `http://api:8080`.           |
| `ORIGIN`          | –                       | Public origin of the web app (e.g. `https://cash.example.com`). Needed by `adapter-node` for CSRF/origin checks and absolute URLs. |
| `PUBLIC_APP_NAME` | `CouplesCash`           | Display name; available in the browser via `$app/env/public`.                                                                      |
| `PORT` / `HOST`   | `3000` / `0.0.0.0`      | adapter-node runtime.                                                                                                              |
| `BODY_SIZE_LIMIT` | `1M` (Dockerfile)       | adapter-node request body cap.                                                                                                     |

## v6 — five tabs, one sentence, one tap

PLAN-V6 §4–§5. The app is organised so every tab has one job:

| Tab              | Route           | Job                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              |
| ---------------- | --------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Week** (home)  | `/week`         | "Left this week" hero, Available / Pending / Still to pay / Income to come, "Next payday Fri, Nov 6 · +$3,200.00" (when a pay period is set), the day list (Planned · Paid · Late · Pending; pending bank rows are listed on their day), Found bills, the weekly budget, a "N to review" banner (or "All caught up"), and the first-run checklist (accounts or bank → bills → partner; each step ticks itself, the card goes when all three are done).                                                                                                           |
| **Transactions** | `/transactions` | **To review** (`GET /inbox?status=pending`, one-tap `ReviewRow`: the big button says the suggestion — "It's Rent", "Groceries", "Transfer", "Choose…"; "Something else" opens `TransactionSheet`; swipe right = the big button, swipe left = Ignore, both with an Undo toast; "Do all suggestions"), then **Pending** pinned (every account, account name + badge), then posted rows by day (`GET /transactions`, account chips, debounced search, "auto" mark + `matchDetail.reason`). The tab badge is the to-review count (`src/lib/state/review.svelte.ts`). |
| **Bills**        | `/bills`        | List ⇄ Calendar (`?view=calendar&m=`, `CalendarView.svelte`), chips All · Bills · Subscriptions · Income · One-offs, Found bills.                                                                                                                                                                                                                                                                                                                                                                                                                                |
| **Money**        | `/accounts`     | Net worth, accounts with "N pending · −$X" (→ the account's Pending filter), Pending grouped by account (`GET /pending`, `#pending`), Goals (`#goals`), "Moved between accounts" (→ `/transfers`).                                                                                                                                                                                                                                                                                                                                                               |
| **More**         | `/more`         | Insights (monthly budgets only, "Share this month" → the report), To pay, Activity, Settings.                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |

- **Quick add** (`QuickAddSheet.svelte`, the **+** on Week, Bills and Transactions): one text field parsed
  by `src/lib/utils/quickadd.ts` (pure, ~85 vitest cases) into a live preview with tappable chips (kind,
  amount + varies, how often, date, category, account, autopay). Add posts the corrected preview to
  `?/quickAdd` (`src/lib/server/quickadd.ts`, which can also parse the raw sentence itself) →
  `POST /recurring` with `bankNames = [name]`. "Twice a month" / "every 6 months" have no API frequency
  and are saved as two monthly / two yearly items. "More options" opens `/bills/new` prefilled; "Already
  spent?" opens `/expenses/new` prefilled.
- **Bank names**: `BankNamesInput.svelte` on the bill form (all kinds; one-offs use the same form) — chips
  posted as `bankName`, learned names listed with a "learned" label and forgotten via
  `DELETE /recurring/{id}/bank-names/learned/{index}`; plus an "Amount varies" toggle.
- **Settings** is one page (`/settings#you|#household|#banks`): name, theme, notifications inline
  (`NotificationsCard.svelte`), passkeys link, password; household name / currency / week start / default
  account, pay period, weekly target, partner, auto-match level (Off / Bills only / Everything) and
  auto-ignore transfers; linked banks, CSV import, matcher status (+ `note`) and "Re-check matches now"
  (`POST /inbox/rematch`, 429 → "Checked a moment ago").
- **Vocabulary**: Planned, Paid, Late, To review, Pending, Matched, Left this week. Flows are signed and
  coloured (`signedMoney` / `Amount.svelte`: "−$12.49", "+$3,200.00"); balances are not. Each main screen
  has a one-line dismissible helper (`HelperText.svelte`, remembered per device in localStorage).
- **Redirects** (`+server.ts`): `/` → `/week`, `/inbox` → `/transactions`, `/calendar` →
  `/bills?view=calendar` (keeps `m`), `/payperiod` → `/week`, `/pending` → `/accounts#pending`, `/goals` →
  `/accounts#goals`, `/settings/household` → `/settings#household`, `/settings/notifications` →
  `/settings#you`, `/bills?tab=expenses` → `/transactions`. Login, register, passkey sign-in and couple
  join land on `/week`; the manifest `start_url` is `/week`.
- **Removed**: the dashboard, `PaydayCard`, `WeekCard`, the pay-period page, the standalone calendar and
  inbox pages, the goals list page, the quick-add FAB, `OneOffForm`, `InboxRow`, `SuggestionChip`, the
  separate household / notification settings pages and the Monthly ⇄ Weekly toggle on Insights. The
  sections below describe v1–v5 as built; where they mention those screens, the table above wins.

## Architecture notes

- **BFF**: `src/lib/server/api.ts` adds the bearer token from the `cc_access` cookie, refreshes
  once on 401 with `cc_refresh` (`/v1/auth/refresh`, de-duplicated per request so parallel loads
  never rotate twice), and redirects to `/login` when the refresh fails.
- **Cookies**: `cc_access` (15 min) and `cc_refresh` (30 d) are `httpOnly`, `SameSite=Lax`,
  `Secure` whenever the request is https. `theme` (1 y) is readable by JS.
- **Hooks** (`src/hooks.server.ts`): theme → `data-theme` on `<html>` via `%theme%`, JWT payload
  decode into `locals.user` / `locals.household`, route protection, security headers. CSP is
  generated by SvelteKit (`csp` in `vite.config.ts`) with nonces.
- **Proxy**: `src/routes/api/[...path]/+server.ts` forwards client fetches to the API. It requires
  `X-Requested-With: CouplesCash` (403 otherwise) and strips hop-by-hop headers. Used only for
  calendar month navigation, mark-paid and quick balance updates; everything else is `load` +
  form actions with progressive enhancement.
- **PWA**: `static/manifest.webmanifest`, icons in `static/icons`, `src/service-worker.ts`
  (precache app shell, network-first navigations with `/offline` fallback, never caches `/api/`).
- **Charts**: hand-rolled SVG (`src/lib/charts`): `Donut.svelte`, `Sparkline.svelte`, `KindBars.svelte`,
  `ProgressRing.svelte` (goals), `BudgetBar.svelte` (budget meters), `GoalSpark.svelte` (history +
  dashed target line), `NetBars.svelte` (v5 transfers: diverging out ← | → in bars per account on one
  shared scale, values at the outer edge, legend, hover focus, and a **Table** toggle with every number). Meters use a lighter step of their own hue as the track; over-budget flips
  to the error colour and says so in text.
- **Money & household settings**: `formatMoney(cents, { currency? })` defaults to the household
  currency held in `src/lib/state/currency.svelte.ts`, which `(app)/+layout.svelte` sets from the
  household loaded in `(app)/+layout.server.ts` (`GET /couple`). The calendar grid honours
  `household.weekStart`; the layout also posts the browser's IANA time zone once when the server
  still has `UTC`.
- **v2 screens**: `/settings/household` (name, currency, week start, default account, pay period
  with live "Next payday" preview, time zone), `/activity` (keyset "Load more" via the proxy),
  Bills › **Expenses** chip + `/expenses/new|[id]`, **Budgets** on `/insights` (inline set/clear via
  proxy), `/insights/report?m=` (month card → **Share** renders a PNG with `html-to-image`
  and uses `navigator.share` or a download; **Print** uses a print stylesheet), `/payperiod`
  (+ dashboard "Until payday" card), `/goals`, `/goals/new|[id]` (+ Goals section on Accounts),
  `/todos` (+ dashboard open items), `/settings/notifications`, `/settings/passkeys`, and
  "Sign in with a passkey" on `/login` through the anonymous BFF endpoint
  `src/routes/(auth)/login/passkey/+server.ts` (requires `X-Requested-With: CouplesCash`).
- **Graceful hiding**: the Notifications page shows a "not set up" note when
  `GET /push/vapid-public-key` is 503; Passkeys (settings and login) hide when
  `GET /auth/passkeys/config` says `enabled: false`, when the context is not secure, or when the
  browser lacks WebAuthn. iPhone browsers that are not installed to the Home Screen get an
  "Add to Home Screen first" explainer instead of the push toggle.
- **v3 screens**: `/settings/banks` (SimpleFIN explainer, paste-a-setup-token form action, per-link
  card with inline rename / pause / **Sync now** (proxy `POST /banks/{id}/sync`, 429 → "Synced
  recently, try again in N min" from `Retry-After`) / unlink confirm sheet; per external account a
  mapping select with "Create account…" mini-form prefilled from the bank's name), `/inbox`
  (dashboard card "N transactions to review", status chips via `?status=`, day groups, suggestion
  chip → bottom sheet to pick a category or match a bill/income within ±10 days, Confirm / Ignore /
  Restore optimistic through the proxy, **Confirm all suggested (N)** + "Ignore all pending" bulk,
  keyset "Load more"), `/imports/csv` (3-step: account + file → papaparse in the browser
  (dynamic import), header detection, column auto-mapping incl. debit/credit pairs, sign convention,
  date-format detection with a live sample → `POST /imports/csv` → result card → "Review in
  inbox"). Helpers in `src/lib/utils/csv.ts` are unit-tested. Occurrence rows paid from the inbox
  carry a **From bank** badge (`paid.source === 'bank'`); the activity feed has icons for
  `bank.*`, `import.csv` and `transaction.*` kinds and labels system rows "Bank sync".
- **Graceful hiding (v3)**: `/settings/banks` shows a "not configured (DATA_KEY missing)" note
  when `GET /banks` or the claim answers 503 `bank_sync_not_configured`; the dashboard inbox card
  stays hidden until there is a link, a CSV import or something pending.
- **v5 — one Transaction sheet** (`src/lib/components/TransactionSheet.svelte`) is the only place a
  bank row is acted on: header (signed amount, account, date, Pending badge, Bank/CSV/Manual source,
  payee/memo), the current state ("To review", "Expense in Fun", "Paid Rent · Nov 1", "Transfer →
  Savings (auto)", "Dropped authorization") and actions: **Match to a bill or income** (ranked
  `GET /inbox/{id}/candidates` with a score meter; already-paid occurrences disabled unless paid by
  this row → `POST /inbox/{id}/match`), **Confirm as expense** (`CategoryGrid.svelte`), **Mark as
  transfer** (peer candidates or "Other account (not linked)" → `POST /inbox/{id}/transfer`),
  **Create a bill from this** (name / kind / frequency → `POST /recurring/from-transaction`, then
  "Open Gym"), **Ignore**, **Unmatch / Not a transfer / Restore** (`POST /inbox/{id}/unmatch`). Manual
  expenses only get **Edit expense**. Every action is optimistic with a toast and reports each version
  through `onchange(item)` so lists update in place. Used by `/inbox` (row tap and suggestion chip;
  inline Confirm / Ignore / Transfer stay), the account **Transactions** tab, `/transfers`, bill
  **Payments** and bank-paid rows on `/week`. Pure helpers (state labels, chips, ignore-reason
  labels, score bars, day groups, pair/payment → sheet rows, query strings) live in
  `src/lib/utils/transactions.ts` and are unit-tested.
- **v5 screens**: `/accounts/[id]?tab=transactions` (balance + "Available $X · $Y pending", month nav,
  money in/out, filter chips All · Pending · To review · Matched · Transfers, 300 ms debounced search,
  day groups with the day's net, keyset **Load more** — first page server-side, the rest via the
  proxy); the Accounts list shows "$Y pending" per account; `/inbox` Ignored filter loads
  `includeHidden=1` and labels the reason (Transfer · auto, Dropped authorization, Ignored), transfer
  rows show "→ Savings"; `/transfers?month=` (More menu + Insights link: total moved, flow cards
  "Everyday → Savings · $500.00", net by account, pairs by date, one-sided list with **Find the other
  side** / **Not a transfer**, empty state); bill detail **Payments** (`GET /recurring/{id}/payments`:
  variance pills, source, **Link a transaction** for unpaid dates via
  `…/occurrences/{date}/candidates` → `…/link`); Settings → Household **Automatically ignore transfers**
  toggle (`autoIgnoreTransfers`); `/week` labels spendable "Available now" when a checking account
  reports `availableCents`.
- **Service worker**: also handles `push` (`{ title, body, url, tag, icon }` → notification) and
  `notificationclick` (focus an open window and navigate to `url`, else open one).

## Testing

`pnpm test:e2e` starts two servers from `playwright.config.ts`: `e2e/mock-api.ts` (an in-memory
implementation of the API contract on port 8099) and `pnpm build && pnpm preview` with
`API_URL=http://127.0.0.1:8099`. Chromium is used for both device projects. The mock covers v1 and
v2 (activity recording, expenses with deduction, budgets, pay-period math shared with the app via
`src/lib/utils/payperiod.ts`, todos, goals + projection, month report, autopay flags). Test-only
helpers: `POST /__reset`, `POST /__mock/autopay?date=YYYY-MM-DD` (runs the autopay scheduler so
the Undo UI can be exercised) and `POST /__mock/bank-sync` (bank-sync scheduler phase for every
link). Push is reported as not configured unless the mock runs with `MOCK_PUSH=1`; passkeys are
always disabled in the mock. **v3**: the mock embeds a fake SimpleFIN Bridge — the setup token
`demo-token` links "Demo Bank" with a checking account (RENT PAYMENT on the 1st, KROGER, ACME
PAYROLL, a pending COFFEE) and a credit card (NETFLIX.COM); any other token is a 400 on
`setupToken`, and `demo-not-configured` answers 503 `bank_sync_not_configured` so one e2e covers
that path without a second server (`MOCK_BANKS_DISABLED=1` does it for every claim). It runs a
simplified matcher (recurring ±5 days / 2 %, learned payee rules, keyword table, transfer
detection), dedupes by fingerprint, maps balances into accounts, and the manual sync has the 15-min
cooldown (`?force` skips it). **v5**: the token `demo-v5` links checking + savings with a pending
THE DINER −40.00, a 6-day-old SHELL OIL −1.00 hold, an ONLINE TRANSFER TO SAV −500 / +500 pair
(auto-ignored while `autoIgnoreTransfers` is on), a one-sided TRANSFER TO BROKERAGE −1000 (keyword
suggestion 60; marking it learns the payee) and PLANET FITNESS. `POST /__mock/bank-sync?phase=…&email=…`
edits that Bridge and syncs only that user's household: `post` (the diner posts at −48.00 under a new
id and replaces the pending row in place, keeping its confirmation), `drop` (the hold disappears →
`ignored / dropped`, hidden), `pair` (a second −250 / +250 pair) and `brokerage` (another brokerage
transfer, auto-ignored by the learned rule). The mock implements every v5 route
(`/accounts/{id}/transactions`, candidates / match / transfer / unmatch, `from-transaction`,
payments, occurrence candidates / link, `/transfers`) plus `availableCents` / `pendingOutCents`.
`e2e/transactions.e2e.ts` covers those flows and skips itself against a real API. **v6**: every
v6 route and field — `/transactions`, `/pending` (unmapped bank accounts group with `accountId: null`),
`/bill-suggestions` + accept / dismiss (409 once handled), `/inbox/rematch` (1/min), `/matcher/status`,
`bankNames` / `bankNamesLearned` (upper-cased) / `amountVaries`, `autoMatchLevel`, `matchScore` /
`matchDetail`, week `pendingOutCents`. Its matcher scores names by significant-token overlap, so a bank
name "Oakwood" auto-matches "ACH DEBIT OAKWOOD PROPERTY MGMT RENT"; it re-scores To review whenever a bill is
created or edited, never auto-applies a row a person sent back (`auto_blocked`), and learns the bank's name
on every confirmed match. Extra phases: `?phase=netflix` (three monthly NETFLIX.COM and SPOTIFY USA charges
→ two found bills) and `?phase=oakwood` (the landlord's ACH line). The v6 specs (`v6-home`, `quickadd`,
`review`, `money`, `bank-names`, `found-bills`, `settings-v6`) wait on `html[data-ready]` (set by the (app)
layout on mount) instead of sleeping. If a Chromium is
preinstalled under `PLAYWRIGHT_BROWSERS_PATH` (as in CI containers) the config uses it even when
its revision differs from the one Playwright expects; set `CHROMIUM_PATH` to point at a specific
binary, or run `pnpm exec playwright install chromium` locally.

## Docker

```sh
docker build -t couplescash-web .
docker run --rm -p 3000:3000 -e ORIGIN=http://localhost:3000 -e API_URL=http://host.docker.internal:8080 couplescash-web
```
