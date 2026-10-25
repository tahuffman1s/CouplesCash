# CouplesCash v6 — Week is home, fuzzy auto-reconcile, pending per account

Contract: [API-CONTRACT.md](./API-CONTRACT.md) "# v6 additions" (API ↔ web) and [MATCHER-CONTRACT.md](./MATCHER-CONTRACT.md)
(API ↔ matcher service). v1–v5 rules still apply.

## 1. Week is the home page
- `/` redirects to `/week`; login, register, passkey login and couple join land on `/week`; the PWA manifest
  `start_url` is `/week`. The v1 dashboard moves to `/overview` (link in the week header and the More menu).
- Dock: **Week** · Accounts · Bills · Calendar · More. The "This week" card is removed from Overview (it *is* the page).

## 2. Fuzzy auto-reconcile with a Python matcher service
A small stateless **Python service** (`apps/matcher`, FastAPI + RapidFuzz) scores bank transactions against
bills, income and one-offs using **amount + name + date**, and against learned payee rules for categories. It runs in
Docker next to the API (internal network only, no published port, shared-secret header), and the API calls it
automatically: on every bank sync, every CSV import, whenever a bill/one-off is created or edited, and on a
scheduler sweep every 30 minutes over all To-review rows. If the matcher is down, the API falls back to its built-in
C# matcher and logs a warning; nothing breaks.

**Bank names** — every bill, income and one-off has a list of **bank names** ("Bank shows it as"), editable as
chips in the bill and one-off forms (not just one-offs). `payee_hint` from v4 migrates into the list. When a match is
confirmed (by the user or automatically), the transaction's normalised description is **learned** into that item's
list (dedupe, max 10, user-added names never evicted). `amountVaries` per item (utilities, credit-card payments)
widens the amount tolerance.

**Auto-match level** (household setting, default **all**): `off` = suggestions only; `bills` = auto-apply
bill/income/one-off matches and transfers; `all` = also auto-confirm expenses whose payee strongly matches a learned
category rule (fuzzy ≥ 90, rule hits ≥ 2). Every automatic action is visible ("Auto-matched · name 94 % · amount exact ·
1 day early"), recorded in the activity feed and reversible with Undo/Unmatch.

## 3. Pending per bank account
- `/pending` page: every pending transaction grouped by bank account, account subtotal, overall total, and the
  balance → available difference per account. Tap → Transaction sheet.
- Accounts list: linked accounts show "N pending · $X" linking to the account's Transactions tab with the Pending filter.
- Week view: a "Pending $X" stat beside Spendable; pending outflows that matched a planned item show "Pending" on that row.
- Pending transactions are matched like posted ones; when they post (v5 lifecycle) the match carries over.

## Data model (`0006_matcher.sql`)
```
recurring_items + bank_names text[] NOT NULL DEFAULT '{}'   (backfill from payee_hint), + bank_names_learned text[] NOT NULL DEFAULT '{}',
                + amount_varies boolean NOT NULL DEFAULT false
households      + auto_match_level text NOT NULL DEFAULT 'all' CHECK (auto_match_level IN ('off','bills','all'))
imported_transactions + match_score smallint NULL, + match_detail jsonb NULL   -- {name, amount, date, reason, engine: 'fuzzy'|'builtin'}
```
`payee_hint` stays readable for compatibility (= first bank name) but is no longer written.

## Agents
| Agent | Owns | Starts |
|---|---|---|
| Matcher + infra | `apps/matcher/**`, `compose*.yml`, `.env.example`, `Makefile`, `install.sh`, `scripts/*`, `.github/**`, `.vscode/**`, docs (not the two contracts / plans) | now |
| API | `apps/api/**` | after v5 lands |
| Web | `apps/web/**` | after v5 lands |

## 4. Radically simple (applies to everything above)
**Principle:** the common path is one tap or one short sentence; anything else is one "Something else" away.

### Adding a bill = one button + a few words
- A single **+** in the app bar of every main page opens a sheet with **one text field**. Typing shows a live preview
  card parsed by `src/lib/utils/quickadd.ts` (pure, tested); **Add** saves it. Examples:
  `netflix 15.49 on the 7th` → subscription, monthly, day 7 · `rent 1850 1st autopay` → bill, monthly, day 1, autopay ·
  `oil change 60 friday` → one-off this Friday · `paycheck 3200 every other friday` → income, biweekly ·
  `car insurance 216 every 6 months` · `gym 45 weekly` · `water bill ~60 on the 20th` (`~` = amount varies).
- Grammar: amount (`$`, commas, `~`/`about` = varies), frequency words (weekly, every week, biweekly, every other week,
  every 2 weeks, twice a month, monthly, every month, quarterly, every 3/6 months, yearly, annually, once, one-off,
  one time), dates (today, tomorrow, weekday names, next <weekday>, the 7th / on the 7th / 7th, Nov 3, 11/3),
  kind words (paycheck, salary, pay, income, refund → income; sub, subscription, streaming names → subscription),
  `autopay`. The rest is the name. Defaults: a day-of-month without a frequency → monthly; a weekday/date without a
  frequency → one-off; no date → monthly from today; category guessed from a keyword table; the name is also the
  first bank name (fuzzy matching covers the rest).
- Each preview chip is tappable to correct it (kind, amount, frequency, date, category, account); the full form is
  still there behind "More options" but nobody has to see it.
- **Found bills**: the matcher also detects recurring payments in imported transactions (`POST /v1/detect-recurring`
  in MATCHER-CONTRACT). The API runs it after each sync and daily, stores `bill_suggestions`, and the week and bills
  pages show a card "We found 3 regular payments" → one row each: "Netflix · $15.49 every month · next Nov 7" with
  **Add** / **Not a bill**. Adding one creates the item with the detected bank name and immediately matches the past
  transactions it was found from.

### Matching = mostly automatic, one tap otherwise
- Default auto level `all` (above). The inbox is renamed **"To review"** and only lists rows that need a human.
- Each row has one big primary button that says the suggestion in words — **"It's Rent"**, **"Groceries"**,
  **"Transfer"** — and a secondary **"Something else"** that opens the Transaction sheet. No suggestion → primary is
  "Choose…". Swipe right = primary, swipe left = Ignore (with Undo toast). "Do all suggestions" in the header.
- When the To-review list is empty the week page says "All caught up" and the tab badge disappears.

### Viewing transactions = everything, including in-progress
- New **Transactions** tab (`/transactions`, all accounts): a **Pending** section pinned on top (every in-progress
  transaction across accounts with account name and "Pending" badge), then posted transactions by day; account filter
  chips, search, and the same row chips (To review / Rent / Groceries / Transfer). Rows that auto-matched show a small
  "auto" mark; tap → Transaction sheet.
- The per-account Transactions tab and the week day list also include pending rows (marked), never hide them.
- `/pending` from §3 becomes the Pending section's "See by account" link.

### Navigation (5 tabs, the order people use them)
**Week** · **Transactions** (badge = to review) · **Bills** (list ↔ calendar toggle; calendar lives here now) ·
**Accounts** · **More**. Overview, Insights, Goals, To-pay, Activity, Transfers, Settings live under More.

### Data model additions (`0006_matcher.sql`)
```
bill_suggestions  id, household_id, name, kind, amount_cents, amount_varies, frequency, next_due, bank_name,
                  sample_transaction_ids uuid[], score smallint, status ('open'|'accepted'|'dismissed'),
                  fingerprint bytea UNIQUE per household (normalised name + frequency), created_at, updated_at
```
Dismissed suggestions never come back for the same fingerprint.
