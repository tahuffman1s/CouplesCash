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

## v3 implementation notes (API)

Deviations and clarifications from the tables above, as built in `apps/api`:

- **SimpleFIN protocol** was re-verified against https://www.simplefin.org/protocol.html (now a v2.0.0-draft). The client
  parses both the v1 shape the Bridge serves today (`errors: string[]`, per-account `org`, `payee`/`memo`) and the v2 draft
  (`errlist: [{code,msg}]`, top-level `connections`, `transacted_at`, `posted: 0` while pending → treated as pending, dated
  by `transacted_at` or today). Amounts are decimal strings → integer cents (banker's rounding); dates are unix seconds.
  Requests send `start-date`, `end-date` (exclusive) and `pending=1`; the Basic credentials from the access
  URL's userinfo go in the `Authorization` header. SimpleFIN transaction ids are only unique per account, so the dedupe
  fingerprint is `sha256('simplefin|' + accountExternalId + '|' + txId)`.
- **`Occurrence.paid.source`**: `'manual' | 'autopay' | 'bank'` (new column `occurrence_payments.source`). `'bank'` for both
  inbox confirm-as-occurrence (`auto=false`, `paidBy` = reviewer) and scheduler auto-match (`auto=true`, `paidBy=null`).
  `auto` keeps its v2 meaning.
- **`InboxItem.confirmed`** also carries `categoryName` (category of the created expense) and `itemName`.
- **Manual sync cooldown** (`Sync__ManualCooldownMinutes`, 15) counts only `POST /banks/{id}/sync`; the inline first sync of
  `claim` and scheduler syncs do not start it, so "Sync now" works right after linking. 429 carries `Retry-After` (seconds)
  and a `retryAfter` extension.
- **`PUT /banks/accounts/{id}` with `{ create }`** creates the account (type guessed from the bank's name unless given,
  institution = bank org name unless given, balance = sign-converted Bridge balance) and returns the `ExternalAccount`
  with the new `accountId`/`accountName`. `{ accountId: null }` (or `{}`) unmaps. Mapping re-points the external account's
  inbox rows (`accountId`/`accountName`) and immediately writes the balance + a snapshot (snapshot `recordedBy` = caller;
  scheduler syncs use the user who linked the bank). `ExternalAccount.balanceCents` is the **raw Bridge value**
  (credit/loan negative when owed); only the mapped CouplesCash account stores positive amount owed.
- **`POST /inbox/bulk`** without `ids` = every pending row. `confirm-suggested` takes rows with confidence ≥ 70: an
  occurrence suggestion → paid, a transfer suggestion → ignored (counted in `ignored`), a category suggestion → expense
  (outflows only; an inflow with a category suggestion is `skipped`). Rows below 70 or without a suggestion are `skipped`.
  Bulk writes one activity event instead of one per row.
- **Confirm as expense** refuses inflows (`400 errors.as`) — money in is matched to an income item or ignored. `categoryId`
  defaults to the suggested category; `name` defaults to `payee ?? description` (≤ 80 chars); `note` = memo. Learning a
  payee rule also re-suggests other pending rows with the same normalised payee (so confirming one Kroger row makes the
  other Kroger rows "suggested").
- **Suggestion order**: recurring match (90, +5 with a learned item rule) → transfer (80) → payee rule (70 + min(hits, 20))
  → keyword (40). Within one sync batch each unpaid occurrence is handed out once. `payee_norm` is computed from
  `payee` when present, else `description`. Transfer detection treats every account that receives bank or CSV rows as
  "mapped".
- **Auto-match** runs on every SimpleFIN sync (claim, manual, scheduler), never on CSV imports: recurring confidence ≥ 90
  **and** a learned payee rule naming that item. It ignores `autopay_skips` (the bank proves the money moved).
- **Undo** (`DELETE /recurring/{id}/occurrences/{date}/paid`) restores the inbox row that paid the occurrence (confirmed or
  auto-matched) to pending; an autopay skip is written only when the item is `autoPay` (or the payment was a genuine
  autopay). `POST /inbox/{id}/restore` on an occurrence confirmation likewise writes a skip only for `autoPay` items.
- **Extra activity kinds**: `bank.updated` (rename / pause / resume), `bank.mapped`, `bank.unmapped`; `account.created`
  is also written when `{ create }` is used. `bank.synced` is written on every sync (actor = user for manual, null for
  scheduler/claim-inline) with `data: { newTransactions, updatedAccounts, autoMatched, errors, manual }`; the push
  "Synced <name> · N to review" goes to members with activity pushes on (minus the actor) when N > 0.
- **Scheduler back-off**: `bank_links.last_attempt_at` is set on success and failure; a link is due when
  `max(last_synced_at, last_attempt_at)` is older than `Sync__IntervalHours`, so a broken link is retried at the normal
  cadence instead of every tick. `Sync__IntervalHours=0` = every tick (tests/smoke only).
- **CSV dedupe** is per source: a CSV row never dedupes against a SimpleFIN row of the same purchase (different
  fingerprint inputs). Rows with `amountCents = 0`, no date or a blank description are counted in `skipped` rather than
  failing the import (shape errors still 400). `POST /imports/csv` is the only route with a 2 MB body limit
  (`RequestSizeLimit` metadata; everything else keeps Kestrel's 64 KB).
- **Without `Security__DataKey`**: `POST /banks/simplefin/claim` and `POST /banks/{id}/sync` → `503 bank_sync_not_configured`;
  `GET /banks` still works and reports `lastError: "encryption key missing"` on every link; the scheduler skips bank sync.
  `SimpleFin__BaseUrl` (tests) rewrites the scheme/host/port of the claim and access URLs and allows plain-http access URLs.

---

# v4 additions

```ts
type BudgetPeriod = 'monthly' | 'weekly';
// RecurringItem gains: payeeHint: string | null
// Household gains: weeklySpendCents: number | null
// PushPrefs gains: weekSummaryEnabled: boolean
// Occurrence gains: varianceCents: number | null   /* paid.amountCents − amountCents when paid, else null */
//                   status: 'planned' | 'cleared' | 'overdue'
type WeekDay = { date: string; planned: Occurrence[]; expenses: Expense[]; plannedOutCents: number; plannedInCents: number; unplannedCents: number };
type WeekBudgetRow = { categoryId: string | null /* null = overall */; name: string; color: string; icon: string; budgetCents: number;
  spentCents: number; varianceCents: number /* spent − budget, + = over */; percent: number };
type WeekBudgetHistory = { weekStart: string; budgetCents: number; spentCents: number; varianceCents: number }[]; // last 8 weeks incl. current, oldest first
type WeekView = {
  start: string; end: string; weekStart: 0 | 1; isCurrent: boolean; isPast: boolean; today: string;
  spendableNowCents: number; startingBalanceCents: number | null /* past weeks: snapshots at/before start */;
  incomeExpectedCents: number; incomeReceivedCents: number; plannedOutCents: number; paidOutCents: number; remainingOutCents: number;
  unplannedCents: number; leftAtEndCents: number | null /* null for past weeks without a starting balance */; projected: boolean;
  days: WeekDay[]; overdue: Occurrence[] /* unpaid planned before `start` still open, shown once at the top */;
  budget: { overall: WeekBudgetRow | null; categories: WeekBudgetRow[]; totalBudgetCents: number; totalSpentCents: number; history: WeekBudgetHistory };
};
```

| Method | Route | Body / Query | Response |
|---|---|---|---|
| GET | `/v1/week?start=YYYY-MM-DD` (any date in the week; default today) | | `200 WeekView` |
| GET | `/v1/budgets/week?start=` | | `200 WeekView['budget']` |
| PUT | `/v1/budgets/{categoryId}?period=weekly\|monthly` (default monthly) | `{ amountCents }` | `200 BudgetRow` (monthly) or `200 WeekBudgetRow` (weekly) |
| DELETE | `/v1/budgets/{categoryId}?period=` | | `204` |
| PATCH | `/v1/couple` | `{ weeklySpendCents?: number \| null }` (added to the existing settings body) | `200 Household` |
| PUT | `/v1/push/prefs` | `{ …, weekSummaryEnabled }` | `200 PushPrefs` |
| POST/PUT | `/v1/recurring` | `{ …, payeeHint?: string \| null }` | unchanged shapes + `payeeHint` |
`GET /v1/budgets?month=` is unchanged and returns monthly rows only.
Matcher: `once` items match within ±7 days and max(15 %, $10); `payeeHint` seeds an item payee rule so the first bank match auto-applies.
Week summaries: push kinds `week.ahead` (day before week start) and `week.left` (5th day of the week) at the user's `hourLocal`, once per week each, when `weekSummaryEnabled`.

## v4 implementation notes (API)

Deviations and clarifications from the "# v4 additions" tables, as built in `apps/api`:

- **Week bounds**: `start = date − ((dayOfWeek − weekStart + 7) mod 7)`, `end = start + 6`. `today` is the household-local
  date (`timezone`). `isCurrent` = start ≤ today ≤ end, `isPast` = end < today, `projected` = start > today (future weeks only;
  the current week's `leftAtEndCents` is also an estimate but is not flagged). `?start` must be `YYYY-MM-DD` (else 400).
- **Occurrence `status`** = `cleared` when paid (any source), `overdue` when unpaid and `dueDate < today`, else `planned`
  (an unpaid item due today is `planned`). `varianceCents = paid.amountCents − amountCents` when paid, else null. On `/calendar`,
  `/payperiod`, `/summary.upcoming` and the mark-paid responses "today" is UTC; on `/week` it is household-local.
- **Totals**: `incomeExpectedCents` = Σ planned income in the week; `incomeReceivedCents` = Σ actual of paid income; `plannedOutCents`
  = Σ planned bill/subscription amounts; `paidOutCents` = Σ **actual** amounts of the paid ones; `remainingOutCents` = Σ planned amounts
  of the unpaid ones (so `paidOut + remainingOut ≠ plannedOut` when actuals differ); `unplannedCents` = Σ expenses dated in the week.
  `WeekDay.plannedOutCents/plannedInCents` are planned amounts; `unplannedCents` the day's expenses.
- **`leftAtEndCents`**: current week = `spendableNow + unpaid income due in [today, end] − unpaid planned outflow due in [today, end]`
  (unpaid items due earlier in the week show as `overdue` rows and are not deducted again); past week =
  `startingBalance + incomeReceived − paidOut − unplanned`, null when `startingBalanceCents` is null; future week =
  `spendableNow + all unpaid income − all unpaid planned outflow due in [today, end]`. `spendableNowCents` = Σ balances of active
  checking + cash accounts. `startingBalanceCents` (past weeks only, else null) = Σ latest snapshots of active checking/cash accounts
  recorded **before local midnight of `start`**; null when no such account has a snapshot that old.
- **`overdue`**: unpaid occurrences (any kind) with `dueDate < start` and `dueDate < today`, looked back 90 days before `start`,
  oldest first, max 50. For a future week, items due between today and `start` are upcoming, not overdue.
- **Weekly budgets**: `spentCents` for a category = Σ expenses in the week in that category + Σ **actual** amounts of paid
  bill/subscription occurrences due in the week in that category; the overall row (`categoryId: null`, name "Weekly spending")
  counts everything including uncategorised. `percent` follows the v2 rule (whole number, uncapped; zero budget → 0 or 100).
  `categories` lists only categories with a weekly budget, in category sort order; `totalBudgetCents`/`totalSpentCents` cover
  the category rows only (not the overall target). `history` = the viewed week and the 7 before it, oldest first, judged against
  today's budgets: with an overall target `budget = target`, `spent` = everything; without one `budget = Σ category weekly
  budgets`, `spent` = Σ spent in those categories; empty when there is neither.
- **Budget routes**: `?period` is case-insensitive, defaults to `monthly`, anything else → `400 errors.period`. `PUT ?period=weekly`
  returns the category's `WeekBudgetRow` for the **current** week (household-local today); `?month` is ignored for weekly rows.
  `DELETE ?period=` removes only that period's row (404 when none). `budget.set` / `budget.cleared` activity events carry
  `data.period`; the weekly summary reads "set Groceries weekly budget to $100.00". `GET /v1/budgets?month=` and `/reports/month`
  stay monthly-only.
- **`weeklySpendCents`** on `PATCH /v1/couple`: ≥ 0 (≤ 9.99 billion cents), `null` clears; the `couple.settings` event lists
  "weekly spending target" and carries `data.weeklySpendCents`.
- **`payeeHint`** (≤ 80 chars, trimmed, blank → null) on `POST/PUT /v1/recurring` and in `RecurringItem`. Saving a hint upserts
  `payee_rules(payee_norm(hint), item_id = item)` for the household (the same normaliser as bank transactions: lower-case, no
  punctuation, digit tokens and processor noise dropped — "JIFFY LUBE #42" ≡ "Jiffy Lube"); any learned category on that payee is
  kept and `hits` is not bumped. Changing or clearing the hint, or archiving the item, removes the previous rule when it still
  points at the item (a rule that also carries a learned category is only unlinked from the item). Hints that normalise to
  nothing are ignored.
- **Matcher**: `once` occurrences match within ±7 days and `max(15 %, $10.00)`; recurring items keep ±5 days / `max(2 %, $1)`.
  A hinted one-off therefore scores 95 (90 + 5 rule bonus) and passes the auto-match preconditions (confidence ≥ 90 **and** a
  learned rule naming the item), so the first matching bank transaction marks it paid with the actual amount (`paid.source: bank`,
  `auto: true`, `paidBy: null`) and confirms the inbox row with `auto`. Undo works as in v3 (the inbox row returns to pending).
- **`weekSummaryEnabled`** on `PUT /v1/push/prefs` defaults to `true` when omitted (v2/v3 clients keep working) and is returned on
  `PushPrefs`. Week summaries go to users with at least one push subscription, during their `hourLocal`, once per
  `(user, week_start, kind)` via `week_summary_log`: `ahead` on the last day of the current week (keyed on the coming week's start),
  `left` on the 5th day of the week (keyed on the current week's start). Payloads: `{ title: "Week ahead", body: "Week ahead: $X planned,
  $Y expected left", url: "/week", tag: "week-ahead" }` (planned = the coming week's `plannedOutCents`, left = its projected
  `leftAtEndCents`) and `{ title: "This week", body: "You have $Z left this week · budget $A under/over", url: "/week", tag: "week-left" }`
  (budget part = overall target variance, else the budgeted categories' total variance, omitted without weekly budgets; "budget on
  target" at 0).
- **Migration 0004**: `budgets.period` (`monthly` default, CHECK) with the PK rebuilt as `(household_id, category_id, period)`;
  `households.weekly_spend_cents`; `recurring_items.payee_hint`; `notification_prefs.week_summary_enabled`; `week_summary_log`.

---

# v5 additions

```ts
type IgnoreReason = 'manual' | 'transfer' | 'dropped' | 'superseded';
// InboxItem gains: ignoreReason: IgnoreReason | null;
//   transfer: { peerId: string | null; peerAccountId: string | null; peerAccountName: string | null; auto: boolean } | null;
//   match: { itemId: string; itemName: string; itemKind: Kind; dueDate: string } | null   // set when confirmed as occurrence
//   expense: { expenseId: string; categoryId: string | null; categoryName: string | null } | null
// Account gains: availableCents: number | null; pendingOutCents: number; linked: boolean
// Household gains: autoIgnoreTransfers: boolean
type AccountTransaction = InboxItem & { kind: 'bank' | 'manual' };   // manual = typed expense on this account (id = expense id, source 'manual')
type MatchCandidate = { itemId: string; itemName: string; kind: Kind; dueDate: string; amountCents: number;
  paid: Paid | null; paidByThis: boolean; score: number /* 0–100 */ };
type TransferPair = { id: string; date: string; amountCents: number; from: { accountId: string; name: string; transactionId: string };
  to: { accountId: string; name: string; transactionId: string }; auto: boolean };
type TransferReport = { month: string; totalCents: number;
  flows: { fromAccountId: string | null; fromName: string; toAccountId: string | null; toName: string; totalCents: number; count: number }[];
  netByAccount: { accountId: string; name: string; inCents: number; outCents: number; netCents: number }[];
  pairs: TransferPair[]; oneSided: InboxItem[] };
type BillPayment = { dueDate: string; plannedCents: number; paid: Paid | null; varianceCents: number | null;
  transaction: { id: string; postedOn: string; amountCents: number; description: string; accountName: string | null } | null };
```

| Method | Route | Body / Query | Response |
|---|---|---|---|
| GET | `/v1/accounts/{id}/transactions` | `?filter=all\|pending\|review\|matched\|transfers&q=&month=YYYY-MM&before=&limit=50` | `200 { items: AccountTransaction[]; nextBefore: string \| null; totals: { inCents, outCents, pendingOutCents, count } }` |
| GET | `/v1/inbox/{id}/candidates` | `?days=30` | `200 { occurrences: MatchCandidate[]; transferPeers: InboxItem[] }` ranked |
| POST | `/v1/inbox/{id}/match` | `{ itemId, dueDate }` | `200 InboxItem` — any status; reverses a previous expense/transfer first · `400` sign/kind mismatch · `409` occurrence paid by another transaction |
| POST | `/v1/inbox/{id}/transfer` | `{ peerId?: string \| null }` | `200 InboxItem` (peer updated too; one-sided learns the payee) |
| POST | `/v1/inbox/{id}/unmatch` | — | `200 InboxItem` back to pending, reversing expense / payment / transfer (and the transfer peer) |
| POST | `/v1/recurring/from-transaction` | `{ transactionId, name?, kind?, frequency? }` | `201 { item: RecurringItem; transaction: InboxItem }` |
| GET | `/v1/recurring/{id}/payments` | `?limit=12` | `200 BillPayment[]` newest first |
| GET | `/v1/recurring/{id}/occurrences/{date}/candidates` | `?days=10` | `200 InboxItem[]` ranked unmatched rows |
| POST | `/v1/recurring/{id}/occurrences/{date}/link` | `{ transactionId }` | `200 Occurrence` |
| GET | `/v1/transfers` | `?month=YYYY-MM` | `200 TransferReport` |
| PATCH | `/v1/couple` | `{ autoIgnoreTransfers?: boolean }` | `200 Household` |

`GET /v1/inbox` excludes `ignoreReason ∈ {dropped, superseded}` unless `?includeHidden=1`. Existing `confirm` / `ignore` / `restore` stay.
Activity kinds added: `transaction.posted`, `transaction.dropped`, `transaction.matched`, `transaction.unmatched`, `transfer.paired`, `transfer.auto`, `transfer.unpaired`, `recurring.from_transaction`.

## v5 implementation notes (API)

Deviations and clarifications from the "# v5 additions" tables, as built in `apps/api`:

- **Pending reconciliation** (`Domain/PendingReconciler.cs`, pure) runs per external account on every SimpleFIN sync, before new
  rows are inserted. Inputs: the stored rows whose fingerprint is in the response plus the account's bank-pending rows posted on or
  after `fetchStart − 7`. A posted transaction with a new id replaces an **absent**, not hidden, pending row when the posted date is in
  `[pending − 1, pending + 7]`, the sign matches, `||amount| − |pending|| ≤ max(25 %, $5.00)` and the descriptions are similar
  (significant-token overlap coefficient ≥ 0.5 — tokens normalised, digits / processor noise / stop words dropped — or the same first
  significant token, or equal normalised payees). Score = `0.5·similarity + 0.3·amount closeness + 0.2·date closeness`; best pairs first,
  one-to-one. The row keeps id, status, suggestion, confirmation; it takes the new `external_id` + fingerprint, amount, date,
  description, payee, memo, `pending = false`, `last_seen_at`. Same-id posts are refreshed the same way (the v3 upsert no longer runs
  for them). When the amount **or date** changed, a confirmed expense follows (`amount_cents`, `spent_on`) and a matched occurrence's
  payment follows (`amount_cents` → variance); `transaction.posted` (actor null, `data: { transactionId, fromCents, toCents, postedOn,
  newExternalId, status, expenseId, itemId }`) is written only when the amount changed.
- **Dropped**: an absent pending row dated in `[fetchStart, today]` and **more than 5 days** old (`today − postedOn > 5`) becomes
  `ignored / dropped` (`reviewed_by NULL`); a confirmation is reversed through the same path as unmatch (expense deleted, payment
  removed + autopay skip for autoPay items, transfer peer back to pending), then `transaction.dropped` (actor null, `data.reason:
  'dropped'`). Absence is judged only when the response carried **no Bridge errors** (and only for accounts present in it);
  replacements still happen on a response with errors. A dropped row whose id reappears is restored to pending
  (`transaction.restored`, `data.reason: 'reappeared'`).
- **Superseded** (rows imported before v5, where the posted copy was stored as its own row while the authorization stayed pending):
  an untouched absent authorization whose posted copy (created later, present in the response, same matching rules) exists is hidden
  as `ignored / superseded`; if the authorization was reviewed and the copy was not, the copy is hidden and the authorization takes the
  posted values; if both were reviewed neither is touched (and the authorization is not dropped). The event is `transaction.dropped`
  with `data.reason: 'superseded'`.
- `last_seen_at` is set on every stored row present in a response. The `bank.synced` event data gains `autoIgnoredTransfers, posted,
  replaced, dropped, superseded`; `SyncResult` is unchanged. The "N to review" push excludes auto-matched and auto-ignored rows.
- **Account** `availableCents` = the mapped external account's raw Bridge `available-balance` (null when unmapped or not reported);
  `pendingOutCents` = Σ |amount| of bank rows on the account with `pending = true`, amount < 0 and status `pending|confirmed`;
  `linked` = an external account is mapped to it. Spendable (`/week` `spendableNowCents`, `/payperiod` `spendableCents`) =
  Σ over active checking + cash accounts of `availableCents ?? balanceCents`. Past-week starting balances still use snapshots.
- **Matcher order** (v5): recurring (90/95) → detected pair (80) → learned transfer payee (`payee_rules.is_transfer`, 85) → learned
  category (70 + hits) → transfer keyword (60) → category keyword (40). Keywords (normalised, whole words): `transfer`, `xfer`,
  `online transfer`, `transfer to|from`; money in only: `payment thank you`, `autopay payment`, `online pmt`. Anything mentioning
  `zelle`, `venmo`, `cash app`/`cashapp`, `square cash` or `paypal` is never a keyword transfer. Bulk `confirm-suggested` keeps its ≥ 70
  floor, so 60 is never bulk-applied; a bulk-ignored transfer suggestion gets `ignoreReason: 'transfer'` (no peer).
- **Auto-ignore** (`households.auto_ignore_transfers`, default true; SimpleFIN syncs **and** CSV imports): every detected pair that
  involves a new row, where both rows are still to review and neither has an occurrence suggestion, becomes `ignored / transfer`, peers
  linked, `transfer_auto = true`; new rows suggested by a learned transfer payee (85) become one-sided auto transfers. One
  `transfer.auto` event per sync/import (`data: { count, pairs, oneSided, transactionIds }`, actor null). Off → they stay suggestions.
- **`POST /inbox/{id}/transfer`**: `peerId` must be another visible row of the household on a different account (an unmapped row
  counts as different), opposite sign, `|amount|` within 1 % (≥ 1 cent), ≤ 5 days apart, not paired with a third row → else
  `400 errors.peerId`. Any previous state of both rows is reversed first. No peer = one-sided; the row's normalised payee is saved with
  `is_transfer = true` (an explicit category/item confirmation of that payee later clears the flag). Re-sending the same state is a
  no-op `200`. Activity `transfer.paired` (`data.oneSided`). Dropped/superseded rows → `400 errors.status`.
- **`POST /inbox/{id}/unmatch`** works for every status (incl. dropped/superseded rows, which return to pending); already pending =
  no-op `200`. A transfer writes `transfer.unpaired` (the peer returns to pending too), anything else `transaction.unmatched`. The
  learned transfer flag is kept. v3 `restore` keeps its `400` for already-pending rows.
- **Candidates** `GET /inbox/{id}/candidates?days=30` (1–90, else 400): occurrences of active items due within ±days, sign-compatible
  only, `score = round(50·amount + 30·date + 20·rule)` clamped 0–100 where `amount = max(0, 1 − diff / max(50 %, $10))`,
  `date = max(0, 1 − |days| / (window + 1))`, `rule` = the row's learned payee rule names the item; an occurrence more than
  max(50 %, $10) off is left out unless the rule names it. The occurrence this row already pays is always listed (`paidByThis`). Paid
  occurrences are listed with `paid` set. Max 20, best first. `transferPeers` = rows that would pass the transfer peer check
  (already paired to this row first), max 20.
- **`POST /inbox/{id}/match`** (any status): `400 errors.itemId` sign/kind, `400 errors.dueDate` not an occurrence, `400 errors.status`
  for dropped/superseded rows, `409` (problem title "Occurrence already paid") when the occurrence is paid by another transaction
  (v7: a tick by hand or by autopay is taken over by this transaction, no 409; confirm-as-occurrence likewise). Matching the occurrence the row already pays is a no-op. Payment: `source 'bank'`, `auto false`, `paidBy` = caller,
  amount = |bank amount|; learns payee → item. Event `transaction.matched` (`data.previousStatus`, `reversedExpense`, `reversedPayment`,
  `unpairedPeerId`).
- **`GET /accounts/{id}/transactions`**: `filter` all (default) | pending (bank `pending = true`) | review (status pending) | matched
  (status confirmed + manual expenses) | transfers (`ignored / transfer`); manual expenses (`kind: 'manual'`, `source: 'manual'`, id =
  expense id, amount negative, status confirmed, `expense` set) appear under all and matched, except expenses created by confirming a
  bank row. `q` (≤ 100 chars) is a parameterised `ILIKE` over description / payee / memo (expense name / note) with `%`, `_` and `\`
  escaped. `month=YYYY-MM`, keyset `before` (same cursor as the inbox), `limit` ≤ 200. Hidden rows only with `includeHidden=1` (all
  filter). `totals` cover the whole filtered set (not just the page); `pendingOutCents` there counts pending outflows with status
  pending|confirmed. Unknown/archived account → 404; bad filter/month/cursor → 400.
- **`POST /recurring/from-transaction`**: unknown `transactionId` → `400 errors.transactionId`; `kind` defaults to bill (money out) or
  income (money in) and must match the sign (`400 errors.kind`); `frequency` defaults to monthly; name defaults to payee ?? description
  (≤ 80); category = the row's confirmed-expense or suggested category (outflows); account = the row's account; `payeeHint` = payee ??
  description (≤ 80, seeds the item payee rule). The row is matched to the occurrence on its own date (previous state reversed).
  One `recurring.from_transaction` event. `201 { item, transaction }`.
- **`GET /recurring/{id}/payments?limit=12`** (1–60): the next occurrence after today (unpaid) first, then the last `limit` occurrences
  on or before today (an ended item counts back from its end date), newest first. `transaction` = the confirmed row paying that date.
- **`GET /recurring/{id}/occurrences/{date}/candidates?days=10`** (1–60): visible rows within ±days that are to review or confirmed as an
  expense (not paying an occurrence, not transfers), ranked by the candidate score. 404 when `{date}` is not an occurrence.
- **`POST /recurring/{id}/occurrences/{date}/link`** = `match` from the other side: `400 errors.transactionId` unknown / dropped /
  sign mismatch, `409` already paid, `200 Occurrence`.
- **`GET /transfers?month=`** (default this UTC month): pairs are dated and counted by their outflow leg (a pair whose outflow is in
  another month is not listed); `flows`/`netByAccount` cover pairs and one-sided rows (one-sided rows flow to/from "Other account",
  `null` id); `TransferPair.id` = the outflow row id; `from/to.accountId` may be null for an unmapped SimpleFIN row.
- `PATCH /couple { autoIgnoreTransfers }` must be a boolean (`null` → 400); the `couple.settings` event lists "auto-ignore transfers".
- **Migration 0005**: `imported_transactions.ignore_reason` (CHECK manual|transfer|dropped|superseded), `transfer_peer_id`
  (self-FK `ON DELETE SET NULL`), `last_seen_at`, `transfer_auto`; indexes `(household_id, account_id, posted_on DESC, id DESC)`,
  `(transfer_peer_id)` and a partial `(external_account_id, posted_on) WHERE pending`; `payee_rules.is_transfer`;
  `households.auto_ignore_transfers DEFAULT true`; existing ignored rows backfilled `ignore_reason = 'manual'`.

---

# v6 additions

```ts
// RecurringItem gains: bankNames: string[] (user), bankNamesLearned: string[] (read-only), amountVaries: boolean   (payeeHint stays read-only = bankNames[0] ?? null)
// Household gains: autoMatchLevel: 'off' | 'bills' | 'all'
// InboxItem gains: matchScore: number | null; matchDetail: { name: number; amount: number; date: number; reason: string; engine: 'fuzzy' | 'builtin' } | null
type PendingGroup = { accountId: string; accountName: string; institution: string; balanceCents: number; availableCents: number | null;
  pendingCount: number; pendingOutCents: number; pendingInCents: number; items: InboxItem[] };
type PendingView = { totalOutCents: number; totalInCents: number; count: number; groups: PendingGroup[] };
```

| Method | Route | Body / Query | Response |
|---|---|---|---|
| POST/PUT | `/v1/recurring` | `{ …, bankNames?: string[] (≤ 10, each 2–80 chars), amountVaries?: boolean }` | `RecurringItem` |
| DELETE | `/v1/recurring/{id}/bank-names/learned/{index}` | — | `200 RecurringItem` (forget a learned name) |
| GET | `/v1/pending` | — | `200 PendingView` (accounts with pending rows first, by pendingOutCents desc) |
| POST | `/v1/inbox/rematch` | — | `202 { queued: true }` (re-score every To-review row now; rate-limited to 1/min) |
| GET | `/v1/matcher/status` | — | `200 { engine: 'fuzzy' \| 'builtin', reachable: boolean, version: string \| null, lastRunAt: string \| null, lastAutoMatched: number }` |
| PATCH | `/v1/couple` | `{ autoMatchLevel? }` | `200 Household` |
WeekView gains `pendingOutCents`. Activity kinds: `transaction.automatched` carries `data.reason`; new `transaction.autocategorized`.

### v6 simplicity additions
```ts
type BillSuggestion = { id: string; name: string; kind: Kind; amountCents: number; amountVaries: boolean; frequency: Frequency;
  nextDue: string; bankName: string; score: number; sampleCount: number; lastSeen: string };
type TransactionFeed = { pending: InboxItem[] /* all in-progress, every account */; items: InboxItem[]; nextBefore: string | null;
  toReviewCount: number };
```
| Method | Route | Body / Query | Response |
|---|---|---|---|
| GET | `/v1/transactions` | `?accountId=&q=&before=&limit=50` | `200 TransactionFeed` — `pending` always complete (first page only), `items` posted rows + manual expenses, newest first |
| GET | `/v1/bill-suggestions` | — | `200 BillSuggestion[]` (status open, best first) |
| POST | `/v1/bill-suggestions/{id}/accept` | `{ name?, kind?, amountCents?, frequency?, nextDue? }` (overrides) | `201 { item: RecurringItem; matched: number }` — creates the item with `bankNames=[bankName]` and matches the sample transactions |
| POST | `/v1/bill-suggestions/{id}/dismiss` | — | `204` (never suggested again) |
`POST /v1/recurring` is what the quick-add sheet calls (the parser is client-side); it already accepts every field the preview produces.

## v6 implementation notes (API)

Deviations and clarifications from the "# v6 additions" tables, as built in `apps/api`:

- **Migration 0006** (`0006_matcher.sql`): `recurring_items.bank_names text[]` (backfilled with the trimmed `payee_hint`),
  `bank_names_learned text[]`, `amount_varies`; `households.auto_match_level` (CHECK off|bills|all, default `all`);
  `imported_transactions.match_score smallint`, `match_detail jsonb`, plus **`auto_blocked boolean`** (see below); `bill_suggestions`
  as in PLAN-V6 plus **`last_seen date`** (newest sample, for `BillSuggestion.lastSeen`) and **`item_id`** (the item an accepted
  suggestion became), `UNIQUE (household_id, fingerprint)`.
- **Bank names.** `POST/PUT /v1/recurring` store `bankNames` trimmed with case-insensitive duplicates dropped (first spelling wins);
  more than 10 (after dedupe) or any name outside 2–80 characters → `400 errors.bankNames`. When `bankNames` is absent the legacy v4
  `payeeHint` becomes the only name (blank or absent → none, so a v4 `PUT` without a hint still clears it; its 80-character limit stays
  `errors.payeeHint`); an explicit `bankNames` (even `[]`) wins over `payeeHint`. PUT replaces the list (and `amountVaries`, default
  false). `payeeHint` in `RecurringItem` = `bankNames[0] ?? null`; the `payee_hint` column is no longer written. Every user bank name
  seeds `payee_rules(payee_norm(name), item)` like the v4 hint did (removed names drop their rule unless a remaining user or learned name
  normalises the same), so the built-in fallback can still auto-apply them. `/recurring/from-transaction` sets `bankNames = [payee ??
  description]`.
- **Learning.** Every confirmed occurrence match — confirm, match, link, from-transaction, found-bill accept, auto-match — upserts the
  payee rule payee → item (v3) and appends the transaction's normalised payee (TransactionMatcher's normaliser on `payee ?? description`,
  upper-cased, ≤ 80 chars, e.g. "ACH DEBIT OAKWOOD PROPERTY MGMT RENT" → "OAKWOOD PROPERTY MGMT RENT") to `bankNamesLearned` unless a
  name with the same normalised form is already in either list; at most 10 learned names, the oldest evicted first; user names are never
  changed. `DELETE /recurring/{id}/bank-names/learned/{index}` (0-based) → `404` when the item or index does not exist; it also drops
  that name's item payee rule unless a remaining name normalises the same, writes `recurring.updated` (`data.forgotLearnedName`) and
  queues a rematch.
- **Matching pipeline** (`Matching/MatchingService.cs`). Runs on the rows a SimpleFIN sync or a **CSV import** inserted (v6: CSV imports
  now auto-apply too), on every To-review row in the scheduler sweep, and on the immediate rematch. Built-in pass first (it is the
  fallback, detects transfer pairs and fills category gaps); rows whose built-in suggestion is a detected pair (80) or a learned transfer
  payee (85) are **never sent** to the matcher and keep the v5 transfer semantics (auto-ignored only for pairs involving a new row, only
  when `autoIgnoreTransfers` is on **and** the level is not `off`). The `/v1/match` request: `today` = household-local date,
  `options = { autoLevel, autoThreshold: 85, suggestThreshold: 55 }`, transactions (description / payee / memo cut to 256 chars), every
  occurrence of an active item due within [earliest − 10, latest + 10] days — paid ones included with `paid: true` (the matcher's
  "only candidate with that exact amount" rule counts them) — with `names = [item name, ...bankNames, ...bankNamesLearned]` (deduped)
  and `amountVaries`, and `categoryRules` from `payee_rules` with a category that are not transfer rules (`payee` = `payee_norm`).
  Requests are split into chunks of ≤ 1000 transactions carrying only the candidates within ±10 days of one of their transactions
  (≤ 2000; a chunk over that is halved) and merged back (one match per transaction and per occurrence, best score wins).
- **What is stored.** A matcher occurrence match → occurrence suggestion with `confidence = matchScore = score` and `matchDetail =
  { name: nameScore, amount: amountScore, date: dateScore, reason, engine: 'fuzzy' }`; a category match → category suggestion with
  `confidence = matchScore = score`, `matchDetail = { name: score, amount: 0, date: 0, reason, engine: 'fuzzy' }`; neither → the built-in
  non-occurrence suggestion (transfer keyword 60, learned category 70 + hits, keyword 40; categories carry an `engine: 'builtin'` detail,
  transfers / none have `matchScore` and `matchDetail` null). With the built-in engine the v5 suggestions are kept and occurrence /
  category ones get a built-in detail computed with the contract's formulas (amount and date scores; name = 100 when a learned rule or
  bank name names the item, else significant-token overlap; reason like "name 100% · amount exact · 2 days early · learned payee").
  A sweep does not rewrite rows whose suggestion, score and detail are unchanged. Detail and score are kept after confirmation (the UI
  shows them on auto rows).
- **Automatic actions.** Occurrence: the matcher's `auto` (fuzzy) or, with the built-in engine, the v3 rule (confidence ≥ 90 and a
  learned rule / bank name naming the item), only at level `bills|all`: the occurrence is paid with the bank amount (`paid_by NULL`,
  `auto true`, `source bank`; skipped when already paid from a bank transaction — v7: a tick by hand or by autopay is taken over), the row is confirmed with `auto`, learning applies, and
  `transaction.automatched` is written with `data: { transactionId, itemId, dueDate, amountCents, auto, reason, score, engine }`.
  Category: the matcher's `auto` at level `all` for money out only: an expense with `deduct = false` (created by the link's creator on a
  sync, the importer on a CSV import, the household's first member on a sweep), the row confirmed with `confirmed.auto = true` and no
  reviewer, `transaction.autocategorized` (`data: { transactionId, expenseId, categoryId, amountCents, reason, score, engine }`). The
  built-in engine never auto-categorises. `SyncResult.autoMatched` counts both; `bank.synced` data gains `autoMatchedOccurrences`,
  `autoCategorized`, `engine`; `import.csv` data gains `autoMatched`, `autoCategorized`, `autoIgnoredTransfers`, `engine`. The "N to
  review" push links `/transactions`.
- **`auto_blocked`.** A row a person sends back to review — `restore`, `unmatch`, or marking its occurrence unpaid — is never
  auto-applied again (its suggestion still refreshes), so a sweep cannot undo an Undo.
- **Fallback.** Not configured (empty `Matcher__BaseUrl` / `Matcher__Key`), 401/403, 413, 400/422, 5xx, timeouts
  (`Matcher__TimeoutSeconds`, 30), connection errors and bodies that are not the contract's JSON all mean "use the built-in engine"; a
  sync or import never fails because of the matcher. The warning (category `CouplesCash.Api.Matching.MatchingService`) is logged at most
  once a minute and names only the failure kind and household id — never descriptions or names.
- **Rematch.** Scheduler phase `rematch` re-scores every household with To-review rows whose last sweep in this process is at least
  `Matcher__RematchMinutes` (30) old (a fresh process sweeps on its first tick). Immediate rematch (background `MatchingWorker`,
  `Matcher__DebounceSeconds` = 10 s after the last trigger, at most 60 s after the first) after `POST/PUT /recurring`, forgetting a
  learned name, `PATCH /couple { autoMatchLevel }`, accepting a found bill and `POST /inbox/rematch`; runs of one household are
  serialised. `POST /v1/inbox/rematch` → `202 { queued: true }` (Location `/v1/matcher/status`); a second request within a minute (per
  household, in memory) → `429` problem with `Retry-After` and a `retryAfter` extension.
- **`GET /v1/matcher/status`** adds `configured`, `lastRunEngine`, `foundBills` (found bills are running) and `note` (why the built-in
  engine is used). `engine` is `fuzzy` only when the matcher is configured, `/health` answers and the last real call did not fail (a
  401 is only cleared by a successful call; a network failure also by a good probe). The probe (3 s timeout) is cached 15 s, an
  "unreachable" result 3 s. `lastRunAt` / `lastAutoMatched` (occurrences + categories of the household's last run: sync, import or
  sweep) are kept in memory and reset on restart.
- **Found bills.** Input: the household's imported rows of the last 180 days (household-local), without transfers and dropped /
  superseded rows, newest 3000; `knownNames` = names, bank names and learned names of active items. Runs (debounced) after a sync or CSV
  import that inserted rows, and daily (scheduler phase `found-bills`). Fingerprint = `sha256(payee_norm(bankName) + '|' + frequency)`;
  open suggestions are inserted / refreshed, accepted and dismissed fingerprints are never touched again, open suggestions the latest
  run no longer reports are removed. Without the matcher detection is skipped (no built-in detector) and `/matcher/status.foundBills` is
  false. Detection itself writes no activity. `GET /bill-suggestions` lists open ones by score, then `lastSeen` (newest sample).
- **Accept.** `404` unknown id; `409` ("Suggestion already handled") when already accepted or dismissed; overrides are validated (name
  1–80, positive amount, enums) and a `kind` that flips the direction of the payments → `400 errors.kind`. The item gets the detected (or
  overridden) name / kind / amount / frequency, `amountVaries` from detection, `bankNames = [bankName]`, the samples' most common account
  and (money out) most common confirmed/suggested category, and `anchorDate` = `nextDue` stepped back whole periods until it is on or
  before the earliest sample (month steps keep the day of month). Each sample still to review or confirmed as an expense is then matched
  (v5 match semantics: an expense is reversed) to the item's nearest occurrence within half a period (weekly 3, biweekly 6, monthly 10,
  quarterly 15 days); `matched` counts them. Activity `recurring.created` with `data: { itemId, suggestionId, matched, fromSuggestion:
  true }`; a rematch is queued. **Dismiss** → `204` (again → `204`), accepted → `409`; activity `bill_suggestion.dismissed`.
- **`GET /v1/pending`**: bank rows with `pending = true` and status pending|confirmed (the same rows as `Account.pendingOutCents`), not
  hidden, newest first. Groups: every active linked account (also without pending rows) and every account with pending rows; rows of a
  bank account that is not mapped form a group with **`accountId: null`** (name, institution and raw balances of the external account);
  rows whose account is archived go to an `"Other"` group (`accountId: null`). Groups with pending rows first, by `pendingOutCents`
  descending, then by name.
- **`GET /v1/transactions`**: `accountId` (unknown / foreign → `400 errors.accountId`), `q` (≤ 100, parameterised ILIKE as v5), `before`
  (inbox cursor), `limit` ≤ 200. `pending` = every visible bank row still pending at the bank (any status), only when `before` is absent
  (≤ 1000); `items` = rows no longer pending (any status, dropped / superseded hidden) ∪ manual expenses not created from a bank row, as
  `AccountTransaction` (InboxItem + `kind`); `toReviewCount` = household-wide rows to review.
- **`WeekView.pendingOutCents`** = Σ `Account.pendingOutCents` over the active checking + cash accounts (the accounts Spendable counts).
- **`PATCH /v1/couple { autoMatchLevel }`**: off|bills|all (`null` or anything else → `400 errors.autoMatchLevel`); `couple.settings`
  lists "auto-match level" with `data.autoMatchLevel`; a rematch is queued.
- The built-in `TransactionMatcher` honours `amountVaries` (±10 days, max(35 %, $15)). OpenAPI tags: `Matching` (status, rematch),
  `Transactions` (`/transactions`, `/pending`), `Bill suggestions`. Env: `Matcher__BaseUrl`, `Matcher__Key`, `Matcher__TimeoutSeconds`
  (30), `Matcher__RematchMinutes` (30), `Matcher__DebounceSeconds` (10), `Matcher__ChunkSize` (1000), `Matcher__MaxCandidates` (2000).
  The matcher `HttpClient` never uses an outbound proxy and recycles connections every 2 minutes.

---

# v7 additions (Friday plan)

```ts
type AccountRole = 'bills' | 'savings' | 'fun' | 'other';
// Account gains: role: AccountRole; ownerUserId: string | null; ownerName: string | null
// Household gains: paydayWeekday: 0..6   (weekStart is derived = paydayWeekday; kept for compatibility)
// Kind gains 'transfer'; RecurringItem gains: fromAccountId, toAccountId (string | null), fromAccountName, toAccountName
type PlanLine = { kind: 'bill' | 'subscription' | 'income' | 'transfer' | 'oneoff'; occurrence: Occurrence };
type ChecklistItem = { key: 'paycheck' | 'fun'; label: string; amountCents: number; done: boolean;
  auto: boolean; transactionId: string | null; itemId: string | null; dueDate: string | null };
type Sweep = { weekStart: string; plannedCents: number; actualCents: number | null; doneAt: string | null; auto: boolean };
type FeedStats = { pending: number; posted: number; availableReported: boolean; at: string } | null;
type Plan = {
  start: string; end: string; nextPayday: string; isCurrent: boolean; isPayday: boolean; today: string;
  billsAccount: { id: string; name: string; availableCents: number; balanceCents: number; availableFromBank: boolean;
                  pending: InboxItem[]; pendingOutCents: number; feed: FeedStats } | null;   // null → setup needed
  accounts: { id: string; name: string; role: AccountRole; ownerName: string | null; balanceCents: number; availableCents: number | null }[];
  bills: PlanLine[]; funMoney: PlanLine[]; oneOffs: PlanLine[]; income: PlanLine[];
  billsOutCents: number; funOutCents: number; oneOffOutCents: number; incomeInCents: number;  // unpaid remainders
  leftAtEndCents: number;        // availableCents − bills − fun − one-offs (+ unpaid income only when it lands before end)
  savingsBills: PlanLine[];      // e.g. student loans from savings — shown, not in the math
  checklist: ChecklistItem[];    // payday only (empty otherwise): paycheck and fun money
  sweep: Sweep | null; toReviewCount: number; setup: { banksLinked: boolean; rolesSet: boolean; paycheckKnown: boolean; billsAdded: boolean; partnerJoined: boolean } };
```

| Method | Route | Body / Query | Response |
|---|---|---|---|
| GET | `/v1/plan` | `?date=YYYY-MM-DD` (any day of the pay week; default today) | `200 Plan` |
| POST | `/v1/plan/sweep/done` | `{ weekStart, actualCents? }` | `200 Sweep` (manual tick; auto-detection may later attach the transaction) |
| DELETE | `/v1/plan/sweep/done` | `{ weekStart }` | `200 Sweep` |
| PUT | `/v1/accounts/{id}/role` | `{ role, ownerUserId? }` (setting `bills` moves the role off the previous bills account) | `200 Account` |
| PATCH | `/v1/couple` | `{ paydayWeekday? }` | `200 Household` |
| POST | `/v1/recurring/bulk` | `{ items: RecurringRequest[] }` (≤ 100; all-or-nothing validation, per-index errors `items[3].amountCents`) | `201 { items: RecurringItem[] }` |
| POST/PUT | `/v1/recurring` | `kind: 'transfer'` requires `fromAccountId` and `toAccountId` (different, both in household) | `RecurringItem` |
| POST | `/v1/couple/reset` | `{ confirm: "RESET" }` (case and spaces ignored) | `200 Household` (settings back to defaults) · `400 errors.confirm` without the word · `409` "No household" |
| GET | `/v1/banks` | — | `BankLink.accounts[].lastFeed: FeedStats` |
Transactions on `fun` accounts never enter To review (status confirmed, `ignoreReason` null, no suggestion) but appear in
the account's transaction list. Activity kinds added: `sweep.done`, `sweep.auto`, `transfer.planned_paid`. Push tag
`payday`. Week endpoints (`/v1/week`) use the pay week (payday-anchored) from v7 on.

## v7 implementation notes (API)

Deviations and clarifications from the "# v7 additions" table, as built in `apps/api`:

- **Migration 0007** (`0007_friday.sql`): `accounts.role` (CHECK bills|savings|fun|other, default `other`), `accounts.owner_user_id`
  (`ON DELETE SET NULL`), partial unique index `(household_id) WHERE role = 'bills' AND archived_at IS NULL` (an archived account keeps its
  role but never blocks a new bills account); `households.payday_weekday` (0–6, default 5) backfilled from `pay_anchor_date`'s weekday when
  `pay_period` is weekly/biweekly; `recurring_items.from_account_id` / `to_account_id` and `recurring_items_kind_check` replaced to allow
  `transfer`; `sweeps` as in PLAN-V7 plus **`left_cents`** (the raw, possibly negative, left-at-end the sweep was frozen from) and
  **`auto_blocked`** (a person unticked it); `external_accounts.last_feed jsonb`; `week_summary_log.kind` adds `payday`. Role backfill for
  existing households: the linked (mapped) active checking account with the most confirmed bill/subscription payments → `bills` (ties →
  largest balance, then id), `savings`-type accounts → `savings`, everything else stays `other`.
- **Pay week.** `WeekEngine.WeekBounds` accepts any weekday 0–6; `/v1/week` (and the calendar grid, through `Household.weekStart`) is
  anchored to `payday_weekday`: `[payday, payday + 6]`. `Household.weekStart` and `WeekView.weekStart` now return `paydayWeekday`;
  `PATCH /couple { weekStart }` is still validated (0/1) and stored in the legacy column but drives nothing. `PATCH /couple
  { paydayWeekday }`: integer 0–6 (`null` or anything else → `400 errors.paydayWeekday`); `couple.settings` lists "payday" with
  `data.paydayWeekday`.
- **Spendable (the "available now" fix).** When the household has a `bills` account, `/v1/week` `spendableNowCents`, `/v1/payperiod`
  `spendableCents` and the plan use **only** that account: bank `availableCents` when reported, else `balanceCents − pendingOutCents`.
  Other checking/cash accounts (fun money) are never summed. Without a bills account the v6 rule (Σ checking + cash of `availableCents ??
  balanceCents`) is kept, so nothing changes before setup. `WeekView.pendingOutCents` follows the same accounts.
- **Pending rows (the "only one pending" bug, fixed).** `Account.pendingOutCents`, `GET /v1/pending` and the account-transactions
  `totals.pendingOutCents` counted only pending rows whose review status was `pending|confirmed`. A pending row that was **ignored** — in
  particular a v5 auto-ignored transfer pair while both legs are still pending (the Friday $20 fun-money transfers, a pending move to
  savings) or a manually ignored authorization — vanished from the pending view and from the pending total although the money is leaving
  the account. v7 counts and lists every bank-pending row that is not dropped / superseded, whatever its review status (`GET /v1/transactions`
  already did). Nothing in the parser → reconcile → fingerprint → upsert path collapses distinct pending rows: distinct ids give distinct
  fingerprints (two same-merchant same-amount authorizations stay two rows), v2 `posted: 0` and `transacted_at`-only rows parse as pending,
  the `ON CONFLICT … WHERE pending` refresh only applies to the same id, the reconciler only pairs a *posted* new id with an *absent*
  pending row and supersedes only an absent authorization whose posted copy is present, and rows of an unmapped bank account (`account_id
  NULL`) are listed (grouped by their external account in `/pending`). Only a repeated id inside one response collapses (first wins), as
  SimpleFIN ids are unique per account. Covered by `V7Tests` (parser / reconciler) and `V7DbTests` (five pending rows stored and listed
  by every endpoint, real database).
- **Feed diagnostics.** Every successful sync stores, per external account in the response (mapped or not), `last_feed = { pending,
  posted, availableReported, at }`: the raw counts of transactions the Bridge sent (after parsing, so a v2 `posted: 0` row counts as
  pending; before any dedupe or reconciliation) and whether it sent `available-balance`. `GET /v1/banks` → `accounts[].lastFeed`
  (`null` until the first v7 sync); the plan's `billsAccount.feed` is the mapped external account's.
- **Account roles.** `PUT /v1/accounts/{id}/role { role, ownerUserId? }`: unknown/archived/foreign account → 404; `role` required
  (`400 errors.role`); `ownerUserId` must be a household member (`400 errors.ownerUserId`; `null`/absent clears the owner; any role may
  carry one). Setting `bills` moves the role off the previous active bills account (to `other`) in the same transaction. Setting `fun`
  confirms the account's rows still to review (suggestion cleared). One `account.updated` event (`data: { accountId, role, ownerUserId,
  previousBillsAccountId, confirmedFromReview }`); a rematch is queued. `Account` gains `role`, `ownerUserId`, `ownerName`; `POST/PUT
  /accounts` leave the role alone.
- **Fun accounts never enter To review.** New SimpleFIN rows (and CSV rows) on a `fun` account are inserted `status: confirmed` with
  nothing attached; mapping an external account onto a fun account confirms its rows to review; every path that sends a row "back to
  review" (restore, unmatch, unpairing a transfer peer, un-marking an occurrence) returns a fun-account row to `confirmed` instead. They are
  therefore never matched, rematched, bulk-confirmed or counted in `toReviewCount` / inbox summaries, and they stay in the account's
  transaction list and in `/v1/transactions`. A fun-account inflow can still become the other leg of a planned transfer (below).
- **Recurring transfers** (`kind: 'transfer'`): `fromAccountId` and `toAccountId` required, different (`400 errors.fromAccountId` /
  `errors.toAccountId`), both active accounts of the household (`400` with the same keys); `accountId` is stored as the from-account; other
  kinds never store from/to. `RecurringItem` gains `fromAccountId`, `toAccountId`, `fromAccountName`, `toAccountName`; `GET
  /recurring?kind=transfer` filters. Transfers are never spending, budgets or income: `Summary.byKind` keeps its three entries, budgets /
  month report / reminders / pay-period outflows ignore them, the v4 week totals skip them and `leftAtEnd` (v4) subtracts unpaid ones.
  They are never sent to the matcher nor used by the built-in matcher; `POST /recurring/from-transaction` and found-bill accept refuse
  `kind: 'transfer'` (`400 errors.kind`). A manual `match` of a money-out row to a transfer occurrence is allowed (v5 semantics).
- **Planned-transfer matching** (`FridayService`, run in the sync / CSV import transaction before the matcher scores new rows, in the
  immediate rematch and in the scheduler's `friday` phase every tick). Unpaid occurrences due `[today − 14, today + 2]` (household-local) of
  transfer items match bank rows on the from-account: money out, `| |amount| − planned | ≤ $1.00`, posted within ±2 days of the due date;
  the row is still to review, or an ignored transfer not yet linked to a plan, never `auto_blocked`, never attached to a sweep. Evidence,
  strongest first: its existing transfer peer is on the to-account; a money-in row on the to-account within $1 and ±2 days of the outflow
  that is unpaired (to review, confirmed with nothing attached — fun money — or a one-sided transfer); or — when the to-account has no bank
  feed and the row reads like a transfer (keyword, learned transfer payee, transfer suggestion), or when the row names the to-account (a
  significant word of its name / institution, e.g. "ALLY") — one-sided. Best evidence, then closest date, then closest amount; one-to-one.
  Then: the occurrence is paid (`paid_by NULL`, `auto true`, `source bank`, bank amount), both rows become `ignored / transfer` (auto, peers
  linked), the outflow records the occurrence in `confirmed_item_id/confirmed_due_date` (so `InboxItem.match` is set on that transfer row
  and `/recurring/{id}/payments` shows it), and `transfer.planned_paid` is written (actor null, `data: { itemId, dueDate, transactionId,
  peerId, amountCents, plannedCents }`). A one-sided match is completed later when the inflow shows up. Unmatching / unpairing the row
  un-pays the occurrence; marking the occurrence unpaid by hand keeps the rows as transfers, drops the link and blocks them from automatic
  matching (`occurrence.unpaid` `data.unlinkedTransactionIds`).
- **`GET /v1/plan?date=`** (`PlanService` + pure `PlanEngine`): the pay week containing `date` (default household-local today); a bad
  date → 400. Lines are the occurrences due in `[start, end]`, sorted by date then name, classified: bills / subscriptions on the bills
  account or unassigned → `bills` (`frequency: once` → `oneOffs`, line kind `oneoff`); on a `savings` account → `savingsBills` (shown,
  never in the math); on any other account → not listed. Income on the bills account or unassigned → `income`. Transfers: from the bills
  account to a `fun` account → `funMoney`; from the bills account elsewhere → `bills` (line kind `transfer`); into the bills account →
  `income`; between two other accounts → not listed. Before setup (no bills account) every account that is not savings / fun counts as the
  bills side. Lines paid from a bank transaction (`paid.source = 'bank'`) are listed (ticked) and not counted — their money is
  already in `available`; lines ticked by hand or by autopay are listed as paid but **keep counting** until their bank transaction
  arrives and takes the payment over, so a tick never moves `leftAtEndCents` (only the bank does, by the paid − planned difference). `billsOutCents` / `funOutCents` / `oneOffOutCents` / `incomeInCents` are
  the unpaid planned amounts; **`leftAtEndCents = available − billsOut − funOut − oneOffOut + incomeIn`** where unpaid income counts only
  when due inside the pay week (≤ end = the day before the next payday): the next payday's paycheck belongs to the new week, and this
  week's paycheck, once it posts, is paid and already in available — it is never added twice. For a past week `leftAtEndCents` is the
  number frozen for its own sweep (`left_cents`, its Left on Thursday before the move) when there is one; a future week uses today's available (a rough
  projection). `billsAccount`: `availableCents` as above,
  `availableFromBank` = the bank reported it, `pending` = every visible bank-pending row of the account (any review status, newest first),
  `pendingOutCents` = Σ of their outflows, `feed` = last feed stats; `null` when no account has role `bills`. `accounts` = every active
  account ordered bills, savings, fun, other, then name. `setup`: `banksLinked` (a bank link exists), `rolesSet` (a bills account exists),
  `paycheckKnown` (an active income item), `billsAdded` (an active bill / subscription), `partnerJoined` (two members). `toReviewCount` =
  household rows to review. `sweep` = this week's sweep row, null until its sweep day (`Sweep` gains `leftCents`, `transactionId`).
  **Start over (`POST /couple/reset`).** One transaction under a row lock on the household: deletes every account (balance history
  cascades), recurring item (payments, skips, reminders), bank link and external account (stored credentials included — the SimpleFIN
  connection on the Bridge stays; linking again needs a new setup token), imported transaction, CSV import, sweep, payee rule, bill
  suggestion, expense, budget, goal, to-do, category and activity event of the household; settings go back to their defaults
  (`paydayWeekday 5`, `autoMatchLevel all`, `autoIgnoreTransfers true`, `payPeriod monthly`, no anchor / weekly target / default account;
  name, currency and time zone stay) and the default categories are seeded again. Members, logins, refresh tokens, passkeys, push
  subscriptions and notification preferences are untouched, so both partners stay signed in and coupled. The new feed holds one event,
  **`household.reset`** (actor = caller). Either member may do it.
  **Fetch window (v7).** A sync covers `today − 30` (`− 90` on the first sync) … `today + 14` (exclusive): some banks put the
  expected post day in a pending charge's `posted`, and `end-date` is midnight UTC, so a window ending tomorrow missed both. Each Bridge
  request is at most 44 days (the Bridge warns in `errors` above 45 days and cuts ranges over 90): a regular sync is one request; the
  first sync adds older history requests without `pending` (best effort — a failed history request is logged and skipped). A pending
  charge is dated by `transacted_at` when present and never after today.
- **Payday checklist** (only for the current pay week; shown on payday and, while an item is open, through payday + 3): `paycheck` =
  income lines due on payday ("Paycheck arrived · $1,500.00", amount = paid amount when paid, `done` = paid, `auto` = the payment is
  automatic, `transactionId` = the paying row); `fun` = fun-money lines due payday … payday + 3 (weekly ones on payday, a monthly one when
  its date falls in that window; label "$20.00 → Ally Spending (Travis)" = amount → to-account name, plus the owner when the name does not already say it). The savings move is not on the checklist (it is the plan's bottom line on the sweep day). `dueDate` = the occurrence date.
- **Sweep (the bottom line, on the last day of the pay week).** What is left on Thursday moves to savings on Thursday, so the sweep is
  never a line in the calculation and never taken out of `leftAtEndCents`. Scheduler phase `sweep-freeze` (first phase of every tick):
  on the sweep day = the pay week's last day (`end`, household-local) every household with a bills account and no sweep row for the week
  gets one: `left_cents` = the week's `leftAtEndCents` at that moment, `planned_cents = max(0, left_cents)`; `INSERT … ON CONFLICT DO
  NOTHING`; activity **`sweep.planned`** (actor null, `data: { weekStart, plannedCents, leftCents }`). No catch-up: when the API was down
  all that day nothing is frozen and the money carries into the next week. Until it is done, the sweep day's `GET /plan` keeps
  `planned_cents` / `left_cents` equal to the live `leftAtEndCents` (a bill can change during the day); the bank row that moves it is
  detected in the same transaction that imports it. Auto-detection (same pass as planned transfers; this week's sweep and last week's):
  a bills-account outflow posted sweep day − 1 … sweep day + 3 within max(5 %, $5.00) of `planned_cents` (to review or a transfer not
  linked to a plan), backed by a transfer peer on a savings account, a savings-account inflow within 1 % ($1 floor) and ±3 days, or —
  savings without a bank feed and a transfer-looking row, or a row naming a savings account — one-sided; closest amount then closest to
  the sweep day wins. The rows become an auto transfer pair, the sweep gets `actual_cents` = |amount|, `transaction_id`, `done_at`
  (done_by NULL = auto) — or, after a manual tick, only the transaction — and `sweep.auto` (actor null) is written. Never for a zero plan
  or an `auto_blocked` week. Migration `0008_sweep_day.sql` deletes the sweep rows frozen under the old payday rule.
- **`POST /v1/plan/sweep/done { weekStart, actualCents? }`**: `weekStart` required (400), `actualCents` 0…max (default the previous
  actual, else the planned amount); no sweep for that week → `404` problem "No sweep for that week"; ticks by the caller (`auto false`);
  again = updates the amount. **`DELETE /v1/plan/sweep/done`** takes `{ weekStart }` in the body or `?weekStart=` (missing → 400, unknown
  week → 404): clears actual / done / transaction and sets `auto_blocked`, so the bank-feed detection never re-ticks that week. Both write
  `sweep.done` (`data.done` true/false, `actualCents`, `wasAuto`). Both answer `200 Sweep`.
- **`POST /v1/recurring/bulk { items }`**: 1–100 items (else `400 errors.items`); every item is validated like `POST /recurring` and
  every reference checked before anything is written; errors are keyed `items[i].field` (camelCase, e.g. `items[3].amountCents`,
  `items[1].categoryId`). One transaction creates the items (bank-name payee rules seeded per item), one `recurring.created` event with
  `summary` "added N bills", `entityId` null and `data: { bulk: true, count, itemIds, totalCents }`, and one rematch. `201 { items }` in
  request order (Location `/v1/recurring`).
- **Payday push** (scheduler phase `payday-push`, replaces the v4 week-ahead / week-left summaries, which are no longer sent): on payday
  (household-local) during the user's `hourLocal`, gated by the existing `weekSummaryEnabled` preference, once per (user, payday) via
  `week_summary_log` kind `payday`: title "Payday", body e.g. "Payday · fun money $40.00" (" sent ✓" once every checklist fun transfer is
  paid; dropped when there is none), `url: /week`, `tag: payday`. **Sweep-day push** (same phase, hour and preference): on the pay week's
  last day, when its sweep is frozen, above zero and not done, once per (user, week) via kind `sweep`: title "Savings", body "Move $253.17
  to savings today", `url: /week`, `tag: sweep`. Bill-due reminders are unchanged.
- **Scheduler order per tick**: autopay → reminders → hygiene → sweep-freeze → bank-sync → friday (planned transfers + sweep for every
  household with a bills account or a transfer item) → rematch → found-bills → payday-push. `bank.synced` data gains
  `plannedTransfersPaid`, `sweepsDone`, `toReview` (the "N to review" push now counts rows actually left to review). OpenAPI tag `Plan`.
