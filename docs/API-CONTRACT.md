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
