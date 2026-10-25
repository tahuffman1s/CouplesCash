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
