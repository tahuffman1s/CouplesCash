# Matcher service contract (API ↔ matcher)

Internal HTTP service, `http://matcher:8090` on the compose network (never published, never behind the tunnel).
Every request except `/health` must carry `X-Matcher-Key: <MATCHER_KEY>`; mismatch → `401`. JSON, snake_case is
**not** used — camelCase like the rest of the stack. Money in integer cents, dates `YYYY-MM-DD`. Stateless: nothing is
stored, nothing is logged beyond request counts and timings (never descriptions).

## `GET /health` → `200 { "status": "ok", "version": "x.y.z" }`

## `POST /v1/match`
```jsonc
{
  "today": "2026-10-26",
  "options": { "autoLevel": "all",            // off | bills | all
               "autoThreshold": 85,            // combined score needed to auto-apply an occurrence match
               "suggestThreshold": 55 },
  "transactions": [ { "id": "uuid", "accountId": "uuid|null", "postedOn": "2026-10-24", "amountCents": -185000,
                      "description": "ACH DEBIT OAKWOOD PROPERTY MGMT RENT", "payee": null, "memo": null, "pending": false } ],
  "candidates":   [ { "itemId": "uuid", "dueDate": "2026-11-01", "kind": "bill|subscription|income", "frequency": "monthly|...|once",
                      "amountCents": 185000, "amountVaries": false, "names": ["Rent", "OAKWOOD PROPERTY"], "accountId": "uuid|null",
                      "paid": false } ],
  "categoryRules": [ { "payee": "KROGER", "categoryId": "uuid", "hits": 3 } ]
}
```
Response:
```jsonc
{
  "engine": "fuzzy", "version": "1.0.0",
  "matches": [ { "transactionId": "uuid", "itemId": "uuid", "dueDate": "2026-11-01", "score": 93,
                 "nameScore": 91, "amountScore": 100, "dateScore": 82, "auto": true,
                 "reason": "name 91% · amount exact · 8 days early" } ],
  "categories": [ { "transactionId": "uuid", "categoryId": "uuid", "score": 96, "auto": true, "reason": "payee KROGER (3 confirmations)" } ]
}
```

### Scoring (the matcher owns these rules; the API's built-in C# matcher is the fallback)
- **Normalise** descriptions/payees/names: upper-case; drop `POS`, `DEBIT`, `CREDIT`, `PURCHASE`, `ACH`, `CHECKCARD`,
  `RECURRING`, `PAYMENT`, `ONLINE`, `WEB`, `PPD`, `ID:…`, card fragments (`XXXX1234`, `*1234`), store numbers (`#123`,
  `STORE 0456`), phone numbers, dates, trailing state codes / city names, `.COM` → ` `, payment processor prefixes
  (`SQ *`, `TST*`, `PAYPAL *`, `AMZN MKTP US*xxxx` → `AMAZON`); collapse whitespace.
- **Name score** (0–100) = max over the candidate's names of `max(token_set_ratio, 0.95·partial_ratio, WRatio)` on
  normalised strings (RapidFuzz).
- **Amount score**: sign must agree with kind (outflow ↔ bill/subscription, inflow ↔ income) else the pair is skipped.
  Tolerance = recurring max(2 %, $1); `once` max(15 %, $10); `amountVaries` max(35 %, $15). Exact → 100; inside tolerance
  → linear 100…70; outside → `max(0, 70 − 4·(excess %))`.
- **Date score**: window ±5 days recurring, ±7 once, ±10 amountVaries; 100 at 0 days, linear to 60 at the window edge,
  0 outside (pair skipped). Early payment is not penalised more than late.
- **Combined** = 0.45·name + 0.35·amount + 0.20·date (rounded).
- **Auto** (only when `autoLevel` ≠ `off`): combined ≥ `autoThreshold` AND name ≥ 70 AND amount within tolerance; **or**
  amount exact AND |days| ≤ 3 AND name ≥ 50 AND it is the only candidate with that exact amount in the window.
- **Assignment**: one transaction ↔ one occurrence; greedy by combined score descending across the whole batch; paid
  candidates are never assigned.
- **Categories** (for transactions without an occurrence match): best `categoryRules` entry by name score;
  `auto` when `autoLevel = all`, name ≥ 90 and hits ≥ 2; else a suggestion when name ≥ 75.
- Returned `matches` include only pairs with combined ≥ `suggestThreshold`; one entry per transaction.
