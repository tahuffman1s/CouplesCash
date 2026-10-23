# CouplesCash API Contract (v1)

Base URL inside Docker: `http://api:8080`. Local dev: `http://localhost:8080`. All routes below are prefixed with `/v1`.
Interactive docs: `GET /scalar` (dev only). OpenAPI: `GET /openapi/v1.json`.

Conventions
- JSON, camelCase. Money is integer **cents** (`amountCents`, `balanceCents`). Dates are `YYYY-MM-DD`. Timestamps are ISO 8601 UTC. IDs are UUIDs.
- Auth: `Authorization: Bearer <accessToken>` on everything except `/v1/auth/register|login|refresh` and `/health`.
- Errors are RFC 9457 `application/problem+json`: `{ type, title, status, detail?, errors?: { field: [msg] } }`.
- Household scoping: the household id comes from the `hh` JWT claim. Users without a household get `409 { title: "No household" }` on household routes except `/v1/couple/*`.
- Unknown or foreign `{id}` → `404`.
- Rate limits: `/v1/auth/*`, `/v1/couple/join` → 10/min/IP (`429` with `Retry-After`). Global 120/min/IP.

## Shapes

```ts
type User       = { id: string; email: string; displayName: string; createdAt: string };
type Member     = { id: string; displayName: string; email: string; joinedAt: string };
type Household  = { id: string; name: string; createdAt: string; members: Member[] };
type AuthResult = { user: User; household: Household | null; accessToken: string; accessTokenExpiresIn: number /* seconds */; refreshToken: string; refreshTokenExpiresAt: string };

type AccountType = 'checking' | 'savings' | 'credit' | 'cash' | 'investment' | 'loan';
type Account = { id: string; name: string; institution: string; type: AccountType; balanceCents: number; color: string /* hex */; icon: string /* lucide icon name */; note: string | null; createdAt: string; updatedAt: string };
type BalanceSnapshot = { balanceCents: number; recordedAt: string; recordedBy: string };

type Category = { id: string; name: string; color: string; icon: string; sortOrder: number; isDefault: boolean };

type Kind = 'bill' | 'subscription' | 'income';
type Frequency = 'weekly' | 'biweekly' | 'monthly' | 'quarterly' | 'yearly' | 'once';
type RecurringItem = { id: string; name: string; kind: Kind; amountCents: number; categoryId: string | null; accountId: string | null; frequency: Frequency; anchorDate: string; endDate: string | null; autoPay: boolean; notes: string | null; url: string | null; monthlyEquivalentCents: number; nextDueDate: string | null; createdAt: string; updatedAt: string };

type Occurrence = { itemId: string; name: string; kind: Kind; amountCents: number; dueDate: string; autoPay: boolean; categoryId: string | null; categoryName: string | null; categoryColor: string | null; accountId: string | null; accountName: string | null; paid: { paidAt: string; paidBy: string; amountCents: number } | null };

type Summary = {
  month: string; // YYYY-MM
  netWorthCents: number; assetsCents: number; liabilitiesCents: number;
  monthlyBillsCents: number; monthlySubscriptionsCents: number; monthlyIncomeCents: number; // monthly-equivalent of active items
  dueThisMonthCents: number; paidThisMonthCents: number; remainingThisMonthCents: number;
  byCategory: { categoryId: string | null; name: string; color: string; amountCents: number; count: number }[]; // monthly-equivalent outflow
  byKind: { kind: Kind; amountCents: number; count: number }[];
  upcoming: Occurrence[]; // next 14 days, unpaid first, max 20
  accounts: Account[];
};
```

Monthly equivalent: weekly ×52/12, biweekly ×26/12, monthly ×1, quarterly ÷3, yearly ÷12, once → 0. Round half-even to cents.

## Auth

| Method | Route | Body | Response |
|---|---|---|---|
| POST | `/v1/auth/register` | `{ email, password (≥10 chars), displayName (1-40) }` | `201 AuthResult` · `409` email taken |
| POST | `/v1/auth/login` | `{ email, password }` | `200 AuthResult` · `401` (same message for bad email/password) |
| POST | `/v1/auth/refresh` | `{ refreshToken }` | `200 AuthResult` (rotated) · `401` invalid/expired/reused (reuse revokes the whole family) |
| POST | `/v1/auth/logout` | `{ refreshToken }` | `204` |
| GET | `/v1/auth/me` | — | `200 { user, household }` |
| PATCH | `/v1/auth/me` | `{ displayName? }` | `200 User` |
| POST | `/v1/auth/password` | `{ currentPassword, newPassword }` | `204` (revokes all refresh tokens) |

JWT claims: `sub` (user id), `email`, `name`, `hh` (household id, omitted when none), `exp`, `iat`, `jti`. Issuer/audience `couplescash`.
When a user joins/leaves a household the client must call `/v1/auth/refresh` to get a token with the new `hh` claim (the server also returns a fresh `AuthResult` from join/leave for convenience).

## Couple

| Method | Route | Body | Response |
|---|---|---|---|
| GET | `/v1/couple` | — | `200 Household \| null` |
| POST | `/v1/couple/code` | `{ name? }` (household name, used when creating one) | `201 { code: "ABCD-EFGH", payload: "cc1.<base64url 20 bytes>", expiresAt, household }` · `409` household already has 2 members |
| POST | `/v1/couple/join` | `{ code }` — accepts either the 8-char typed code (dashes/case ignored) or the full `cc1.` payload | `200 AuthResult` · `404` invalid/expired/used · `409` caller already in a household |
| POST | `/v1/couple/leave` | — | `200 AuthResult` (household kept for the other member; deleted if empty) |
| PATCH | `/v1/couple` | `{ name }` | `200 Household` |

Code generation: 20 random bytes. `payload = "cc1." + base64url(bytes)`. `code` = first 40 bits of SHA-256(bytes) as 8 Crockford-base32 chars grouped `XXXX-XXXX`. Both `sha256(bytes)` and `sha256(code)` are stored so either form can be redeemed. TTL 10 min, single use. Creating a new code invalidates earlier unredeemed codes for that household.

## Accounts

| Method | Route | Body | Response |
|---|---|---|---|
| GET | `/v1/accounts` | — | `200 Account[]` (non-archived, ordered by type then name) |
| POST | `/v1/accounts` | `{ name, institution, type, balanceCents, color?, icon?, note? }` | `201 Account` |
| GET | `/v1/accounts/{id}` | — | `200 Account` |
| PUT | `/v1/accounts/{id}` | same as POST (all fields) | `200 Account` |
| DELETE | `/v1/accounts/{id}` | — | `204` (soft delete: `archived_at`) |
| POST | `/v1/accounts/{id}/balance` | `{ balanceCents }` | `200 Account` (also appends a snapshot) |
| GET | `/v1/accounts/{id}/history?limit=30` | — | `200 BalanceSnapshot[]` newest first |

`credit` and `loan` balances are liabilities: store as positive "amount owed", summary subtracts them.

## Categories

| Method | Route | Body | Response |
|---|---|---|---|
| GET | `/v1/categories` | — | `200 Category[]` |
| POST | `/v1/categories` | `{ name, color, icon }` | `201 Category` |
| PUT | `/v1/categories/{id}` | `{ name, color, icon, sortOrder? }` | `200 Category` |
| DELETE | `/v1/categories/{id}` | — | `204` (items referencing it get `categoryId = null`) |

Defaults seeded when a household is created: Housing, Utilities, Insurance, Transportation, Groceries, Subscriptions, Debt, Health, Kids & Pets, Fun, Income, Other.

## Recurring items

| Method | Route | Body | Response |
|---|---|---|---|
| GET | `/v1/recurring?kind=` | — | `200 RecurringItem[]` |
| POST | `/v1/recurring` | `{ name, kind, amountCents, categoryId?, accountId?, frequency, anchorDate, endDate?, autoPay?, notes?, url? }` | `201 RecurringItem` |
| GET | `/v1/recurring/{id}` | — | `200 RecurringItem` |
| PUT | `/v1/recurring/{id}` | same as POST | `200 RecurringItem` |
| DELETE | `/v1/recurring/{id}` | — | `204` (soft) |
| PUT | `/v1/recurring/{id}/occurrences/{date}/paid` | `{ amountCents? }` (defaults to item amount) | `200 Occurrence` · `404` if `{date}` is not a real occurrence |
| DELETE | `/v1/recurring/{id}/occurrences/{date}/paid` | — | `200 Occurrence` (unpaid) |

## Calendar & summary

| Method | Route | Query | Response |
|---|---|---|---|
| GET | `/v1/calendar` | `from=YYYY-MM-DD&to=YYYY-MM-DD` (≤ 93 days) | `200 { from, to, occurrences: Occurrence[] }` sorted by dueDate, then name |
| GET | `/v1/summary` | `month=YYYY-MM` (default: current) | `200 Summary` |

## Health

`GET /health` → `200 { status: "ok", db: "ok" }` (no auth; used by Docker healthcheck).

## Web (BFF) surface — what the browser talks to

The SvelteKit server exposes the API to the browser only through `/api/[...path]` (same paths as above minus `/v1`), requiring header `X-Requested-With: CouplesCash`. Server `load` functions and form actions call the API directly via `$lib/server/api.ts`. Cookies: `cc_access` (15 min), `cc_refresh` (30 d), `theme` (1 y, not httpOnly). The BFF never exposes refresh tokens to client JS.

## Implementation notes (resolved during build)

- Validation failures return **400** with `errors: { field: [msg] }`. A wrong current password on `POST /v1/auth/password` is a 400 field error (`errors.currentPassword`), not a 401, so the BFF never confuses it with an expired token. A `categoryId`/`accountId` from another household is a 400 field error; 404 is reserved for the route `{id}`.
- The strict 10/min rate limit covers register, login, refresh, logout, password and `/v1/couple/join`. `GET/PATCH /v1/auth/me` only fall under the global limit because the BFF calls them on navigation.
- `/v1/couple/*` reads membership from the database (the caller's token is stale right after creating a household); every data route uses the `hh` claim. After join/leave, use the returned `AuthResult` cookies.
- Typed codes normalise Crockford aliases (`O→0`, `I/L→1`), case and dashes.
- Summary: `monthly*`, `byKind` and `byCategory` are monthly equivalents of non-archived items active in the month; `byCategory` covers bills + subscriptions only, with a grey "Uncategorized" bucket. `dueThisMonth` = outflow occurrences in the month, `paidThisMonth` = recorded payments, `remainingThisMonth` = unpaid occurrences.
- Accounts record a balance snapshot on create, on PUT when the balance changes, and on `/balance`. `GET /health` returns 503 `{ status: "degraded", db: "error" }` when the database is unreachable.

---

# v2 additions

All v2 routes are under `/v1`, require auth and (except push/passkeys/prefs, which are per-user) a household. See [PLAN-V2.md](./PLAN-V2.md) for behaviour.

```ts
type PayPeriod = 'weekly' | 'biweekly' | 'bimonthly' | 'monthly';
// Household gains settings:
type Household = { id; name; createdAt; members: Member[];
  currency: string /* ISO 4217, default USD */; weekStart: 0 | 1; timezone: string /* IANA */;
  defaultAccountId: string | null; payPeriod: PayPeriod; payAnchorDate: string | null };
// Occurrence.paid gains `auto`:
type Paid = { paidAt: string; paidBy: string | null; amountCents: number; auto: boolean };

type ActivityEvent = { id: string; kind: string; summary: string; actorId: string | null; actorName: string | null;
  entityType: string; entityId: string | null; data: Record<string, unknown>; createdAt: string };
type Expense = { id: string; name: string; amountCents: number; categoryId: string | null; accountId: string | null;
  spentOn: string; note: string | null; deducted: boolean; createdBy: string; createdAt: string; updatedAt: string };
type BudgetRow = { categoryId: string; name: string; color: string; icon: string; budgetCents: number | null;
  recurringCents: number; expensesCents: number; spentCents: number; remainingCents: number | null; percent: number | null };
type PushPrefs = { remindersEnabled: boolean; daysBefore: number[]; hourLocal: number; activityEnabled: boolean; subscriptions: { id: string; userAgent: string; createdAt: string; lastUsedAt: string | null }[] };
type Todo = { id: string; title: string; amountCents: number | null; dueDate: string | null; sortOrder: number;
  doneAt: string | null; doneBy: string | null; doneByName: string | null; createdBy: string; createdAt: string };
type Goal = { id: string; name: string; targetCents: number; targetDate: string | null; accountId: string | null; accountName: string | null;
  color: string; icon: string; currentCents: number; percent: number; monthlyPaceCents: number | null /* avg change/month, last 90d */;
  projectedDate: string | null; monthlyNeededCents: number | null /* to hit targetDate */; history: { at: string; value: number }[]; createdAt: string };
type Passkey = { id: string; name: string; createdAt: string; lastUsedAt: string | null; backedUp: boolean };
type PayPeriodView = { period: PayPeriod; start: string; end: string /* inclusive */; nextPayday: string; daysLeft: number;
  expectedIncomeCents: number; dueCents: number; paidCents: number; remainingCents: number;
  spendableCents: number /* checking + cash balances − remaining */; occurrences: Occurrence[]; expensesCents: number };
type MonthReport = { month: string; summary: Summary; previousNetWorthCents: number | null; netWorthDeltaCents: number | null;
  expensesTotalCents: number; topExpenses: Expense[]; budgets: BudgetRow[]; autoPaid: Occurrence[]; paidCount: number; unpaidCount: number;
  generatedAt: string };
```

## Household settings
| Method | Route | Body | Response |
|---|---|---|---|
| PATCH | `/v1/couple` | `{ name?, currency?, weekStart?, timezone?, defaultAccountId?, payPeriod?, payAnchorDate? }` (all optional, validated: currency 3 upper letters, timezone valid IANA, account in household) | `200 Household` |
`GET /v1/couple` and `AuthResult.household` return the extended shape.

## Activity
| GET | `/v1/activity?before=<createdAt ISO>&limit=30` (limit ≤ 100) | `200 { events: ActivityEvent[]; nextBefore: string | null }` newest first |

Kinds (dot-separated): `account.created|updated|balance|deleted`, `recurring.created|updated|deleted`, `occurrence.paid|unpaid|autopaid`, `expense.created|updated|deleted`, `budget.set|cleared`, `todo.created|done|undone|deleted`, `goal.created|updated|contributed|deleted`, `couple.joined|left|settings`, `category.created|updated|deleted`, `user.renamed`. `summary` is a ready-to-show English sentence without the actor ("marked **Rent** paid · $1,850.00"); the web prefixes the actor name. `data` carries ids/amounts for deep links.

## Expenses
| Method | Route | Body | Response |
|---|---|---|---|
| GET | `/v1/expenses?month=YYYY-MM` (default current) or `?from&to` (≤ 93 days) | — | `200 Expense[]` newest `spentOn` first |
| POST | `/v1/expenses` | `{ name, amountCents (>0), categoryId?, accountId?, spentOn, note?, deduct?: boolean (default true when accountId set) }` | `201 Expense` (deducting adjusts the account balance and records a snapshot) |
| PUT | `/v1/expenses/{id}` | same fields (changing amount/account re-applies the deduction delta) | `200 Expense` |
| DELETE | `/v1/expenses/{id}` | — | `204` (reverses the deduction if `deducted`) |

## Budgets
| GET | `/v1/budgets?month=YYYY-MM` | `200 { month, rows: BudgetRow[] (every category, budgeted or not), totalBudgetCents, totalSpentCents }` |
| PUT | `/v1/budgets/{categoryId}` `{ amountCents (≥ 0) }` | `200 BudgetRow` |
| DELETE | `/v1/budgets/{categoryId}` | `204` |
`spentCents = recurringCents (monthly equivalents of bills+subscriptions in the category) + expensesCents (expenses in the month)`.

## Pay period
| GET | `/v1/payperiod?date=YYYY-MM-DD` (default today in household tz) | `200 PayPeriodView` · `409 { title: "Pay period not configured" }` when `payAnchorDate` is null |
Period math (pure, unit-tested): `weekly` = 7 days from anchor; `biweekly` = 14; `monthly` = anchor day each month (clamped); `bimonthly` = anchor day and anchor day + 15 (clamped; if anchor day > 15, the other payday is anchor − 15). `start` = most recent payday ≤ date, `end` = next payday − 1, `nextPayday` = `end + 1`. `expectedIncomeCents` = income occurrences in `[start, end]`.

## Autopay & occurrences
`PUT/DELETE /v1/recurring/{id}/occurrences/{date}/paid` unchanged; `Occurrence.paid.auto` is `true` for scheduler payments. Unpaying an auto payment is the **undo**.

## To-pay checklist
| Method | Route | Body | Response |
|---|---|---|---|
| GET | `/v1/todos?includeDone=false` | — | `200 Todo[]` open first by sortOrder, then done by doneAt desc (last 30 days) |
| POST | `/v1/todos` | `{ title (1-120), amountCents?, dueDate? }` | `201 Todo` |
| PUT | `/v1/todos/{id}` | `{ title, amountCents?, dueDate?, sortOrder? }` | `200 Todo` |
| POST | `/v1/todos/{id}/done` | — | `200 Todo` |
| DELETE | `/v1/todos/{id}/done` | — | `200 Todo` |
| DELETE | `/v1/todos/{id}` | — | `204` |
| PUT | `/v1/todos/order` | `{ ids: string[] }` | `204` |

## Savings goals
| Method | Route | Body | Response |
|---|---|---|---|
| GET | `/v1/goals` | — | `200 Goal[]` (non-archived) |
| POST | `/v1/goals` | `{ name, targetCents (>0), targetDate?, accountId?, color?, icon? }` | `201 Goal` |
| GET | `/v1/goals/{id}` | — | `200 Goal` (history: account snapshots or manual contributions, oldest first, ≤ 90 points) |
| PUT | `/v1/goals/{id}` | same as POST | `200 Goal` |
| DELETE | `/v1/goals/{id}` | — | `204` (soft) |
| POST | `/v1/goals/{id}/contribute` | `{ amountCents (≠ 0) }` | `200 Goal` · `400` for account-linked goals (update the account balance instead) |
Projection: `monthlyPaceCents` = (latest − earliest value in the last 90 days) ÷ months elapsed (null with < 2 points or < 7 days). `projectedDate` = today + (target − current) ÷ pace (null if pace ≤ 0 or already reached). `monthlyNeededCents` = (target − current) ÷ months until `targetDate` (null without a date or if reached).

## Push notifications
| Method | Route | Body | Response |
|---|---|---|---|
| GET | `/v1/push/vapid-public-key` | — | `200 { publicKey }` · `503 { type: ".../push_not_configured" }` |
| POST | `/v1/push/subscriptions` | `{ endpoint, keys: { p256dh, auth }, userAgent? }` | `201 { id }` (upsert by endpoint) |
| DELETE | `/v1/push/subscriptions` | `{ endpoint }` | `204` |
| GET | `/v1/push/prefs` | — | `200 PushPrefs` |
| PUT | `/v1/push/prefs` | `{ remindersEnabled, daysBefore (0-14, ≤ 5 values), hourLocal (0-23), activityEnabled }` | `200 PushPrefs` |
| POST | `/v1/push/test` | — | `202` (sends "CouplesCash notifications are working 🎉" to the caller's subscriptions) |
Payload JSON: `{ title, body, url, tag, icon: "/icons/icon-192.png" }`; the service worker shows it and opens `url` on click. Local hour is evaluated in the household time zone.

## Passkeys (WebAuthn)
| Method | Route | Body | Response |
|---|---|---|---|
| POST | `/v1/auth/passkeys/register/options` (auth) | `{ name (1-40) }` | `200 { challengeId, options: PublicKeyCredentialCreationOptionsJSON }` |
| POST | `/v1/auth/passkeys/register` (auth) | `{ challengeId, name, credential: RegistrationResponseJSON }` | `201 Passkey` |
| GET | `/v1/auth/passkeys` (auth) | — | `200 Passkey[]` |
| DELETE | `/v1/auth/passkeys/{id}` (auth) | — | `204` |
| POST | `/v1/auth/passkeys/login/options` (anon, `auth` rate limit) | `{}` | `200 { challengeId, options: PublicKeyCredentialRequestOptionsJSON }` (discoverable: empty allowCredentials, userVerification required) |
| POST | `/v1/auth/passkeys/login` (anon, `auth` rate limit) | `{ challengeId, credential: AuthenticationResponseJSON }` | `200 AuthResult` · `401` |
| GET | `/v1/auth/passkeys/config` (anon) | — | `200 { enabled: boolean, rpId }` |
Options/response JSON follow the WebAuthn Level 3 JSON encodings (what `@simplewebauthn/browser` produces/consumes). Challenges expire after 5 minutes and are single-use.

## Month report
| GET | `/v1/reports/month?month=YYYY-MM` | `200 MonthReport` |

## CLI
`dotnet CouplesCash.Api.dll --vapid` prints `Push__VapidPublicKey=…` and `Push__VapidPrivateKey=…` (base64url, P-256) and exits 0.

---

# v2 implementation notes (resolved during build)

Read these alongside the §v2 tables above; where the two differ the notes describe what the API actually does.

- **Money is hundredths for every currency.** `*Cents` fields are integer hundredths of the household `currency`
  (USD cents, EUR cents, JPY "hundredths" too) — the API is currency-agnostic and never converts. `currency` only
  drives formatting (activity summaries, push bodies): symbol for USD/EUR/GBP/CAD/AUD (`$1,850.00`, `€12.50`,
  `CA$…`, `A$…`), otherwise the ISO code prefix (`CHF 1,234.56`).
- **Activity `summary` is plain text, no markdown.** E.g. `marked Rent paid · $1,850.00`; the web prefixes the actor
  name (`actorName`, null for system events such as autopay) and uses the ids in `data` for links. `data` always
  carries the entity id (`accountId`, `itemId` + `dueDate`, `expenseId`, `categoryId`, `todoId`, `goalId`) plus
  amounts where relevant. `GET /v1/activity` pages with `before` = the last event's `createdAt` (keyset on
  `created_at DESC, id DESC`); `nextBefore` is null on the last page.
- **Household settings:** `PATCH /v1/couple` body fields are all optional; sending `null` for `defaultAccountId` or
  `payAnchorDate` clears them (other fields reject null). `timezone` must resolve with
  `TimeZoneInfo.TryFindSystemTimeZoneById` (IANA ids; the container ships tzdata). A name-only patch is recorded
  as `couple.settings` with summary "renamed the household to …". `GET /v1/couple`, `GET /v1/auth/me` and every
  `AuthResult.household` carry `currency, weekStart, timezone, defaultAccountId, payPeriod, payAnchorDate`.
- **Summary (`GET /v1/summary`) now includes expenses.** `byCategory` = monthly equivalents of active bills +
  subscriptions **plus** the month's expenses (count = items + expenses; names/colours come from the categories
  table, deleted categories fold into "Uncategorized"); `dueThisMonth` and `paidThisMonth` both add the month's
  expenses total (an expense is spent the moment it is recorded); `remainingThisMonth` stays "unpaid occurrences".
  "Active in the month" now also requires `anchorDate ≤ month end` (an item starting next year does not count).
- **Calendar** responses gain `expenses: Expense[]` (those dated in `[from, to]`) next to `occurrences`, so the grid
  can show expense dots without a second call. `Occurrence.paid` carries `auto` and `paidBy` is null for autopay.
- **Expenses:** `deduct` defaults to `true` whenever `accountId` is set; `deduct: true` without an account is a
  400 on `deduct`. Deducting adjusts the account inside the same transaction and records a balance snapshot:
  assets go **down** by the amount, liabilities (`credit`/`loan`, stored as amount owed) go **up**. `PUT` nets the
  old deduction against the new one per account (same account + new amount → one delta; moved account → restore
  one, charge the other; account removed → restore). `DELETE` reverses when `deducted`. `GET /v1/expenses/{id}`
  exists in addition to the list routes.
- **Budgets:** every category gets a row. `percent` = `round(spent / budget × 100)` (whole number, **not capped**,
  so over-budget reads 120, 250 …); null when unbudgeted; a zero budget is 100 when anything was spent else 0.
  `totalBudgetCents`/`totalSpentCents` sum **budgeted** categories only. `PUT /v1/budgets/{categoryId}` takes an
  optional `?month=YYYY-MM` (default current) and returns that month's row; a foreign `categoryId` is 404.
- **Pay period:** `daysLeft` = days from the requested date to `nextPayday` (0 on payday itself, which starts a new
  period). `spendableCents` = checking + cash balances − `remainingCents` (unpaid outflow in the period).
  `?date` defaults to today in the household time zone. Weekly/biweekly anchors may lie in the future (periods
  are projected backwards); monthly/bimonthly use only the anchor's day of month.
- **To-pay:** `GET /v1/todos` returns open items only unless `?includeDone=true`, which appends items done in the
  last 30 days (newest first). `POST` ignores `sortOrder` (appended at the end); `PUT /v1/todos/order` rejects any
  id outside the household with a 400 on `ids`. Checking an already-done item keeps the original who/when and
  writes no activity event.
- **Goals:** `percent` is clamped to 0–100. Manual goals start their `history` with `{ createdAt, 0 }` and add one
  point per contribution (running sum); account-linked goals use the account's latest 90 snapshots (oldest first).
  `monthlyPaceCents` uses points from the last 90 days (null with < 2 points or < 7 days between them); a month is
  30.4375 days; `monthlyNeededCents` rounds up and equals the full remainder when `targetDate` has passed.
  Taking out more than has been saved is a 400 on `amountCents`; so is contributing to a linked goal.
- **Autopay & undo guard:** the scheduler marks `autoPay` occurrences due today (household tz; also up to
  `Scheduler__AutopayLookbackDays` = 3 days back, for a Pi that was off) with `auto = true, paidBy = null`,
  writes `occurrence.autopaid` (`actorId` null) and pushes "Rent auto-paid · $1,850.00" to members with
  `activityEnabled`. `DELETE …/paid` on an auto-paid (or any `autoPay` item's) occurrence records a row in
  `autopay_skips (recurring_item_id, due_date)` so the scheduler **never re-pays it**; marking it paid by hand
  removes the skip. The event data carries `wasAuto`.
- **Push:** every `/v1/push/*` route is per-user (no household needed) and answers 503
  `type: https://couplescash.app/problems/push_not_configured`, title "Push not configured", while
  `Push__VapidPublicKey`/`Push__VapidPrivateKey` are blank. `DELETE /v1/push/subscriptions` takes
  `{ endpoint?: string, id?: string }` (either identifies one of the **caller's** subscriptions; 404 when neither
  matches, 400 when both are missing). Subscribing re-uses the id for the same endpoint (upsert) and resets its
  failure count. `daysBefore` is stored deduplicated and sorted. `POST /v1/push/test` returns 202 immediately;
  delivery happens on a background queue. Push failures: 404/410 delete the subscription at once, other failures
  count up and the subscription is dropped at 5. Payloads are `{ title, body, url, tag, icon }`; partner-activity
  pushes use body "<Actor> <summary>" and a deep link per entity (`/calendar?m=YYYY-MM&d=YYYY-MM-DD` for
  occurrences and reminders, `/goals/{id}`, `/expenses/{id}`, `/todos`, …).
- **Passkeys:** `GET /v1/auth/passkeys/config` is always 200 (`enabled`, `rpId` or null). The other routes answer
  503 `type: https://couplescash.app/problems/passkeys_not_configured` unless `WebAuthn__Origin` (falling back to
  `Api__PublicUrl`) is https — `http://localhost[:port]` is the only exception. `rpId` defaults to the origin's host.
  Registration uses `residentKey: required`, `userVerification: required`, `attestation: none`; options/response
  JSON are the WebAuthn L3 encodings used by `@simplewebauthn/browser` (extra fields such as `publicKey`,
  `authenticatorAttachment` are ignored). A failed/garbled registration is a 400 on `credential`; a failed login
  is 401. Challenges are single-use with a 5-minute TTL; the user handle is the user id's 16 bytes.
- **Month report:** `previousNetWorthCents` = latest snapshot per active account recorded strictly before the first
  day of the month (liabilities subtracted); null when no account has one that old, in which case
  `netWorthDeltaCents` is null too. `paidCount`/`unpaidCount` count outflow occurrences of the month; `autoPaid`
  lists occurrences with `paid.auto`. `topExpenses` = 5 largest by amount.
- **Activity on v1 routes:** account create/update/balance/archive, recurring create/update/archive, occurrence
  paid/unpaid, category create/update/delete, couple join/leave/settings and `PATCH /v1/auth/me` (rename, when
  the user has a household) all write their event in the same transaction as the change.
- **Scheduler config:** `Scheduler__Enabled` (default true; set false in tests), `Scheduler__IntervalSeconds` (60),
  `Scheduler__AutopayLookbackDays` (3). The job starts only after the app is listening (after migrations) and
  logs under `CouplesCash.Api.Scheduler.SchedulerService`.
- **CLI:** `dotnet CouplesCash.Api.dll --vapid` runs before any configuration is read (no database, no
  `Jwt__Secret`, read-only FS fine) and prints exactly two stdout lines, `Push__VapidPublicKey=…` and
  `Push__VapidPrivateKey=…` (base64url, 65-byte uncompressed P-256 point / 32-byte scalar), exit code 0.

---

# v3 additions

```ts
type BankLink = { id: string; provider: 'simplefin'; name: string; createdAt: string; lastSyncedAt: string | null; lastError: string | null;
  syncEnabled: boolean; bridgeErrors: string[]; accounts: ExternalAccount[] };
type ExternalAccount = { id: string; externalId: string; orgName: string; name: string; currency: string; balanceCents: number;
  availableCents: number | null; balanceAt: string; accountId: string | null; accountName: string | null; updatedAt: string };
type SyncResult = { linkId: string; newTransactions: number; updatedAccounts: number; autoMatched: number; errors: string[]; syncedAt: string };
type InboxStatus = 'pending' | 'confirmed' | 'ignored';
type Suggestion =
  | { kind: 'category'; categoryId: string; categoryName: string; confidence: number }
  | { kind: 'occurrence'; itemId: string; itemName: string; itemKind: Kind; dueDate: string; confidence: number }
  | { kind: 'transfer'; confidence: number }
  | null;
type InboxItem = { id: string; source: 'simplefin' | 'csv'; accountId: string | null; accountName: string | null; postedOn: string;
  amountCents: number /* signed */; description: string; payee: string | null; memo: string | null; pending: boolean; status: InboxStatus;
  suggestion: Suggestion; confirmed: { expenseId: string | null; itemId: string | null; dueDate: string | null; by: string | null; byName: string | null; at: string | null; auto: boolean } | null;
  createdAt: string };
type InboxSummary = { pending: number; suggested: number /* pending with confidence ≥ 70 */; lastSyncedAt: string | null; linkCount: number };
type CsvImportResult = { id: string; rowCount: number; imported: number; duplicates: number; skipped: number; inboxPending: number };
```

## Linked banks
| Method | Route | Body | Response |
|---|---|---|---|
| GET | `/v1/banks` | — | `200 BankLink[]` |
| POST | `/v1/banks/simplefin/claim` | `{ setupToken, name? }` | `201 BankLink` (claims, stores encrypted, runs a first sync inline with a 20 s budget) · `400 errors.setupToken` invalid/expired · `503 bank_sync_not_configured` without `Security__DataKey` · `502 bridge_unreachable` |
| DELETE | `/v1/banks/{id}` | — | `204` (keeps inbox rows and mappings' accounts; external accounts removed) |
| PATCH | `/v1/banks/{id}` | `{ name?, syncEnabled? }` | `200 BankLink` |
| POST | `/v1/banks/{id}/sync` | — | `200 SyncResult` · `429` if synced < 15 min ago (`Retry-After`) · `502` with `errors` on Bridge failure |
| PUT | `/v1/banks/accounts/{externalAccountId}` | `{ accountId: string | null }` or `{ create: { name, type, institution? } }` | `200 ExternalAccount` (mapping an account immediately updates its balance + snapshot) |

## Inbox
| Method | Route | Body / Query | Response |
|---|---|---|---|
| GET | `/v1/inbox/summary` | — | `200 InboxSummary` |
| GET | `/v1/inbox?status=pending&before=&limit=50` | | `200 { items: InboxItem[]; nextBefore: string | null }` newest `postedOn` first |
| POST | `/v1/inbox/{id}/confirm` | `{ as: 'expense', categoryId?, name? }` or `{ as: 'occurrence', itemId, dueDate }` | `200 InboxItem` · `400` sign/kind mismatch, not pending, occurrence already paid |
| POST | `/v1/inbox/{id}/ignore` | — | `200 InboxItem` |
| POST | `/v1/inbox/{id}/restore` | — | `200 InboxItem` (back to pending; reverses the confirmed expense / payment) |
| POST | `/v1/inbox/bulk` | `{ action: 'confirm-suggested' \| 'ignore', ids?: string[] }` (no ids = all pending; confirm-suggested only takes confidence ≥ 70) | `200 { confirmed, ignored, skipped }` |

## CSV import
| POST | `/v1/imports/csv` | `{ accountId, filename, rows: { postedOn: 'YYYY-MM-DD', amountCents: number, description, payee?, memo? }[] }` (≤ 5000 rows, body ≤ 2 MB — the web parses and maps columns client-side) | `201 CsvImportResult` |
| GET | `/v1/imports/csv` | — | `200 { id, filename, rowCount, imported, duplicates, createdAt, createdByName }[]` last 20 |

Activity kinds added: `bank.linked|unlinked|synced|error`, `import.csv`, `transaction.confirmed|ignored|restored|automatched`.
Occurrence payments created from the inbox carry `paid.auto = false` (user) or `true` (auto-match); unpaying an auto-matched occurrence restores the inbox row to pending.
Env: `Security__DataKey` (base64 32 bytes), `Sync__IntervalHours` (6), `Sync__ManualCooldownMinutes` (15), `SimpleFin__BaseUrl` (override for tests; default derived from the access URL).
