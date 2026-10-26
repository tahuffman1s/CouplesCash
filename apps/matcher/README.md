# CouplesCash matcher

Small, stateless Python service (FastAPI + RapidFuzz) that the API calls to:

- **`POST /v1/match`** — score bank transactions against bill / income / one-off occurrences (amount + name +
  date) and against learned category rules, and say which pairs are safe to apply automatically;
- **`POST /v1/detect-recurring`** — find regular payments in imported transactions ("We found 3 regular
  payments") so the app can offer **Add this bill?**;
- **`GET /health`** — `{ "status": "ok", "version": "1.0.0" }`.

The wire format and every scoring rule are defined in [`docs/MATCHER-CONTRACT.md`](../../docs/MATCHER-CONTRACT.md);
the product side in [`docs/PLAN-V6.md`](../../docs/PLAN-V6.md) §2 and §5 and
[`docs/BANK-SYNC.md`](../../docs/BANK-SYNC.md) → "Automatic matching". If the matcher is down, the API falls back to
its built-in C# matcher, so nothing breaks.

## Run it

```bash
make dev-matcher                        # uv run uvicorn --reload on http://localhost:8090 (reads MATCHER_KEY from .env.dev)
cd apps/matcher
uv sync                                 # Python 3.13 venv in .venv (runtime + dev deps from uv.lock)
uv run pytest                           # tests
uv run ruff check . && uv run ruff format --check .
```

`make dev` starts it next to the API and web app; `make test` / `make lint` include it.

```bash
curl -s localhost:8090/health
curl -s -H "X-Matcher-Key: $MATCHER_KEY" -H 'Content-Type: application/json' \
     -d '{"today":"2026-10-26","transactions":[{"id":"t1","postedOn":"2026-10-30","amountCents":-185000,
          "description":"ACH DEBIT OAKWOOD PROPERTY MGMT RENT"}],
          "candidates":[{"itemId":"rent","dueDate":"2026-11-01","kind":"bill","frequency":"monthly",
          "amountCents":185000,"names":["Rent","OAKWOOD PROPERTY"]}]}' \
     localhost:8090/v1/match
# → matches[0]: score 97 · name 100 · amount 100 · date 84 · auto · "name 100% · amount exact · 2 days early"
```

## Configuration

| Variable | Meaning |
|---|---|
| `MATCHER_KEY` | Shared secret; every request except `/health` must send it as `X-Matcher-Key` (compared in constant time). **Required**: empty → the process logs why and exits (code 2). Same value as the API's `Matcher__Key`; `make secrets` / `install.sh` generate it (`openssl rand -hex 32`). |
| `MATCHER_ALLOW_NO_KEY` | `1` runs without a key — tests and local experiments only. |
| `LOG_LEVEL` | `INFO` (default) logs one line per request: counts and timings. Never descriptions, payees or names. |

Limits: 1 MB request body, ≤ 5000 transactions, ≤ 2000 candidates (else `413`). Errors are RFC 9457 problem
details; validation errors list field paths and messages but never echo the input.

## Layout

| File | What |
|---|---|
| `src/matcher/app.py` | FastAPI app, key check + body limit (pure ASGI middleware, runs before the body is parsed), logging |
| `src/matcher/models.py` | pydantic v2 request/response models, camelCase aliases exactly as in the contract |
| `src/matcher/normalize.py` | Table-driven description normalisation (noise words, processors, card fragments, phones, dates, locations, merchant aliases) |
| `src/matcher/scoring.py` | `/v1/match`: name / amount / date / combined scores, tolerances, auto rules, greedy one-to-one assignment, category rules |
| `src/matcher/recurring.py` | `/v1/detect-recurring`: clustering, cadence fit, amount check, next due date, display name, kind, score |
| `src/matcher/version.py` | Version reported by `/health` and every match response (keep in sync with `pyproject.toml`) |

### Interpretation notes (where the contract leaves room)

- Component scores are rounded (half up) first; `score` is the weighted sum of those integers, rounded — so every
  number in a response can be recomputed from the others.
- A transaction's **description and payee** are both scored (best wins); memo is ignored.
- Tolerances: the widest applicable rule wins (`amountVaries` ⊃ `once` ⊃ recurring).
- "Only candidate with that exact amount in the window" counts **paid** occurrences too: an exact amount that also
  fits a paid bill is ambiguous, so it is not auto-applied by that rule.
- Ties in the greedy assignment: combined score, then amount score, then closeness in days, then name score, then
  same account, then input order. Each transaction keeps its 8 best alternatives (bounds memory on huge batches).
- Category rules are applied to **outflows** only (they describe expenses); `score` is the name score.
- Detect-recurring: the display name drops business-form words (`MGMT`, `INC`, `LLC`, `CORP`, …); a fixed amount is
  the **latest** one (price changes), a varying amount is the median; monthly/quarterly `nextDue` keeps the usual
  (most common) day-of-month; same-day duplicate imports are collapsed; names containing `TRANSFER`/`XFER` are never
  bills; a cluster with no payment for more than two periods (stopped subscription) is not suggested; when a whole
  merchant cluster fails, amount bands inside it are tried (Amazon Prime among Amazon purchases).

## Docker

`Dockerfile` is multi-stage on `python:3.13-slim` (builder: `uv sync --frozen --no-dev` into `/opt/venv`; runtime:
venv + `src`, non-root uid 10001, read-only-FS friendly, port 8090, one uvicorn worker, Python `urllib` healthcheck).
In compose it has **no published port** and a 96 MB memory limit.

Measured on x86-64 (Pi 4 is roughly 3–4× slower): idle RSS ≈ 52 MB; a maximum batch (5000 transactions × 2000
occurrences spread over a year, 300 category rules) ≈ 1.2 s and ≈ 79 MB peak. Typical sync batches (tens of
transactions) take a few milliseconds.
