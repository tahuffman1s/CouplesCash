# CouplesCash v3 — Bank sync, CSV import, Review inbox

Balances and transactions stop being typed in. A **SimpleFIN Bridge** link (read-only, $1.50/mo,
built for self-hosters) pulls them every few hours; a **CSV import** covers banks SimpleFIN can't
reach or one-off statements. Both feed one **Review inbox** where each transaction is confirmed
with a suggested category, matched to a recurring bill (which marks it paid), or ignored. The human
stays in the loop; nothing is categorised silently.

Contract: [API-CONTRACT.md](./API-CONTRACT.md) "# v3 additions". v1/v2 rules still apply.

## How SimpleFIN works (the API agent must verify against https://www.simplefin.org/protocol.html)
1. The user creates a **setup token** at the Bridge (`https://beta-bridge.simplefin.org/simplefin/create`) after connecting banks there. The token is base64 of a one-time **claim URL**.
2. The app decodes it and `POST`s to the claim URL with an empty body → response body is the **access URL** (`https://<user>:<pass>@…/simplefin`). It is a credential: store **encrypted**, never log it, never return it to clients.
3. `GET {accessUrl}/accounts?start-date=<unix>&end-date=<unix>&pending=1` → `{ errors: string[], accounts: [{ id, name, currency, balance: "123.45", "available-balance", "balance-date": unix, org: { name, domain, url }, transactions: [{ id, posted: unix, amount: "-12.34", description, payee?, memo?, pending?: bool }] }] }`. Amounts are decimal strings; negative = money out. `errors` carries human-readable connection problems (show them in the UI).
4. Rate: the Bridge caches; polling more than every few hours gains nothing. Default sync interval 6 h; manual "Sync now" allowed once per 15 min per link.

## Data model (`0003_imports.sql`, append-only)
```
bank_links              id, household_id, provider ('simplefin'), name, access_url_enc bytea, created_by, created_at,
                          last_synced_at NULL, last_error NULL, sync_enabled bool DEFAULT true
external_accounts       id, bank_link_id FK, household_id, external_id, org_name, name, currency, balance_cents, available_cents NULL,
                          balance_at, account_id NULL FK accounts ON DELETE SET NULL (the mapping), created_at, updated_at;
                          UNIQUE (bank_link_id, external_id)
imported_transactions   id, household_id, source ('simplefin'|'csv'), external_account_id NULL, account_id NULL, external_id NULL,
                          fingerprint bytea, posted_on date, amount_cents (signed, negative = outflow), description, payee NULL, memo NULL,
                          pending bool, status ('pending'|'confirmed'|'ignored'), suggested_category_id NULL, suggested_item_id NULL,
                          suggested_due_date NULL, confidence smallint, confirmed_expense_id NULL, confirmed_item_id NULL, confirmed_due_date NULL,
                          reviewed_by NULL, reviewed_at NULL, created_at; UNIQUE (household_id, fingerprint)
payee_rules             household_id, payee_norm, category_id NULL, item_id NULL, hits int, updated_at; PK (household_id, payee_norm)
csv_imports             id, household_id, account_id, filename, row_count, imported_count, duplicate_count, created_by, created_at
households              + data: nothing new
```
`fingerprint = sha256(source || external_id)` when an external id exists, else `sha256(account_id || posted_on || amount_cents || normalise(description))`.
`payee_norm` = lower-cased description with digits, punctuation and common noise removed (`POS`, `DEBIT`, `CARD 1234`, store numbers, dates).

## Suggestion engine (`Domain/TransactionMatcher.cs`, pure, unit-tested)
For each new pending transaction, in order:
1. **Recurring match**: an unpaid occurrence of an active item within ±5 days of `posted_on` whose amount is within max(2 %, $1) of |amount| and whose kind matches the sign (bills/subscriptions ↔ outflow, income ↔ inflow) → `suggested_item_id`, `suggested_due_date`, confidence 90; +5 if the item's learned payee rule matches.
2. **Payee rule**: `payee_rules` hit → `suggested_category_id`, confidence 70 (+ min(hits,20)).
3. **Keyword defaults**: a small built-in table (grocery chains → Groceries, streaming → Subscriptions, fuel → Transportation, utilities, insurance, pharmacies → Health, restaurants → Fun, "PAYROLL"/"DIRECT DEP" → income) → confidence 40.
4. Otherwise no suggestion, confidence 0.
Transfers between two mapped accounts (same |amount|, opposite sign, within 3 days) are both suggested as **ignore** ("transfer") with confidence 80.

## Confirm semantics
- **as expense**: creates an `expenses` row with `deduct = false` (the bank balance already reflects it), category from the user's choice, `name` = payee or description, `spent_on = posted_on`, `account_id` = mapped account. Learns/strengthens the payee rule.
- **as bill/income payment**: marks `occurrence(item, due_date)` paid with the actual amount (`paid_by` = reviewer); learns the item's payee rule so next month is auto-suggested.
- **ignore**: status ignored (transfers, refunds already handled, noise). Restorable.
- Bulk confirm accepts every pending row whose confidence ≥ 70 with its suggestion.
Balances: a sync updates each mapped account's `balance_cents` (+ snapshot) from SimpleFIN. For `credit`/`loan` accounts the Bridge balance is negative-when-owed → store as positive owed.

## Background job additions
`SchedulerService` phase 4 **bank sync**: for each `bank_links` with `sync_enabled` and `last_synced_at` older than `Sync__IntervalHours` (6): fetch 30 days (first sync 90), upsert external accounts, insert new transactions (fingerprint dedupe), run the matcher, update balances, write activity `bank.synced` with counts, push "Synced Chase · 4 to review" to members with `activity_enabled` when new rows > 0. Errors go to `last_error` and an activity row, never crash the loop. Also **auto-mark**: when a transaction's recurring match has confidence ≥ 90 and the item has a learned payee rule from a prior confirmation, mark the occurrence paid automatically (`paid_by NULL`, `auto = true`) and set the inbox row to confirmed with `confirmed_item_id` — the activity feed says "matched Rent · $1,850.00 (auto)" and Undo on the occurrence restores the inbox row to pending.

## Encryption
`Security__DataKey` (base64, 32 bytes) → AES-256-GCM for `access_url_enc` (12-byte nonce + tag + ciphertext). Without it `POST /banks/simplefin/claim` returns 503 `bank_sync_not_configured`; existing links cannot be decrypted (sync logs an error). Generated by `make secrets` / `install.sh` as `DATA_KEY`. Rotation = re-link.

## Web
- **Settings → Linked banks** (`/settings/banks`): explainer with the Bridge link and the $1.50 note; paste setup token + name → link; per link: status, last sync, errors from the Bridge, **Sync now**, unlink; per external account: balance, mapped to ▸ account select or "Create account" (prefilled name/type guess), "Not tracked".
- **Inbox** (`/inbox`): dashboard card "N transactions to review" (hidden at 0); list grouped by day, each row: payee/description, amount (red out / green in), account, suggestion chip (category or "Rent · due Nov 1" or "Transfer"); actions: **Confirm** (uses suggestion; tap chip to change category / pick a bill from a sheet), **Ignore**; header: **Confirm all suggested (N)**; filter chips Pending / Confirmed / Ignored; undo via Restore.
- **CSV import** (`/imports/csv`): choose account → file picker → client-side parse (papaparse) → preview first 10 rows → column mapping with auto-detect (date, description, amount **or** debit+credit, optional payee/memo), sign convention toggle ("outflows are negative" / "debit column") and date format detect → Import → summary (imported / duplicates / skipped) → "Review in inbox".
- Occurrence rows that were auto-matched show a "matched from bank" badge; Undo works as for autopay.
- More menu: Inbox, Linked banks, Import CSV. Activity feed renders the new kinds.

## Agents
| Agent | Owns | Done when |
|---|---|---|
| API | `apps/api/**` | 0003 migration; SimpleFIN client (verified against the protocol page), AES-GCM store, matcher + tests, inbox/banks/csv endpoints, scheduler phase, smoke extended with a **fake SimpleFIN server** (a tiny in-process or python http server serving protocol-shaped JSON) |
| Web | `apps/web/**` | screens above, mock API extended (incl. a fake setup-token flow), e2e: link → map → sync → inbox confirm/ignore/bulk → expense & paid bill appear; CSV upload → mapping → inbox |
| Infra | root/docs | `DATA_KEY` in `.env.example`/compose/`make secrets`/`install.sh`/`dev-env.sh`; `Sync__IntervalHours`; `docs/BANK-SYNC.md`; SECURITY rows; CLAUDE.md |
