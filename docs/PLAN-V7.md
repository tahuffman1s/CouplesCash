# CouplesCash v7 — Our Friday, automated

Tailored to one household: Travis and his wife. Everything that doesn't serve the Friday routine is removed from
the app (the API keeps old endpoints; the web no longer shows them). Contract: API-CONTRACT.md "# v7 additions".

## The routine this replaces (her notes app + calculator)
Every Friday (Travis's payday) she:
1. takes the **US Bank** balance **after pending** transactions,
2. subtracts every bill still to come **before next Friday**,
3. gets what will be **left at the end of the week**,
4. on Thursday — the last day of the pay week — moves that leftover to **Ally High Yield Savings**.
Travis also moves **$20 a week each** to the two **Ally fun-money checking** accounts, and **$150 each** at the start
of the month. Student loans auto-deduct from Ally savings (not from US Bank).

v7 makes that a single screen that does the arithmetic, updates itself as bank transactions arrive, and reads like her note.

## Accounts have roles
| Role | Their account | Counts in the weekly math | Transactions reviewed |
|---|---|---|---|
| **Bills** (exactly one) | US Bank checking | yes, the only one | yes |
| **Savings** | Ally High Yield Savings | no (shows balance + savings bills like student loans) | yes (loans) |
| **Fun money** (one per person) | Ally checking × 2, each with an owner | no | **no** (personal; never in To review) |
| **Other** | anything else | no | yes |
`accounts.role`, `accounts.owner_user_id`. Setup guesses roles from linked-bank names ("savings" → savings; the
checking account with the most bills/paycheck → bills; other checking → fun) and asks once to confirm.

## The week is a pay week
The week runs **payday → the day before the next payday** (Fri → Thu for them). `households.payday_weekday`
(0–6, default 5 = Friday) replaces `week_start` and drives the week view, the calendar grid and summaries.
Paycheck = the household's income item (weekly); detected automatically from deposits (found bills) if missing.

## The Friday plan (the home screen, `/week`)
**The current week reads as a ledger** (v7.1), top to bottom: *Start of the week* (payday; worked back from available now), each day
from payday through today with what went through the bills account (a transaction that paid a bill shows as that bill, once; pending and
added-by-you marked), **today highlighted**, *available now* set apart in a box, then every day left in the week with its bills still to
come (anything due earlier and not through the bank yet first), the green **Projected Savings Amount** (start + what moved − what is to
come) and **Projected Amount in Savings** (savings balance + this week's projected savings until it moves). Other weeks keep the receipt below.
Reads top-to-bottom like her note, every line tappable to expand:
```
US Bank, available now             $2,345.67   ← bank available balance (already after pending)
   3 pending                         −$62.40   ← list; informational when the bank gives "available"
+ Already paid this week             +$15.49   ← only when the bank already paid some of this week's lines (added back)
− Bills before next Friday        −$2,007.99   ← every bill of the week, paid or not; ticks are a to-do list
− Fun money                           −$40.00   ← $20 Travis · $20 <wife>; +$300 on the first one of the month
− One-offs                            −$60.00   ← planned with + ("oil change 60 friday")
─────────────────────────────────────────────
= Projected Savings Amount            $253.17   ← the number she writes down (left on Thursday; green)
→ Move $253.17 to savings today   [Copy][Done]  ← the bottom line, on Thursday; "Moved … ✓" once the bank shows it
```
- **Move to savings** is the bottom line, not a line in the sum: on Thursday what is left moves to savings. Before Thursday it
  reads "On Thursday, move what's left to savings"; on Thursday "Move $X to savings today" (X = Left on Thursday, Copy / Done);
  once the transfer shows up (or she taps Done) "Moved $X to savings ✓" and Left on Thursday drops to what stays.
- **Left on Thursday (the Projected Savings Amount) is the balance after everything is paid**, so a tick never moves it. Every bill
  of the week is in the bills line, paid or not; one the bank already paid is out of `available`, so it is added back as
  "Already paid this week"; one ticked by hand (or by autopay) is still in `available` until the bank shows it, and that bank
  transaction then takes the tick over. It moves only when the bank does, by the difference between paid and planned.
- **Available now** = the bills account's bank `availableCents`; when the bank doesn't report it,
  `balanceCents − pendingOutCents` (so pending is never ignored and never double-counted).
- Bills/one-offs count only when due in the pay week **and** assigned to the bills account or unassigned. Savings
  bills (student loans) show on the savings card, not in the math.
- **Payday checklist** (shown on payday and until done; auto-ticks from bank data):
  ☐ Paycheck arrived (+$X) · ☐ $20 → Travis fun · ☐ $20 → <wife> fun · ☐ (first payday of the month) $150 each.
  (The move to savings is not here: it is Thursday's bottom line, with a **Copy** button for the bank app.) Each item ticks itself when the matching transfer shows up in
  the bank feed (bills → savings / bills → fun account, ±2 days, amount within $1; the sweep within max(5 %, $5)),
  or by a tap ("Done") if the bank is slow.
- Accounts strip at the top: US Bank · Savings · Fun (Travis) · Fun (<wife>) balances; tap → that account's transactions.
- "N to review" line only when something needs a person; otherwise nothing.

## Recurring transfers (new item kind)
`kind = 'transfer'` with `from_account_id` + `to_account_id`: the $20 weekly and $150 monthly fun-money transfers
(seeded by setup), optional anything else. They count as outflow from the bills account in the weekly math, never as
spending, and are marked paid automatically when a transfer pair (or a one-sided outflow from the bills account to an
unlinked account) matches amount ±$1 within ±2 days. `fromAccountId`/`toAccountId` on RecurringItem.

## Savings sweep
`sweeps` table: one row per pay week (household, week_start = payday, planned_cents frozen at 00:00 household time on the
week's last day — Thursday — from that week's Left on Thursday, and kept equal to it during the day until moved; actual_cents,
done_at, done_by NULL = auto, transaction_id). Auto-done when a bills → savings transfer appears Thursday −1 … +3 days.
Negative leftover → the bottom line says "Nothing to move to savings this week (−$42.10)". A "Move $X to savings today" push
goes out on Thursday at the user's hour.

## From her notes to the app (one-time, two minutes)
**Paste your notes**: a textarea in setup (and Bills → "Paste from notes"). Each line is parsed with the existing
quick-add grammar ("Rent 1850 1st", "Netflix 15.49 7th", "Car ins 216 every 6 months", "Water ~60 20th"); a preview
list with a checkbox and correctable chips per line; **Add N bills** → `POST /v1/recurring/bulk`. Lines it can't read
stay visible as "skipped" so nothing is silently lost. The bank names default to the line's name so matching starts right away.

## Pending diagnostics (the "only one pending" report)
Each sync stores per external account how many pending and posted transactions the bank feed contained
(`external_accounts.last_feed jsonb {pending, posted, availableReported: bool, at}`). Settings → Banks shows
"Last sync: US Bank sent 1 pending, 47 posted · available balance reported". If the app shows fewer pending than the
feed sent, that's our bug; if the feed itself sent one, it's the bank/MX feed (and the available balance still makes
the weekly number right).

## Adding a transaction by hand (when the bank hasn't shown it yet)
SimpleFIN refreshes the bank about once a day, and some banks send few pending charges, so a coffee bought this morning
may not be there on Friday. The **+** button opens on **Transaction** (Bill is the second tab): money out / in, amount,
what it was, account (the bills account by default), date (today). It is listed with the account's pending charges as
"Added by you" and comes off "available now" right away (`plan.billsAccount.addedCents`). If the bank already shows
something like it, the sheet asks first ("U.S. Bank already shows STARBUCKS $6.50 on Oct 27" → Add anyway). When the
bank's own copy arrives (same sign, −3 … +7 days, within $1, or max(25 %, $5) for a matching description), it takes
the added row's place, so nothing counts twice. If it paid a bill (Netflix this morning), match it to the bill from its sheet
("Match to a bill or income", or "Link a transaction" on the bill): the bill is ticked and stops counting, since the added row
already took the money off available; the bank's copy later takes that payment over. "Unlink from …" puts the bill back to unpaid.
It can be deleted from its transaction sheet (a bill it paid goes back to unpaid). Caveat: if the bank's
available balance already includes a hold the feed doesn't list, adding it by hand counts it twice — delete it.

## Plaid (fresher bank data)
SimpleFIN refreshed their banks about once a day and once stalled for two days. Plaid (free Trial plan, "Personal use",
10 connections) is offered next to it: *Settings → Banks → Connect a bank*, Reconnect when the bank wants a new sign-in, and
Sync now asks Plaid to check the bank right away. Same pipeline; switching dedupes the overlap. Setup: `docs/BANK-SYNC.md`.

## What's removed from the app (web)
Insights (donut, monthly budgets, month report), weekly budgets, goals, to-pay list, activity feed page, typed manual
expenses, CSV import entry points (kept only under Settings → Banks → "Import a statement"), pay-period settings,
currency/week-start settings, calendar view (Bills shows "Next 4 weeks" instead), the Money tab (folded into the
accounts strip + Settings → Accounts). Old URLs redirect to `/week`. Data stays in the database; nothing is dropped.

## Navigation: three tabs + settings
**This week** (`/week`) · **Transactions** (`/transactions`, badge = to review) · **Bills** (`/bills`) — and a gear
(top right) → **Settings**: You (name, password, passkeys, theme, notifications), Household (partner, payday,
fun-money amounts, auto-match level), Accounts (roles + owners), Banks (linked banks, feed diagnostics, re-check
matches, import a statement).

## Notifications (simplified)
One **payday** push at the user's hour: "Payday · fun money $40 sent ✓", and on Thursday "Move $253.17 to savings today". Optional "bill due
tomorrow" reminders stay. Partner-activity and week-ahead pushes are removed.

## Data model (`0007_friday.sql`)
```
accounts      + role text NOT NULL DEFAULT 'other' CHECK (role IN ('bills','savings','fun','other')),
              + owner_user_id uuid NULL REFERENCES users ON DELETE SET NULL; partial unique (household_id) WHERE role='bills'
households    + payday_weekday smallint NOT NULL DEFAULT 5 CHECK (0..6)
recurring_items + from_account_id uuid NULL, + to_account_id uuid NULL (both REFERENCES accounts ON DELETE SET NULL);
              kind CHECK adds 'transfer'
sweeps        id, household_id, week_start date, planned_cents, actual_cents NULL, done_at NULL, done_by NULL,
              transaction_id NULL, created_at; UNIQUE (household_id, week_start)
external_accounts + last_feed jsonb NULL
```

## Agents
| Agent | Owns |
|---|---|
| API | `apps/api/**`: 0007, roles, payday week, plan endpoint, transfers kind + matching, sweeps, bulk create, feed diagnostics, payday push, smoke |
| Web | `apps/web/**`: three tabs, the Friday plan screen, payday checklist, setup wizard incl. Paste your notes, settings regroup, removals + redirects, mock + e2e |
