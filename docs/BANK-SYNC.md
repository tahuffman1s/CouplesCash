# Bank sync, CSV import and the Review inbox

Until v3, every balance and every expense in CouplesCash was typed in by hand. Now a
**SimpleFIN Bridge** link pulls balances and transactions from your banks every few hours,
and a **CSV import** covers banks the Bridge cannot reach (or a one-off statement). Both
land in one **Review inbox**, where each transaction is confirmed with a suggested category,
matched to a bill (which marks it paid), or ignored. Nothing is categorised silently — you
stay in the loop, but the typing is gone.

Bank sync is optional. Without a `DATA_KEY` in `.env` the feature is simply hidden; CSV
import and the inbox still work.

## Why SimpleFIN Bridge

Most bank-data providers are built for companies shipping an app to thousands of users: you
apply for a developer account, get your app reviewed, and pay per connected user. None of
that fits one household running a Pi in a closet. [SimpleFIN Bridge](https://beta-bridge.simplefin.org)
is built for exactly that case.

| | SimpleFIN Bridge | Plaid | Teller |
|---|---|---|---|
| Who pays | **You**, a flat **$1.50/month** for all your banks | The developer, per connected item | The developer, per enrolled user |
| Sign-up | Create an account, connect banks, click *New Setup Token* | Developer application + production approval | Developer application + approval |
| Access | **Read-only** balances and transactions, by protocol design | Depends on products requested | Read + payments |
| Credentials | You log in to your bank **at the Bridge**; CouplesCash only ever holds an access URL | OAuth / Link flow embedded in the app | Enrollment flow embedded in the app |
| Self-hoster friendly | Yes: one token per app, open protocol, no SDK | No | No |

How the pieces fit:

```
 you, once               SimpleFIN Bridge                     Pi: api container                       phones
 ┌──────────────┐  bank  ┌──────────────────┐  HTTPS, every  ┌──────────────────────────┐          ┌──────────────┐
 │ connect banks│ ─────▶ │ holds your bank  │ ◀────────────  │ SchedulerService         │ ───────▶ │ Inbox        │
 │ at the Bridge│        │ logins, caches   │  6 h (or       │ bank-sync phase          │  push    │ "4 to review"│
 │ New Setup    │  token │ balances +       │  "Sync now")   │ · fetch accounts/txns    │          │ Linked banks │
 │ Token → paste│ ─────▶ │ transactions     │                │ · dedupe (fingerprint)   │          │ Dashboard    │
 └──────────────┘        └──────────────────┘                │ · suggest category/bill  │          └──────────────┘
                                                             │ · update balances        │
                                                             │ access URL stored        │
                                                             │ AES-256-GCM (DATA_KEY)   │
                                                             └──────────────────────────┘
```

1. **Setup token.** After connecting your banks at the Bridge you create a *setup token*: a
   one-time, short-lived string (base64 of a "claim" URL).
2. **Claim.** You paste it into *Settings → Linked banks*. The API claims it once and receives
   an **access URL** — a credential that can read your accounts. It is encrypted with
   `DATA_KEY` (AES-256-GCM) before it touches the database, never logged, never returned to
   the browser. The token itself is now spent; a second app needs a second token.
3. **Sync.** Every `SYNC_INTERVAL_HOURS` (default 6) the background job fetches the last 30
   days through two weeks ahead (so pending charges the bank dates on their post day are included;
   90 days of history on the first sync, in requests of at most 44 days so the Bridge never warns
   about the range), updates the balances of mapped accounts, drops transactions it
   has already seen, and files the new ones in the inbox with a suggestion each.

## Requirements

| | Why |
|---|---|
| `DATA_KEY` in `.env` | Encrypts the access URL at rest. `make secrets` and `install.sh` generate it (`openssl rand -base64 32`). Empty → *Linked banks* says "not configured" and the API answers `503 bank_sync_not_configured`. |
| A SimpleFIN Bridge account ($1.50/month) **or** Plaid keys (free Trial plan, see *Plaid* below) | They hold the bank logins and talk to the banks. CouplesCash never sees a bank password. |
| Outbound HTTPS from the Pi to `beta-bridge.simplefin.org` | The only network connection bank sync makes. The tunnel is not involved; LAN-only installs can use bank sync too (it does not need `https://` like push and passkeys do). |
| A backup of `.env` | The dump alone cannot restore links — see *Backups* below. |

## Linking a bank

1. Create an account at <https://beta-bridge.simplefin.org> and subscribe.
2. **Connect your banks** there. The bank login happens on the Bridge's site (or your bank's own
   page), never inside CouplesCash. Some institutions ask for a one-time code; a few need
   re-authorising every 90 days — the Bridge tells you when.
3. On the Bridge, click **New Setup Token** and copy it (one long base64 string). It can be
   claimed **once** and expires if unused, so do the next step right away.
4. In CouplesCash: *More → Settings → Linked banks* → paste the token, give the link a name
   ("Chase", "Our credit union") → **Link**. The first sync runs immediately (it gives up after
   20 seconds and finishes in the background if the Bridge is slow).
5. **Map each external account.** Every account the Bridge reports appears under the link with
   its balance. For each one choose: an existing CouplesCash account, **Create account**
   (name and type pre-filled from the bank's data), or **Not tracked**. Mapping immediately
   copies the balance over and records a snapshot, so net worth updates on the spot.
6. Repeat with another token for a second Bridge account if you keep separate ones.

A link can be renamed, paused (*sync enabled* off) or removed. **Unlinking** deletes the
encrypted credential and the external accounts; your CouplesCash accounts, their balance history
and every inbox row stay. The token remains valid at the Bridge until you also delete it there
(*Delete access*), which you should do if the Pi is lost (see [SECURITY.md](./SECURITY.md)).

## Plaid (optional, next to SimpleFIN)

SimpleFIN refreshes each bank about once a day and its feed can lag a day or two. Plaid checks most
banks several times a day, and **Sync now** asks it to check right away (`/transactions/refresh`).
Both can be linked at once; their rows go through the same pending lifecycle, matching and plan.

**Set it up (once, on the Pi):**

1. Sign up at <https://dashboard.plaid.com/signup> and choose **Personal use**. That gives the free
   **Trial plan**: up to **10 bank connections** with real (production) data, Transactions included.
   The 10 is a lifetime count — removing a connection does not give the slot back — so avoid
   linking the same bank over and over. US and Canadian developers who signed up on or after
   2026-04-15 and have never had Production access qualify.
2. Dashboard → **Developers → Keys**: copy the **client_id** and the **Production secret** into `.env`:
   `PLAID_CLIENT_ID=…`, `PLAID_SECRET=…` (`PLAID_ENV=production`; `sandbox` with sandbox keys for
   testing with Plaid's fake banks, user `user_good` / password `pass_good`).
3. Dashboard → **Developers → API → Allowed redirect URIs**: add
   `https://<your PUBLIC_URL host>/settings/banks/plaid-oauth`. Banks that sign in through their own
   site (OAuth — US Bank, Chase, Wells Fargo, …) send you back there on a phone. The API derives that
   URL from `PUBLIC_URL`; `PLAID_REDIRECT_URI` overrides it. If Plaid rejects it (not listed yet) the
   API logs a warning and creates the link token without it, so desktop browsers still work (pop-up).
   Some OAuth banks also need Plaid's institution access to be approved in the dashboard first.
4. `make pi` (or `make up`). The log says `Plaid enabled (production, every 60 min, OAuth redirect …)`.
   `DATA_KEY` must be set too: the Plaid access token is stored encrypted with it.

**Link a bank:** *Settings → Banks → Connect a bank* opens Plaid's window; sign in to the bank there.
CouplesCash receives a one-time public token, exchanges it server-side and stores the access token
encrypted — the browser and the logs never see it. Then map the accounts exactly like a SimpleFIN
link. Plaid often needs a few minutes for the first transactions after linking ("Plaid is still
loading transactions…" on the link card); the scheduler checks every few minutes for half an hour.

**How it syncs:** every `Plaid__IntervalMinutes` (60) the scheduler reads the last 30 days (90 on the
first sync) with `/transactions/get`, the balances Plaid cached with them, and `/item/get`'s last
successful update (shown as "Bank data as of …"). *Sync now* also calls `/transactions/refresh` and
then checks every 3 minutes for 20 minutes, since Plaid fetches from the bank in the background.
A posted transaction names the pending one it replaces (`pending_transaction_id`), so the pending row
is updated in place even when the tip or the wording changed.

**When the bank wants a new sign-in** (`ITEM_LOGIN_REQUIRED`, a new password, MFA…): the link card
says so and shows **Reconnect**, which opens Plaid in update mode for that bank; the scheduler stops
trying until then. **Unlink** also revokes the access token at Plaid (`/item/remove`).

**Switching from SimpleFIN:** connect the bank through Plaid and map its accounts to the **same**
CouplesCash accounts. Plaid's first sync brings 90 days, most of which SimpleFIN already brought: a
posted Plaid row with exactly the same amount as a posted SimpleFIN row on that account, at most 3 days
apart, is kept once (the newcomer is hidden as `superseded` with `replaced_by`, anything it was
matched to reversed, one `bank.deduplicated` activity); new rows from either side keep being checked
while both are linked. Then unlink the SimpleFIN link — unlinking drops its pending charges, which
nothing can update any more (posted rows stay) — and cancel the Bridge subscription.

## How sync works

- **Cadence.** The scheduler inside `api` checks each link once a minute and syncs when the
  last sync is older than `SYNC_INTERVAL_HOURS` (6). The Bridge caches upstream data, so a
  shorter interval just hits the cache — leave it alone.
- **Sync now.** The button on the link card syncs immediately, at most **once per 15 minutes
  per link**; inside the cooldown the API answers `429` and the UI shows when it is next allowed.
- **Balances.** Each mapped account gets the Bridge's balance plus a snapshot, so the dashboard
  net worth and the account sparklines update automatically. For **credit** and **loan**
  accounts the Bridge reports a negative number when you owe money; CouplesCash stores and shows
  it as a positive **owed** amount, like a manually entered card.
- **Transactions.** New rows are deduplicated by a fingerprint of the bank's transaction id, so
  overlapping windows never create duplicates. Rows the bank still marks *pending* carry a
  *pending* badge and can change amount or disappear when they settle.
- **Notifications.** When a sync files new rows, members with *partner activity* pushes on get
  *"Synced Chase · 4 to review"*. The activity feed records `bank.synced` with counts.
- **Errors.** Connection problems the Bridge reports ("Chase: connection needs attention")
  are shown on the link card and in the activity feed — they are never swallowed. The job
  moves on to the next link; nothing else is affected.

## The Review inbox

*More → Inbox*, or the dashboard card *"N transactions to review"* (hidden at zero). Rows are
grouped by day: payee or description, amount (red out / green in), the account, and a
**suggestion chip**:

| Chip | What CouplesCash found | How |
|---|---|---|
| **Rent · due Nov 1** | An unpaid occurrence of a bill, subscription or income item | Due within ±5 days of the posting date, amount within 2 % (or $1), direction matches (bills ↔ money out, income ↔ money in). Confidence 90; 95 if you have confirmed this payee for that item before. |
| **Groceries** (a category) | A category for an expense | First from your own history (*payee rules*: "WHOLEFDS" → Groceries because you confirmed it before; confidence 70+), otherwise a small built-in keyword table (grocery chains, streaming, fuel, utilities, insurance, pharmacies, restaurants, "PAYROLL"; confidence 40). |
| **Transfer** | Money moving between two of your mapped accounts | Same amount, opposite sign, within 3 days, on two mapped accounts. Suggested as *ignore* on both sides. |
| *(none)* | Nothing matched | Pick a category or a bill yourself. |

Actions on each row:

- **Confirm** uses the suggestion. Tap the chip first to change the category, or to pick a
  different bill from a sheet.
  - **As an expense** creates an expense (category of your choice, name = payee or description,
    date = posting date, account = the mapped account) with **deduct = off**. The bank balance
    the sync just imported *already* includes this charge, so deducting again would count it
    twice. Manual expenses you type in yourself still deduct, as before. Confirming also
    teaches the payee rule, so the same shop is suggested next time.
  - **As a bill / income payment** marks that occurrence **paid** with the actual bank amount,
    recorded as paid by you, exactly as if you had tapped *Mark paid* on the calendar. It also
    learns the payee for that item, so next month's charge is auto-matched (below).
- **Ignore** files the row away without creating anything: transfers, refunds you already
  handled, noise. Ignored rows are kept and can be **restored**.
- **Confirm all suggested (N)** in the header confirms every pending row whose suggestion has
  confidence ≥ 70 — i.e. bill matches and payees you have confirmed before, never the built-in
  keyword guesses. Review those by hand.
- **Filters**: *Pending* / *Confirmed* / *Ignored*.
- **Undo** = **Restore** on a confirmed or ignored row. It puts the row back to pending and
  reverses what the confirmation did: the expense is deleted, or the occurrence is marked
  unpaid again.

**Auto-match.** Once you have confirmed a payee for a bill once, the next time a transaction
matches that bill with confidence ≥ 90 the sync marks the occurrence paid **automatically**
(`auto`, no actor — the same mechanism as autopay) and files the inbox row as confirmed. The
calendar shows a *matched from bank* badge, the activity feed says *"matched Rent · $1,850.00
(auto)"*, and **Undo** on the occurrence puts the inbox row back to pending. Expenses are never
auto-confirmed; only bill/income matches are. *(v3 rule. Since v6 the fuzzy matcher below decides, and
the household's auto-match level can also auto-confirm expenses.)*

Everything the inbox does is recorded in the activity feed (`transaction.confirmed`,
`ignored`, `restored`, `automatched`) so both partners can see what happened.

## Automatic matching

Since v6 a small **matcher** service (`apps/matcher`, Python + RapidFuzz, its own container) reads every new
transaction like a person would — *who* was paid, *how much* and *when* — and either applies the obvious matches
for you or puts one clear suggestion on the row. The inbox is now the **To review** section of the Transactions
tab and only lists what still needs a human.

**When it runs.** On every bank sync, after every CSV import, whenever a bill or one-off is created or edited, and
on a scheduler sweep every 30 minutes over all To-review rows (`POST /v1/inbox/rematch` queues one at once).

**What is scored.** Each transaction against every unpaid occurrence due near it, with three scores (0–100):

| Score | How | Weight |
|---|---|---|
| **Name** | The bank text, cleaned up (`ACH DEBIT`, `POS`, card numbers, phone numbers, store numbers, dates, city/state, `SQ *` / `PAYPAL *` prefixes removed; `AMZN Mktp US*2K4H71` → `AMAZON`), compared fuzzily with the item's name **and its bank names** | 45 % |
| **Amount** | Exact = 100; inside the tolerance it slides to 70; beyond it drops fast. Tolerance: bills max(2 %, $1) · one-offs max(15 %, $10) · *amount varies* max(35 %, $15). Money out only matches bills/subscriptions, money in only income. | 35 % |
| **Date** | 100 on the due date, 60 at the edge of the window: ±5 days for bills, ±7 for one-offs, ±10 for *amount varies*. Early and late count the same. Outside the window: no match. | 20 % |

Each transaction gets at most one occurrence and each occurrence at most one transaction (best total first, across
the whole batch); occurrences already marked paid are never taken.

**Bank names** ("Bank shows it as"). Every bill, income and one-off has a list of names the bank uses for it,
editable as chips in the bill and one-off forms — `Rent` might add `OAKWOOD PROPERTY`. When a match is confirmed,
by you or automatically, the cleaned-up bank text is **learned** into that list (up to 10; names you typed are never
pushed out; learned ones can be removed). Tick **Amount varies** for utilities and card payments so a $112 water
bill still matches a $90 estimate.

**Auto-match level** (*Settings → Household*, default **All**):

| Level | Applied automatically | Left as a suggestion |
|---|---|---|
| **Off** | nothing | everything |
| **Bills** | bill, income and one-off matches, detected transfers | expense categories |
| **All** | the above **plus** expenses whose payee strongly matches a category you have confirmed at least twice (name ≥ 90) | the rest |

A bill match is applied automatically when the total is ≥ 85 **and** the name ≥ 70 **and** the amount is inside
the tolerance — or when the amount is exact, it is within 3 days, the name is at least half-similar and no other
occurrence nearby has that exact amount. Weaker matches (total ≥ 55) become the row's one-tap suggestion
(**"It's Rent"**).

**Why it matched.** Every automatic or suggested match carries its reason, shown on the row, in the Transaction
sheet and in the activity feed: *"Auto-matched · name 94 % · amount exact · 1 day early"*, or for categories
*"payee KROGER (3 confirmations)"*. The scores are stored on the transaction (`match_score`, `match_detail`), with
`engine: fuzzy` or `builtin`.

**Undo.** Every automatic action is an ordinary, reversible action: *Unmatch* on the transaction (or *Undo* in the
toast / on the occurrence) marks the occurrence unpaid again and returns the row to To review; an auto-confirmed
expense is deleted the same way. The activity feed records `transaction.automatched` / `transaction.autocategorized`
with the reason, and the undo.

**When the matcher is down.** The API never waits on it: if `matcher` is unreachable, slow or answers an error, the
API uses its **built-in matcher** (the v5 C# rules: exact amount ±2 % / $1, ±5 days, learned payees) and logs a
warning. Results are marked `engine: builtin`, and the matcher status (`GET /v1/matcher/status`) says which engine
ran last. Nothing else
changes, and the next sweep re-scores with the fuzzy engine once it is back. `make logs S=matcher` shows its log
(request counts and timings only — never descriptions).

**Found bills.** The matcher also looks for regular payments you have not set up yet: at least 3 payments to the
same (fuzzy) name whose gaps fit weekly, every two weeks, monthly (same day ±3) or quarterly, with amounts within
10 % (or within 35 % → *amount varies*). After each sync and once a day the week and bills pages show a card
*"We found 3 regular payments"*: **Netflix · $15.49 every month · next Nov 7** with **Add** / **Not a bill**.
**Add** creates the item with that bank name and immediately matches the past transactions it was found from;
**Not a bill** never suggests it again. Names that already belong to an item (or its bank names), transfers,
yearly payments and payments that stopped are not suggested.

## CSV import

For banks the Bridge does not support, for history older than the sync window, or if you
would rather not pay for the Bridge at all. *More → Import CSV* (`/imports/csv`).

1. **Choose the CouplesCash account** the statement belongs to.
2. **Pick the file.** It is parsed in your browser (nothing is uploaded yet); the first ten rows
   are previewed.
3. **Map the columns.** Auto-detected, adjustable:
   - **Date** — ISO `2026-10-05`, US `10/05/2026`, EU `05.10.2026` / `05/10/2026` and long
     forms like `Oct 5, 2026` are recognised; if day/month is ambiguous the mapping step asks.
   - **Description**, plus optional **Payee** and **Memo** columns.
   - **Amount** — either **one signed column** (`-12.34` out, `+1850.00` in) **or** separate
     **Debit** and **Credit** columns. A **sign convention** toggle covers banks that export
     charges as positive numbers ("outflows are negative" / "debit column").
4. **Import.** Up to 5 000 rows (2 MB) per file. The summary shows *imported / duplicates /
   skipped*; rows go to the inbox as pending with suggestions, just like synced ones.
5. **Review in inbox.**

**Duplicates.** Each row is fingerprinted from account + date + amount + normalised
description, so importing the same statement twice, or two statements with overlapping date
ranges, is safe — the overlap is reported as *duplicates* and not re-added. Two genuinely
identical charges on the same day (two coffees, same price) collapse into one; split them by
importing the second as a manual expense. CSV rows are **not** matched against SimpleFIN rows
(the bank's export has no SimpleFIN id), so do not import a CSV for an account that is also
linked unless you want to ignore the duplicates by hand.

**Getting a CSV from your bank.** On the bank's website (apps rarely export), open the account →
*Statements*, *Activity* or *Transactions* → *Download* / *Export* → choose **CSV** (sometimes
labelled "Excel" or "Spreadsheet") and a date range. Quicken/QFX/OFX files are not CSV; pick the
plain CSV option. The last 20 imports are listed on the page with their counts.

## Troubleshooting

| Symptom | Cause → fix |
|---|---|
| *Linked banks* says "Bank sync is not configured" / API `503 bank_sync_not_configured` | `DATA_KEY` is empty in `.env` → `make secrets && make up` (the key is generated, nothing else changes). Native dev: `make secrets` also rewrites `.env.dev`. |
| `api` restarts on boot with `Security__DataKey must be base64 (32 bytes)` / `must decode to exactly 32 bytes` | `DATA_KEY` was edited by hand or truncated. It must be the base64 of exactly 32 random bytes: `openssl rand -base64 32`. Fix `.env`, `make up`. (If links already existed under the old value, they need re-linking.) |
| Linking fails with "invalid or expired setup token" (`400`) | Tokens are single-use and short-lived. Create a fresh *New Setup Token* at the Bridge and paste it within a few minutes; paste the whole string (no quotes or spaces). |
| Linking fails with `502 bridge_unreachable` | The Pi cannot reach `beta-bridge.simplefin.org` (DNS, firewall, Pi-hole blocking?). `docker compose exec web node -e "fetch('https://beta-bridge.simplefin.org').then(r=>console.log(r.status))"`. |
| Link card shows "connection needs attention" / a bank name with an error | The **Bridge** lost its connection to that bank (password changed, re-authorisation due, MFA). Fix it on the Bridge's site; CouplesCash picks it up at the next sync. Nothing to change in the app. |
| Sync now → "try again in N minutes" (`429`) | The 15-minute per-link cooldown. The scheduled sync is unaffected. |
| Settings → Linked banks shows "cannot decrypt" after a restore or a move | The database was restored with a different `DATA_KEY` than it was written with. Restore the matching `.env` copy from the backup folder (`make restore` reminds you) and `make up`. If the key is really gone: unlink and re-link each bank. |
| Duplicate transactions in the inbox | A CSV imported for an account that is also linked (see *CSV import*), or the bank changed a pending transaction's id when it settled. Ignore the extras. |
| Amounts have the wrong sign after a CSV import (charges show green) | Wrong sign convention. Ignore the batch in the inbox (*Ignore all* with the import's rows selected), re-import with the toggle flipped. |
| Balance of a credit card shows negative | The account type in CouplesCash is not *credit*/*loan*. Edit the account's type; the next sync stores the owed amount as positive. |
| Matches say `builtin` / Settings shows the matcher as unreachable | The `matcher` container is down or `MATCHER_KEY` differs between `api` and `matcher`. `make ps`, `make logs S=matcher` (an empty key makes it exit with "MATCHER_KEY is empty"). Fix: `make secrets && make up` (fills an empty `MATCHER_KEY`; both containers read the same value). Matching keeps working with the built-in rules meanwhile. |
| A bill keeps landing in To review instead of matching | Its bank text is not close to the item's name: add a bank name to the item (or confirm one match — it is learned). For utilities with changing amounts tick *Amount varies*. Check the auto-match level is not *Off*. |
| Nothing syncs and the activity feed is quiet | `SCHEDULER_ENABLED=false` in `.env`, or *sync enabled* is off on the link, or `api` is unhealthy (`make ps`). `make scheduler-logs` shows every sync decision. |

Useful commands on the Pi:

```bash
make scheduler-logs | grep -i sync   # what the bank-sync phase did each tick (names and counts only)
grep -c '^DATA_KEY=.' .env           # 1 = key present (never paste the value anywhere)
make secrets                         # fills an EMPTY DATA_KEY; FORCE=1 rotates it and breaks every link
make backup                          # DB dump + .env copy (DATA_KEY) → backups/
make logs S=matcher                  # fuzzy matcher: one line per request (counts, timings), never descriptions
```

## Backups

`make backup` writes `backups/couplescash-<stamp>.sql.gz` **and** `couplescash-<stamp>.env`
(mode 600). The database holds the bank access URLs only in encrypted form; the key is in
`.env`. A dump restored without its `.env` has links nobody can read — you would have to
re-link every bank. Keep both files together, off the Pi, somewhere encrypted: the `.env` copy
is a secret like the dump itself.

## Privacy and security notes

- **The matcher** sees transaction descriptions and item names only for the duration of a request, over the
  internal compose network (shared key, no published port). It stores nothing and logs only counts and timings.
- **What leaves the Pi:** HTTPS requests to the SimpleFIN Bridge (claiming the token, then
  `GET …/accounts` each sync) — nothing else. No bank data goes to Cloudflare beyond the
  ordinary page traffic the tunnel already carries, and nothing is sent to us.
- **What the Bridge knows:** your bank logins (you gave them to the Bridge, not to CouplesCash)
  and that *some* app fetched your data with your token. It does not know what CouplesCash does
  with it.
- **Credentials at rest:** the access URL is encrypted with AES-256-GCM under `DATA_KEY` before
  it is stored; the plaintext exists only in the `api` process memory while syncing. It is never
  logged (logs show link names and counts) and never returned by any API route.
- **Key loss or rotation = re-link.** `make secrets FORCE=1` warns loudly before rotating
  `DATA_KEY`. Rotation is the right move if `.env` leaked (also *Delete access* at the Bridge).
- **Household scoping.** Links, external accounts, inbox rows, payee rules and imports are all
  household-scoped like everything else; the inbox of one couple cannot see another's. Every
  inbox action is written to the activity feed.
- **Read-only by construction.** A SimpleFIN access URL can read; it cannot move money. Even a
  decrypted URL in the wrong hands is a privacy problem, not a theft problem — revoke it at the
  Bridge (*Delete access*) and rotate `DATA_KEY`.

See [SECURITY.md](./SECURITY.md) for the threat model.
