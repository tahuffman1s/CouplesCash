# CouplesCash v4 — Week view, planned one-offs, weekly budgets

"How much do we have left at the end of this week, after the bills and the stuff we planned,
and are we under or over what we said we'd spend?" One screen answers it, fed by the SimpleFIN
inbox so planned items reconcile themselves when the real transaction clears.

Contract: [API-CONTRACT.md](./API-CONTRACT.md) "# v4 additions". v1–v3 rules still apply.

## Concepts
- **Week** = `weekStart` … `weekStart + 6` using the household's `week_start` (0 Sun / 1 Mon). The
  **current week** contains today in the household time zone.
- **Planned item** = any occurrence (bill, subscription, income, or a **one-off** = `frequency: once`)
  whose due date falls in the week. A one-off is created from "Plan a one-off" and may carry a
  `payeeHint` so the first bank transaction that matches reconciles automatically.
- **Cleared** = the occurrence is paid (manually, autopay, or matched from the bank). The paid amount is
  the *actual*; `amountCents` on the occurrence stays the *planned*. `varianceCents = actual − planned`
  (positive = cost more).
- **Overdue** = unpaid and `dueDate < today`.
- **Unplanned spending** = expenses in the week (typed or confirmed from the inbox). These never came
  from a planned occurrence.
- **Spendable now** = sum of `checking` + `cash` account balances (what SimpleFIN says, or what was typed).
- **Left at end of week** (current week) = spendable now + unpaid income in the rest of the week − unpaid
  planned outflow in the rest of the week. Past weeks: `startingBalance` = latest balance snapshots at or
  before the week start (null if none) and the view shows actuals only. Future weeks: projected =
  spendable now + all unpaid income − all unpaid planned outflow between today and that week's end.
- **Weekly budgets**: per-category weekly amounts **and** one overall weekly spending target
  (`households.weekly_spend_cents`). *Spent* in a week for a category = expenses in the week + paid
  bill/subscription occurrences due in the week (actual amounts) in that category. Overall spent = all of
  the above regardless of category. `varianceCents = spent − budget` (positive = over). Monthly budgets
  from v2 stay as they are; the two periods are independent rows.

## Data model (`0004_week.sql`, append-only)
```
budgets              + period text NOT NULL DEFAULT 'monthly' CHECK (period IN ('monthly','weekly'));
                       PK becomes (household_id, category_id, period)  [DROP CONSTRAINT budgets_pkey; ADD PRIMARY KEY (...)]
households           + weekly_spend_cents bigint NULL CHECK (>= 0)
recurring_items      + payee_hint text NULL    -- seeds payee_rules (item rule) on create/update so the first match auto-applies
notification_prefs   + week_summary_enabled boolean NOT NULL DEFAULT true
reminder_log         (reuse) — week summaries log with recurring_item_id = '00000000-0000-0000-0000-000000000000' sentinel?  NO:
week_summary_log     user_id, week_start, kind ('ahead'|'left'), sent_at; PK (user_id, week_start, kind)
```

## Reconciliation tweaks (`Domain/TransactionMatcher.cs`)
- One-off items (`frequency = once`) match with a wider window: ±7 days and amount within max(15 %, $10).
  Recurring tolerance stays max(2 %, $1).
- `payeeHint` on an item → on create/update upsert `payee_rules(payee_norm(hint), item_id = item, hits = 1)`;
  the matcher already treats a learned item rule as "auto-apply at ≥ 90". Clearing the hint deletes that rule.
- When a one-off reconciles, the occurrence is marked paid with the actual amount (existing behaviour); the
  week view surfaces the variance.

## Scheduler additions
Phase 5 **week summaries** (per user with `week_summary_enabled`, at `hour_local`, in the household tz, once per
(user, week_start, kind) via `week_summary_log`):
- **ahead**: on the day before `week_start` → "Week ahead: $X planned, $Y expected left" → `/week`.
- **left**: on the 5th day of the week (Friday for Monday-start, Thursday for Sunday-start) → "You have $Z left
  this week · budget $A under/over" → `/week`.

## Web
- **`/week`** (route + "This week" dashboard card above the payday card, hidden only if no accounts exist):
  header with week range + prev/next, hero **Left at end of week** (projected label for future weeks, "actual"
  for past), four mini stats (Spendable now · Income to come · Planned to pay · Unplanned so far); then a
  **day list** Mon…Sun (or Sun…Sat): each planned item as an OccurrenceRow with status chip Planned / Cleared
  (actual, and "+$4.20" variance when ≠ planned) / Overdue, inline **mark paid**, **Undo**; unplanned expenses
  listed under the day in a quieter style; income rows in success colour. Then the **Weekly budget** section:
  overall target bar (spent / target, "under by $X" or "over by $X" in error colour), per-category bars for
  categories with a weekly budget, "Set weekly budgets" → inline edit sheet (per-category amounts + overall
  target), and a small 8-week history of over/under as bars (dataviz skill). Empty state when no budgets.
- **Plan a one-off**: quick-add sheet option on the dashboard and a "+" on the week view: name, amount,
  date (defaults to today, must be ≥ today − 7 days), kind (expense / income), account (default household
  account), optional "Bank shows it as" (payeeHint), notes. Posts `POST /recurring` with `frequency: 'once'`.
  Bills page lists one-offs under a **One-offs** chip.
- **Settings → Household** gains "Weekly spending target"; **Settings → Notifications** gains "Weekly summary"
  toggle. Insights budgets section gets a Monthly / Weekly segmented control (weekly shows the current week's
  rows via `/budgets/week`).

## Agents
| Agent | Owns | Done when |
|---|---|---|
| API | `apps/api/**` | 0004 migration; `Domain/WeekEngine.cs` (pure: week bounds, left-at-end math, variance, budget rows, history) with tests; `/week`, `/budgets/week`, budgets `period`, `weeklySpendCents`, `payeeHint`, matcher tolerance, scheduler phase 5; smoke extended (plan one-off → fake bank clears it at a different amount → week view shows cleared + variance + left-at-end; weekly budget under/over) |
| Web | `apps/web/**` | screens above, mock extended, e2e: week view math with seeded data, one-off quick-add → appears on week + calendar, reconcile via mock bank sync → Cleared with variance, weekly budgets set → under/over, history bars render, dashboard card, settings target |
