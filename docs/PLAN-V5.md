# CouplesCash v5 — Pending lifecycle, transfers, per-account transactions, matching anywhere

Contract: [API-CONTRACT.md](./API-CONTRACT.md) "# v5 additions". v1–v4 rules still apply.

## 1. Pending lifecycle (fixes duplicates and ghost authorizations)
Banks often re-issue a transaction with a **new id** when it posts, sometimes at a different amount
(restaurant tips, fuel holds, hotel deposits). Today that duplicates the row. New rules, in a pure
`Domain/PendingReconciler.cs`, applied per external account on every successful sync:
- **Same id** posts → refresh in place (existing behaviour).
- **New posted id with no fingerprint match** → look for an existing `pending = true` row on the same
  external account that is **absent from this response**, with `posted.date ∈ [pending.posted_on − 1, pending.posted_on + 7]`,
  `|amount|` within max(25 %, $5), and description similarity (normalised token overlap ≥ 0.5, or the same first
  significant token). Best score wins; each pending row is used once. The **pending row is updated in place**
  (keeps its id, status, suggestion, and confirmation): new external id + fingerprint, amount, date, description,
  `pending = false`. If it was confirmed as an expense the expense amount follows; if it was matched to an
  occurrence the paid amount follows (variance updates). Activity `transaction.posted` when the amount changed.
- **Pending row absent** from the response while its date is inside the fetched window and it is older than
  5 days → `status = ignored`, `ignore_reason = 'dropped'` (authorization fell off). Any confirmation is
  reversed (expense deleted / occurrence unpaid), activity `transaction.dropped`.
- Superseded/dropped rows are hidden from the inbox and account lists by default.
- **Balances**: mapped accounts gain `availableCents` (from the Bridge's `available-balance`) and
  `pendingOutCents` (Σ unreviewed + confirmed pending outflows on that account). **Spendable** in the week
  view and pay-period view uses `availableCents ?? balanceCents` for checking/cash.

## 2. Transfers
- **Auto-ignore pairs**: household setting `autoIgnoreTransfers` (default **on**). When the matcher detects a pair
  (both accounts mapped, opposite signs, same |amount|, ≤ 3 days) both rows become `ignored`, `ignore_reason =
  'transfer'`, `transfer_peer_id` set on each, activity "ignored 2 transfers (auto)". Off → they stay suggestions.
- **One-sided transfers** (other side not linked): keyword suggestion (description contains `TRANSFER`, `XFER`,
  `ONLINE TRANSFER`, `TRANSFER TO|FROM`, credit-card inflows `PAYMENT THANK YOU`, `AUTOPAY PAYMENT`, `ONLINE PMT`)
  → Transfer suggestion at confidence 60 (never auto, never bulk). Marking a one-sided row as a transfer learns
  the payee (`payee_rules.is_transfer = true`); next time with auto on it is ignored automatically (confidence 85).
  Zelle/Venmo/Cash App are **not** keyword transfers (they are usually payments to people).
- **Manual**: "Mark as transfer" on any row, optionally choosing the other side from candidates (opposite sign,
  ±5 days, same |amount| within 1 %). "Not a transfer" unpairs both and returns them to pending.
- **Transfer analysis** `/transfers?month=`: flows between accounts ("Checking → Savings · $800 · 2"), net in/out per
  account, pairs list, one-sided list with "Find the other side". Transfers never count as spending, budgets or income.

## 3. Per-account transactions
`/accounts/[id]` gains a **Transactions** tab: every bank row (SimpleFIN or CSV) **and** manual expenses on that
account, grouped by day with day totals, filters **All · Pending · To review · Matched · Transfers**, search,
keyset "Load more". Header: current balance, available balance, pending out, money in/out for the visible month.
Each row shows a status chip: *To review* / *Expense · Groceries* / *Paid Rent · Nov 1* / *Transfer → Savings* /
*Pending*. Tapping a row opens the shared **Transaction sheet**.

## 4. Matching from anywhere
One **Transaction sheet** (used by Inbox, account transactions, the week view and bill payments):
details (description, payee, memo, account, date, pending, source), current state, and actions:
- **Match to a bill or income** — ranked candidates ±30 days (score from amount closeness, date distance and payee
  rule); paid occurrences shown but disabled unless already paid by *this* transaction. Works on rows in any status:
  an expense confirmation is reversed first.
- **Confirm as expense** (category grid), **Mark as transfer** (peer candidates or "other account"),
  **Ignore**, **Unmatch / Restore** (back to To review, reversing whatever was done).
- **Create a bill from this** — name, kind, frequency (default monthly), anchor = transaction date, amount = |amount|,
  `payeeHint` = payee/description; the transaction is matched to the first occurrence immediately so future months
  auto-match.
From the other side, **bill detail** (`/bills/[id]`) shows **Payments**: the last 12 occurrences with the linked
transaction (or manual/autopay), variance, and **Link a transaction** for unpaid ones (ranked candidates from
unmatched rows around the due date).

## Data model (`0005_transactions.sql`, append-only)
```
imported_transactions + ignore_reason text NULL CHECK (ignore_reason IN ('manual','transfer','dropped','superseded')),
                        + transfer_peer_id uuid NULL REFERENCES imported_transactions(id) ON DELETE SET NULL,
                        + last_seen_at timestamptz NULL,
                        + transfer_auto boolean NOT NULL DEFAULT false
                        index (household_id, account_id, posted_on DESC, id DESC); index (transfer_peer_id)
payee_rules           + is_transfer boolean NOT NULL DEFAULT false
households            + auto_ignore_transfers boolean NOT NULL DEFAULT true
```
Existing ignored rows get `ignore_reason = 'manual'`; existing rows confirmed/ignored as transfer suggestions stay as they are.

## Agents
| Agent | Owns | Done when |
|---|---|---|
| API | `apps/api/**` | 0005; PendingReconciler + tests (id change, tip, hold release, dropped, window rules); transfers (auto pair, keywords, learned rule, manual, report); account transactions endpoint; candidates both directions; match/unmatch any status; from-transaction; bill payments; available/pending balances in spendable; smoke with fake-bridge phases covering each case |
| Web | `apps/web/**` | Transaction sheet shared everywhere; account Transactions tab; `/transfers`; bill Payments; household toggle; inbox hides superseded/dropped and labels ignore reasons; mock + e2e for every flow |
