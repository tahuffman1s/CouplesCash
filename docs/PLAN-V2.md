# CouplesCash v2 — "check it every day"

v1 is a dashboard. v2 turns it into something both of you open daily: reminders arrive on the
phone, every change shows who did it, one-off spending and budgets are tracked, autopay bills
mark themselves, a shareable month-close replaces the screenshot, and logging in is a passkey.

Contract additions are in [API-CONTRACT.md](./API-CONTRACT.md) §v2. The v1 rules still hold
(cents, `YYYY-MM-DD`, household from the `hh` claim, 404 for foreign ids, 400 field errors).

| # | Feature | Summary |
|---|---|---|
| 1 | **Push reminders** | Web Push (VAPID). "Electric is due tomorrow · $142.50" on both phones N days before, at a chosen local hour. Also optional "partner activity" pushes. iOS 16.4+ PWA, Android Chrome. Requires HTTPS (tunnel mode). |
| 2 | **Activity feed** | Every mutation writes an `activity_events` row in the same transaction: "Kaylee marked Rent paid · 2h ago". Dashboard shows the latest 5; `/activity` is the full feed, infinite scroll. |
| 3 | **Expenses** (one-off) | Amount, name, category, optional account (deducts balance + snapshot), date, note. Feeds budgets, insights donut, "remaining this month" and the pay-period view. |
| 4 | **Budgets** | Monthly amount per category. Spent = monthly-equivalent recurring outflow in the category + expenses this month. Progress bars on `/insights`, over-budget flagged. |
| 6 | **Autopay auto-mark** | A background job marks `autoPay` occurrences paid on their due date (`paid.auto = true`, `paidBy = null`) in the household's time zone. Toast with **Undo** = the existing unpaid endpoint. |
| 7 | **Month-close summary** | `/insights/report?m=YYYY-MM`: one scrollable card (net worth change, paid vs due, by category vs budget, biggest expenses, autopay list). **Share** button renders it to a PNG (html-to-image) → Web Share API / download; print stylesheet gives a PDF. |
| 8 | **Pay-period view** | Household chooses `weekly`, `biweekly`, `bimonthly` (twice a month) or `monthly` plus an anchor payday. Dashboard card "Until payday (Fri Nov 6)": expected income, due, paid, remaining, spendable balance (checking + cash − unpaid). `/payperiod` lists the occurrences. |
| 9 | **Household settings** | Currency (ISO 4217, drives all money formatting), week start (Sunday/Monday, drives the calendar grid), default account for new bills/expenses, time zone (auto-set from the browser), pay period. Re-inviting a partner after a leave already works via a new Couple Code; the Couple page says so explicitly. |
| 10 | **Savings goals** | Name, target, optional target date, colour/icon. Linked to a savings account (current = its balance) or manual (contribute button). Projection: average monthly change over the last 90 days of snapshots → projected completion date; "needed per month" to hit the target date. Sparkline with target line. |
| 12 | **Passkeys** | WebAuthn via Fido2NetLib: register one or more passkeys from Settings (named, e.g. "Travis's iPhone"); sign in with a passkey (discoverable credential, no email needed) on `/login`. Password stays as a fallback. Secure-context only; hidden in LAN/http mode. |
| 13 | **To-pay checklist** | Shared checklist for things that aren't bills: "Transfer $200 to savings", "Call insurance". Title, optional amount, optional due date, check off (records who/when), reorder. Dashboard shows open items. |

Skipped on purpose: 5 (notes import), 11 (export/delete) — v3 candidates.

## Data model additions (`0002_v2.sql`, append-only)

```
households           + currency text NOT NULL DEFAULT 'USD', week_start smallint NOT NULL DEFAULT 0 (0=Sun,1=Mon),
                       timezone text NOT NULL DEFAULT 'UTC', default_account_id uuid NULL REFERENCES accounts ON DELETE SET NULL,
                       pay_period text NOT NULL DEFAULT 'monthly' CHECK (weekly|biweekly|bimonthly|monthly), pay_anchor_date date NULL
occurrence_payments  + auto boolean NOT NULL DEFAULT false; paid_by becomes NULLABLE (auto payments have no actor)
activity_events      id, household_id, actor_id NULL (null = system/autopay), kind text, entity_type text, entity_id uuid NULL,
                       summary text, data jsonb NOT NULL DEFAULT '{}', created_at; index (household_id, created_at DESC)
expenses             id, household_id, name, amount_cents, category_id NULL, account_id NULL, spent_on date, note NULL,
                       deducted boolean (whether the account balance was adjusted), created_by, created_at, updated_at
budgets              household_id, category_id, amount_cents, updated_at; PK (household_id, category_id)
push_subscriptions   id, user_id, endpoint text UNIQUE, p256dh text, auth text, user_agent text, created_at, last_used_at NULL, failures int DEFAULT 0
notification_prefs   user_id PK, reminders_enabled bool DEFAULT true, days_before smallint[] DEFAULT '{1}', hour_local smallint DEFAULT 9,
                       activity_enabled bool DEFAULT true, updated_at
reminder_log         recurring_item_id, due_date, user_id, sent_at; PK (recurring_item_id, due_date, user_id)
todos                id, household_id, title, amount_cents NULL, due_date NULL, sort_order int, done_at NULL, done_by NULL, created_by, created_at, updated_at
savings_goals        id, household_id, name, target_cents, target_date NULL, account_id NULL (ON DELETE SET NULL), saved_cents bigint DEFAULT 0 (manual goals),
                       color, icon, created_by, created_at, updated_at, archived_at NULL
goal_contributions   id, goal_id, amount_cents, contributed_by, created_at   (manual goals only; account goals read account history)
webauthn_credentials id, user_id, credential_id bytea UNIQUE, public_key bytea, sign_count bigint, aaguid uuid NULL, transports text[], name text,
                       backed_up bool, created_at, last_used_at NULL
webauthn_challenges  id, challenge bytea, user_id NULL, kind text (register|login), options jsonb, expires_at, created_at
```

## Background job (`SchedulerService`, hosted service, ticks every 60 s)

1. **Autopay**: for each household, "today" in its time zone; for each active `autoPay` item with an occurrence due today and no payment row → insert payment (`auto = true`, `paid_by NULL`, amount = item amount), write activity event, push "Rent auto-paid · $1,850" to members with `activity_enabled`.
2. **Reminders**: for each user with `reminders_enabled` whose local hour == `hour_local` (checked once per hour via `reminder_log`): for each `d` in `days_before`, unpaid occurrences due on `today + d` → one push per occurrence ("Electric is due tomorrow · $142.50", deep link `/calendar?m=…&d=…`), log it.
3. **Hygiene**: delete expired `webauthn_challenges`; drop push subscriptions with ≥ 5 consecutive failures (410/404 from the push service count as immediate removal).

## Web information architecture

Dock stays 5 items: **Home · Accounts · Bills · Calendar · More**.
- Home: pay-period card (top), to-pay open items, due in 14 days, recent activity (5) → `/activity`, quick-add FAB → bottom sheet with **Expense / Bill / To-pay**.
- Accounts: accounts + **Goals** section (cards with progress ring) → `/goals`, `/goals/new`, `/goals/[id]`.
- Bills: chips **Bills · Subscriptions · Income · Expenses**; expenses list grouped by day with month nav → `/expenses/new`, `/expenses/[id]`.
- Calendar: unchanged + expenses shown as small grey dots; autopay badge on items; Undo toast after auto-paid items when viewed the same day.
- Insights: donut now includes expenses; **Budgets** section with progress bars + "Set budget" inline; **Month report** link → `/insights/report`.
- More: Activity, To-pay (`/todos`), Pay period (`/payperiod`), Couple, Household settings (`/settings/household`), Settings (profile, theme, **Notifications**, **Passkeys**, password, install).
- Login: "Sign in with a passkey" button above the password form when `window.PublicKeyCredential` exists and the context is secure.

## Config additions

| Env | Who | Purpose |
|---|---|---|
| `Push__VapidPublicKey`, `Push__VapidPrivateKey`, `Push__Subject` (`mailto:you@example.com`) | api | Web Push signing. Generate with `dotnet CouplesCash.Api.dll --vapid` (prints the two `KEY=value` lines). Compose maps `VAPID_PUBLIC_KEY`/`VAPID_PRIVATE_KEY`/`VAPID_SUBJECT` from `.env`; `make secrets` and `install.sh` generate them via `docker compose run --rm --no-deps api dotnet CouplesCash.Api.dll --vapid` (or `dotnet run -- --vapid` natively). Push is disabled (endpoints return 503 `push_not_configured`) when unset. |
| `WebAuthn__Origin` (= `PUBLIC_URL`), `WebAuthn__RelyingPartyId` (host of PUBLIC_URL, derived if unset), `WebAuthn__RelyingPartyName` (`CouplesCash`) | api | Passkeys. Disabled (503) when origin is not https. |
| `PUBLIC_PUSH_ENABLED` | web | not needed — the web asks `GET /push/vapid-public-key` and hides the UI on 503. |

## Agents

| Agent | Owns | Done when |
|---|---|---|
| API | `apps/api/**` | migration 0002 applies on top of 0001; all §v2 endpoints; scheduler with unit-tested pure functions (pay-period math, goal projection, autopay/reminder selection); `--vapid`; tests green; smoke script extended |
| Web | `apps/web/**` | all screens above; mock API extended; e2e for expenses, budgets, todos, goals, pay period, activity, autopay undo, report render, settings; passkey/push UI gracefully hidden in insecure contexts; check/lint/build/e2e green |
| Infra | root files + docs | compose/env/Makefile/install.sh carry VAPID + WebAuthn; docs for notifications (iOS install requirement), passkeys, pay period; CLAUDE.md updated |
