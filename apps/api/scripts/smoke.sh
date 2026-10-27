#!/usr/bin/env bash
# End-to-end smoke test against a running API (default http://localhost:8080).
# Walks: register → couple code → second user joins → account → recurring → calendar → paid → summary,
# then v2: settings → pay period → expenses (deduction) → budgets → to-pay → goals → activity → push →
# passkeys config → month report → (optionally) autopay by the scheduler,
# then v4: weekly target + weekly budgets → plan a one-off with a payeeHint → week view (planned, left-at-end) → past/future weeks,
# then v3: inbox/CSV validation → (with SMOKE_SIMPLEFIN_PORT) fake SimpleFIN Bridge: claim → map → inbox → confirm/ignore/
# restore → CSV import → bulk → (v4) the Bridge clears the one-off at a different amount → cleared + variance + weekly budgets →
# sync/cooldown → scheduler auto-match → undo → unlink, then v4 cleanup (DELETE weekly budgets, clear the target).
# v6 (always): bank names / legacy payeeHint, amountVaries, auto-match level, matcher status, rematch 202/429, /pending,
# /transactions, found-bill routes; with SMOKE_MATCHER_URL the fuzzy-matcher flow (see below).
# v7 (always): paydayWeekday, account roles, recurring transfers, bulk create, /plan, sweep routes; with SMOKE_SIMPLEFIN_PORT +
# SMOKE_SCHEDULER_WAIT the Friday flow on the fake Bridge's v7 dataset (roles → plan math → fun transfers auto-paid → sweep frozen
# then auto-done → fun accounts never in To review → bulk from her note → feed diagnostics → all pending listed).
# Requires: curl, jq. Usage: scripts/smoke.sh [base-url]
#   SMOKE_SCHEDULER_WAIT=<seconds>  also create an autopay bill due today and wait for the scheduler
#                                   to mark it (set to > Scheduler__IntervalSeconds); default 0 = skip.
#   SMOKE_SIMPLEFIN_PORT=<port>     start scripts/fake-simplefin.py and run the bank-sync flow (API needs Security__DataKey
#                                   and SimpleFin__BaseUrl=http://127.0.0.1:<port>; add Sync__IntervalHours=0 +
#                                   Scheduler__IntervalSeconds=5 + SMOKE_SCHEDULER_WAIT for auto-match).
#   SMOKE_PLAID_PORT=<port>         v7: start scripts/fake-plaid.py and run the Plaid flow (API needs Security__DataKey,
#                                   Plaid__BaseUrl=http://127.0.0.1:<port>, Plaid__ClientId, Plaid__Secret; with SMOKE_SCHEDULER_WAIT
#                                   also Plaid__IntervalMinutes=0.1 + Plaid__RefreshPollMinutes=0.05 for the scheduler checks).
#   SMOKE_MATCHER_URL=<url>         v6: the API runs with Matcher__BaseUrl=<url> + Matcher__Key=$SMOKE_MATCHER_KEY. Replaces the v3–v5
#   SMOKE_MATCHER_KEY=<key>         bank flows (which assert built-in scores) with the fuzzy-matcher flow: auto-match with a reason,
#                                   learned names, rematch after a bank name, auto level off/bills, found bills accept/dismiss,
#                                   /pending, /transactions, and — when the smoke started the matcher itself (it does when <url> is
#                                   down and uv is installed) — the built-in fallback after stopping it. Needs SMOKE_SIMPLEFIN_PORT
#                                   and SMOKE_SCHEDULER_WAIT.
# Push and passkey checks adapt to the server: 503 problems when unconfigured, real flows when configured.
set -euo pipefail

BASE="${1:-http://localhost:8080}"
RUN="$(date +%s)-$RANDOM"
A_EMAIL="alex+$RUN@example.com"
B_EMAIL="sam+$RUN@example.com"
PASSWORD="correct horse battery staple"

command -v jq >/dev/null || { echo "jq is required"; exit 1; }

pass() { printf '  ok   %s\n' "$1"; }
fail() { printf '  FAIL %s\n' "$1"; exit 1; }

TMP_BODY="$(mktemp)"
TMP_REQ="$(mktemp)"
trap 'rm -f "$TMP_BODY" "$TMP_REQ"' EXIT
STATUS=""
BODY=""

# request METHOD PATH [TOKEN] [JSON] → sets $STATUS and $BODY (the body goes through a file so large CSV payloads work)
request() {
  local method="$1" path="$2" token="${3:-}" json="${4:-}"
  local -a args=(-s -o "$TMP_BODY" -w '%{http_code}' -X "$method" "$BASE$path" -H 'Content-Type: application/json')
  [[ -n "$token" ]] && args+=(-H "Authorization: Bearer $token")
  if [[ -n "$json" ]]; then
    printf '%s' "$json" > "$TMP_REQ"
    args+=(--data-binary "@$TMP_REQ")
  fi
  STATUS="$(curl "${args[@]}")"
  BODY="$(cat "$TMP_BODY")"
}

expect() { # expect CODE LABEL
  [[ "$STATUS" == "$1" ]] && pass "$2 ($STATUS)" || { echo "$BODY" | head -c 600; echo; fail "$2: expected $1 got $STATUS"; }
}

echo "Smoke test against $BASE"

request GET /health; expect 200 "health"
[[ "$(echo "$BODY" | jq -r .db)" == "ok" ]] || fail "health db != ok"

request POST /v1/auth/register '' "$(jq -nc --arg e "$A_EMAIL" --arg p "$PASSWORD" '{email:$e,password:$p,displayName:"Alex"}')"
expect 201 "register Alex"
A_TOKEN="$(echo "$BODY" | jq -r .accessToken)"; A_REFRESH="$(echo "$BODY" | jq -r .refreshToken)"
[[ "$(echo "$BODY" | jq -r .household)" == "null" ]] || fail "new user should have no household"

request POST /v1/auth/register '' "$(jq -nc --arg e "$A_EMAIL" --arg p "$PASSWORD" '{email:$e,password:$p,displayName:"Dup"}')"
expect 409 "duplicate email rejected"

request POST /v1/auth/login '' "$(jq -nc --arg e "$A_EMAIL" '{email:$e,password:"wrong-password-1"}')"
expect 401 "wrong password"
request POST /v1/auth/login '' "$(jq -nc '{email:"nobody@example.com",password:"wrong-password-1"}')"
expect 401 "unknown email (same status)"

request GET /v1/accounts "$A_TOKEN"; expect 409 "accounts without household → 409"
[[ "$(echo "$BODY" | jq -r .title)" == "No household" ]] || fail "expected title 'No household'"

request POST /v1/couple/code "$A_TOKEN" '{"name":"Alex & Sam"}'; expect 201 "create couple code"
CODE="$(echo "$BODY" | jq -r .code)"; PAYLOAD="$(echo "$BODY" | jq -r .payload)"
[[ "$CODE" =~ ^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$ ]] || fail "code format: $CODE"
[[ "$PAYLOAD" == cc1.* ]] || fail "payload prefix: $PAYLOAD"
HH_ID="$(echo "$BODY" | jq -r .household.id)"

request POST /v1/auth/refresh '' "$(jq -nc --arg r "$A_REFRESH" '{refreshToken:$r}')"; expect 200 "refresh Alex (picks up hh claim)"
A_TOKEN="$(echo "$BODY" | jq -r .accessToken)"; A_REFRESH2="$(echo "$BODY" | jq -r .refreshToken)"
[[ "$(echo "$BODY" | jq -r .household.id)" == "$HH_ID" ]] || fail "refresh should return household"

request POST /v1/auth/refresh '' "$(jq -nc --arg r "$A_REFRESH" '{refreshToken:$r}')"; expect 401 "replayed refresh token rejected"
request POST /v1/auth/refresh '' "$(jq -nc --arg r "$A_REFRESH2" '{refreshToken:$r}')"; expect 401 "family revoked after reuse"

request POST /v1/auth/login '' "$(jq -nc --arg e "$A_EMAIL" --arg p "$PASSWORD" '{email:$e,password:$p}')"; expect 200 "login Alex again"
A_TOKEN="$(echo "$BODY" | jq -r .accessToken)"; A_REFRESH="$(echo "$BODY" | jq -r .refreshToken)"

request POST /v1/auth/register '' "$(jq -nc --arg e "$B_EMAIL" --arg p "$PASSWORD" '{email:$e,password:$p,displayName:"Sam"}')"; expect 201 "register Sam"
B_TOKEN="$(echo "$BODY" | jq -r .accessToken)"

request POST /v1/couple/join "$B_TOKEN" '{"code":"ZZZZ-ZZZZ"}'; expect 404 "bogus code → 404"
LOWER="$(echo "$CODE" | tr -d '-' | tr 'A-Z' 'a-z')"
request POST /v1/couple/join "$B_TOKEN" "$(jq -nc --arg c "$LOWER" '{code:$c}')"; expect 200 "Sam joins with lowercase undashed code"
B_TOKEN="$(echo "$BODY" | jq -r .accessToken)"
[[ "$(echo "$BODY" | jq -r '.household.members | length')" == "2" ]] || fail "household should have 2 members"
request POST /v1/couple/join "$B_TOKEN" "$(jq -nc --arg c "$PAYLOAD" '{code:$c}')"; expect 409 "already in a household"
request POST /v1/couple/code "$A_TOKEN" '{}'; expect 409 "household full → no more codes"

request GET /v1/categories "$A_TOKEN"; expect 200 "categories"
[[ "$(echo "$BODY" | jq length)" == "12" ]] || fail "expected 12 default categories"
HOUSING="$(echo "$BODY" | jq -r '.[] | select(.name=="Housing") | .id')"
GROCERIES="$(echo "$BODY" | jq -r '.[] | select(.name=="Groceries") | .id')"

request POST /v1/accounts "$A_TOKEN" '{"name":"Joint checking","institution":"Credit Union","type":"checking","balanceCents":254000}'; expect 201 "create account"
ACC="$(echo "$BODY" | jq -r .id)"
[[ "$(echo "$BODY" | jq -r .icon)" == "landmark" ]] || fail "default icon"
request POST /v1/accounts "$B_TOKEN" '{"name":"Visa","institution":"Bank","type":"credit","balanceCents":54000,"color":"#ef4444"}'; expect 201 "Sam creates a credit card"
request POST "/v1/accounts/$ACC/balance" "$B_TOKEN" '{"balanceCents":249000}'; expect 200 "Sam updates Alex's account balance (shared household)"
request GET "/v1/accounts/$ACC/history" "$A_TOKEN"; expect 200 "history"
[[ "$(echo "$BODY" | jq length)" == "2" ]] || fail "expected 2 snapshots"
request GET /v1/accounts/00000000-0000-7000-8000-000000000000 "$A_TOKEN"; expect 404 "unknown account → 404"
request POST /v1/accounts "$A_TOKEN" '{"name":"","institution":"x","type":"piggy","balanceCents":1}'; expect 400 "invalid enum value → 400"
[[ "$(echo "$BODY" | jq -r '.errors | keys | join(",")')" == *type* ]] || fail "errors should name the field 'type'"
request POST /v1/accounts "$A_TOKEN" '{"name":"","institution":"x","type":"checking","balanceCents":1}'; expect 400 "validation error"
[[ "$(echo "$BODY" | jq -r '.errors | keys | join(",")')" == *name* ]] || fail "errors should name the field 'name'"
request POST /v1/accounts "$A_TOKEN" '{"name": '; expect 400 "malformed JSON → 400"
[[ "$(echo "$BODY" | jq -r '.errors | keys | join(",")')" != "" ]] || fail "malformed JSON should carry errors"
request GET "/v1/recurring?kind=snack" "$A_TOKEN"; expect 400 "invalid query enum → 400"
[[ "$(echo "$BODY" | jq -r '.errors | keys | join(",")')" == "kind" ]] || fail "errors should name 'kind'"

request POST /v1/recurring "$A_TOKEN" "$(jq -nc --arg c "$HOUSING" --arg a "$ACC" '{name:"Rent",kind:"bill",amountCents:185000,categoryId:$c,accountId:$a,frequency:"monthly",anchorDate:"2024-01-31"}')"; expect 201 "create monthly bill anchored Jan 31"
RENT="$(echo "$BODY" | jq -r .id)"
[[ "$(echo "$BODY" | jq -r .monthlyEquivalentCents)" == "185000" ]] || fail "monthly equivalent"
request POST /v1/recurring "$A_TOKEN" '{"name":"Netflix","kind":"subscription","amountCents":1599,"frequency":"yearly","anchorDate":"2024-02-10"}'; expect 201 "create yearly subscription"
[[ "$(echo "$BODY" | jq -r .monthlyEquivalentCents)" == "133" ]] || fail "yearly/12 half-even: 1599/12=133.25 → 133"
request POST /v1/recurring "$A_TOKEN" '{"name":"Salary","kind":"income","amountCents":250000,"frequency":"biweekly","anchorDate":"2024-02-02"}'; expect 201 "create biweekly income"
request GET "/v1/recurring?kind=subscription" "$A_TOKEN"; expect 200 "filter by kind"
[[ "$(echo "$BODY" | jq length)" == "1" ]] || fail "kind filter"
request POST /v1/recurring "$A_TOKEN" '{"name":"Bad","kind":"bill","amountCents":100,"frequency":"monthly","anchorDate":"2024-01-31","categoryId":"00000000-0000-7000-8000-000000000000"}'; expect 400 "foreign categoryId → 400"

request GET "/v1/calendar?from=2024-02-01&to=2024-02-29" "$B_TOKEN"; expect 200 "calendar Feb 2024 (Sam sees it too)"
DATES="$(echo "$BODY" | jq -r '[.occurrences[] | select(.name=="Rent") | .dueDate] | join(",")')"
[[ "$DATES" == "2024-02-29" ]] || fail "Jan 31 monthly should clamp to 2024-02-29, got '$DATES'"
[[ "$(echo "$BODY" | jq -r '[.occurrences[] | select(.name=="Salary")] | length')" == "2" ]] || fail "biweekly salary twice in Feb"
request GET "/v1/calendar?from=2024-01-01&to=2024-06-30" "$A_TOKEN"; expect 400 "range > 93 days → 400"

request PUT "/v1/recurring/$RENT/occurrences/2024-02-28/paid" "$A_TOKEN" '{}'; expect 404 "non-occurrence date → 404"
request PUT "/v1/recurring/$RENT/occurrences/2024-02-29/paid" "$A_TOKEN" '{"amountCents":185000}'; expect 200 "mark Feb 29 paid"
[[ "$(echo "$BODY" | jq -r .paid.amountCents)" == "185000" ]] || fail "paid info"
request GET "/v1/calendar?from=2024-02-01&to=2024-02-29" "$A_TOKEN"
[[ "$(echo "$BODY" | jq -r '.occurrences[] | select(.name=="Rent") | .paid.paidBy')" != "null" ]] || fail "calendar shows paid"

request GET "/v1/summary?month=2024-02" "$A_TOKEN"; expect 200 "summary Feb 2024"
echo "$BODY" | jq '{month,netWorthCents,assetsCents,liabilitiesCents,monthlyBillsCents,monthlySubscriptionsCents,monthlyIncomeCents,dueThisMonthCents,paidThisMonthCents,remainingThisMonthCents,byKind,byCategory:[.byCategory[]|{name,amountCents,count}]}'
[[ "$(echo "$BODY" | jq -r .netWorthCents)" == "195000" ]] || fail "net worth 249000 - 54000"
[[ "$(echo "$BODY" | jq -r .paidThisMonthCents)" == "185000" ]] || fail "paid this month"
[[ "$(echo "$BODY" | jq -r .monthlyIncomeCents)" == "541667" ]] || fail "biweekly 250000*26/12 = 541666.67 → 541667"

request DELETE "/v1/recurring/$RENT/occurrences/2024-02-29/paid" "$A_TOKEN"; expect 200 "un-mark paid"
[[ "$(echo "$BODY" | jq -r .paid)" == "null" ]] || fail "unpaid"
request PUT "/v1/recurring/$RENT/occurrences/2024-03-31/paid" "$A_TOKEN" '{}'; expect 200 "mark Mar 31 paid (manual → paid.auto=false)"
[[ "$(echo "$BODY" | jq -r .paid.auto)" == "false" ]] || fail "manual payment should have auto=false"
[[ "$(echo "$BODY" | jq -r .paid.paidBy)" != "null" ]] || fail "manual payment should carry paidBy"

echo "--- v2: household settings & pay period"
request GET /v1/payperiod "$A_TOKEN"; expect 409 "pay period unconfigured → 409"
[[ "$(echo "$BODY" | jq -r .title)" == "Pay period not configured" ]] || fail "409 title"
request PATCH /v1/couple "$A_TOKEN" '{"timezone":"Mars/Olympus_Mons"}'; expect 400 "invalid timezone → 400"
[[ "$(echo "$BODY" | jq -r '.errors | keys | join(",")')" == "timezone" ]] || fail "errors should name 'timezone'"
request PATCH /v1/couple "$A_TOKEN" '{"currency":"usd"}'; expect 400 "lower-case currency → 400"
request PATCH /v1/couple "$A_TOKEN" '{"weekStart":2}'; expect 400 "weekStart 2 → 400"
request PATCH /v1/couple "$A_TOKEN" '{"defaultAccountId":"00000000-0000-7000-8000-000000000000"}'; expect 400 "foreign defaultAccountId → 400"
[[ "$(echo "$BODY" | jq -r '.errors | keys | join(",")')" == "defaultAccountId" ]] || fail "errors should name 'defaultAccountId'"
request PATCH /v1/couple "$A_TOKEN" "$(jq -nc --arg a "$ACC" '{currency:"USD",weekStart:1,timezone:"America/Chicago",defaultAccountId:$a,payPeriod:"biweekly",payAnchorDate:"2026-01-09"}')"; expect 200 "PATCH settings"
[[ "$(echo "$BODY" | jq -r .timezone)" == "America/Chicago" ]] || fail "timezone saved"
[[ "$(echo "$BODY" | jq -r .weekStart)" == "$(echo "$BODY" | jq -r .paydayWeekday)" ]] || fail "v7: weekStart is derived from paydayWeekday"
[[ "$(echo "$BODY" | jq -r .payPeriod)" == "biweekly" ]] || fail "payPeriod saved"
[[ "$(echo "$BODY" | jq -r .defaultAccountId)" == "$ACC" ]] || fail "defaultAccountId saved"
request GET /v1/couple "$B_TOKEN"; expect 200 "GET couple carries settings"
[[ "$(echo "$BODY" | jq -r .currency)" == "USD" && "$(echo "$BODY" | jq -r .payAnchorDate)" == "2026-01-09" ]] || fail "couple settings shape"
request GET /v1/auth/me "$A_TOKEN"; expect 200 "auth/me household carries settings"
[[ "$(echo "$BODY" | jq -r .household.timezone)" == "America/Chicago" ]] || fail "me.household.timezone"
request PATCH /v1/couple "$A_TOKEN" '{"name":"Alex & Sam (renamed)"}'; expect 200 "PATCH name only"
[[ "$(echo "$BODY" | jq -r .name)" == "Alex & Sam (renamed)" ]] || fail "rename"
request GET "/v1/payperiod?date=2026-02-03" "$A_TOKEN"; expect 200 "pay period view"
[[ "$(echo "$BODY" | jq -r .period)" == "biweekly" ]] || fail "period"
[[ "$(echo "$BODY" | jq -r .start)" == "2026-01-23" && "$(echo "$BODY" | jq -r .end)" == "2026-02-05" && "$(echo "$BODY" | jq -r .nextPayday)" == "2026-02-06" ]] || fail "biweekly window from anchor 2026-01-09 at 2026-02-03: $(echo "$BODY" | jq -c '{start,end,nextPayday}')"
[[ "$(echo "$BODY" | jq -r .daysLeft)" == "3" ]] || fail "daysLeft"
[[ "$(echo "$BODY" | jq -r '.expectedIncomeCents')" == "250000" ]] || fail "one biweekly salary (Jan 30) in window: $(echo "$BODY" | jq -r .expectedIncomeCents)"
[[ "$(echo "$BODY" | jq -r '.spendableCents')" == "$(( $(echo "$BODY" | jq -r '249000 - .remainingCents') ))" ]] || fail "spendable = checking − remaining"
request GET /v1/payperiod "$A_TOKEN"; expect 200 "pay period view (today)"
request PATCH /v1/couple "$A_TOKEN" '{"payAnchorDate":null}'; expect 200 "clear payAnchorDate with null"
[[ "$(echo "$BODY" | jq -r .payAnchorDate)" == "null" ]] || fail "payAnchorDate cleared"
request PATCH /v1/couple "$A_TOKEN" '{"payAnchorDate":"2026-01-09"}'; expect 200 "restore payAnchorDate"

echo "--- v2: expenses with deduction"
request GET "/v1/accounts/$ACC" "$A_TOKEN"; BAL0="$(echo "$BODY" | jq -r .balanceCents)"
request GET "/v1/accounts/$ACC/history" "$A_TOKEN"; HIST0="$(echo "$BODY" | jq length)"
request POST /v1/expenses "$A_TOKEN" '{"name":"x","amountCents":0,"spentOn":"2024-02-15"}'; expect 400 "expense amount 0 → 400"
request POST /v1/expenses "$A_TOKEN" '{"name":"x","amountCents":100,"spentOn":"2024-02-15","categoryId":"00000000-0000-7000-8000-000000000000"}'; expect 400 "foreign categoryId → 400"
request POST /v1/expenses "$A_TOKEN" '{"name":"x","amountCents":100,"spentOn":"2024-02-15","deduct":true}'; expect 400 "deduct without account → 400"
request POST /v1/expenses "$A_TOKEN" "$(jq -nc --arg c "$GROCERIES" --arg a "$ACC" '{name:"Groceries run",amountCents:4210,categoryId:$c,accountId:$a,spentOn:"2024-02-15",note:"weekly shop"}')"; expect 201 "create expense (deduct defaults to true with an account)"
EXP="$(echo "$BODY" | jq -r .id)"
[[ "$(echo "$BODY" | jq -r .deducted)" == "true" ]] || fail "deducted flag"
request GET "/v1/accounts/$ACC" "$A_TOKEN"
[[ "$(echo "$BODY" | jq -r .balanceCents)" == "$((BAL0 - 4210))" ]] || fail "balance dropped by 4210: $(echo "$BODY" | jq -r .balanceCents) vs $BAL0"
request GET "/v1/accounts/$ACC/history" "$A_TOKEN"
[[ "$(echo "$BODY" | jq length)" == "$((HIST0 + 1))" ]] || fail "deduction added a snapshot"
request GET "/v1/expenses?month=2024-02" "$B_TOKEN"; expect 200 "list expenses by month (Sam sees it)"
[[ "$(echo "$BODY" | jq -r '.[0].id')" == "$EXP" ]] || fail "expense listed"
request GET "/v1/expenses?from=2024-02-01&to=2024-02-29" "$A_TOKEN"; expect 200 "list expenses by range"
request GET "/v1/expenses?from=2024-01-01&to=2024-06-30" "$A_TOKEN"; expect 400 "range > 93 days → 400"
request GET "/v1/summary?month=2024-02" "$A_TOKEN"; expect 200 "summary includes the expense"
[[ "$(echo "$BODY" | jq -r '.byCategory[] | select(.name=="Groceries") | .amountCents')" == "4210" ]] || fail "byCategory Groceries = 4210"
[[ "$(echo "$BODY" | jq -r .dueThisMonthCents)" == "190809" ]] || fail "dueThisMonth = rent 185000 + netflix 1599 + expense 4210: $(echo "$BODY" | jq -r .dueThisMonthCents)"
[[ "$(echo "$BODY" | jq -r .paidThisMonthCents)" == "4210" ]] || fail "paidThisMonth = expense only (Feb rent + netflix unpaid)"
request GET "/v1/calendar?from=2024-02-01&to=2024-02-29" "$A_TOKEN"; expect 200 "calendar carries expenses"
[[ "$(echo "$BODY" | jq -r '.expenses | length')" == "1" ]] || fail "calendar.expenses"
request PUT "/v1/expenses/$EXP" "$A_TOKEN" "$(jq -nc --arg c "$GROCERIES" --arg a "$ACC" '{name:"Groceries run",amountCents:5210,categoryId:$c,accountId:$a,spentOn:"2024-02-15"}')"; expect 200 "PUT expense amount (+1000)"
request GET "/v1/accounts/$ACC" "$A_TOKEN"
[[ "$(echo "$BODY" | jq -r .balanceCents)" == "$((BAL0 - 5210))" ]] || fail "delta re-applied: $(echo "$BODY" | jq -r .balanceCents)"
request PUT "/v1/expenses/$EXP" "$A_TOKEN" '{"name":"Groceries run","amountCents":5210,"spentOn":"2024-02-15"}'; expect 200 "PUT expense without account (deduction reversed)"
[[ "$(echo "$BODY" | jq -r .deducted)" == "false" ]] || fail "deducted false without account"
request GET "/v1/accounts/$ACC" "$A_TOKEN"
[[ "$(echo "$BODY" | jq -r .balanceCents)" == "$BAL0" ]] || fail "balance restored after removing account"
request PUT "/v1/expenses/$EXP" "$A_TOKEN" "$(jq -nc --arg a "$ACC" '{name:"Groceries run",amountCents:5210,accountId:$a,spentOn:"2024-02-15"}')"; expect 200 "PUT expense back onto the account"
request DELETE "/v1/expenses/$EXP" "$A_TOKEN"; expect 204 "DELETE expense (reverses deduction)"
request GET "/v1/accounts/$ACC" "$A_TOKEN"
[[ "$(echo "$BODY" | jq -r .balanceCents)" == "$BAL0" ]] || fail "balance restored after delete"
request GET "/v1/expenses/$EXP" "$A_TOKEN"; expect 404 "deleted expense → 404"
request POST /v1/expenses "$A_TOKEN" "$(jq -nc --arg a "$ACC" '{name:"Cash coffee",amountCents:450,accountId:$a,spentOn:"2024-02-16",deduct:false}')"; expect 201 "expense with deduct=false"
[[ "$(echo "$BODY" | jq -r .deducted)" == "false" ]] || fail "deduct=false honoured"
request GET "/v1/accounts/$ACC" "$A_TOKEN"
[[ "$(echo "$BODY" | jq -r .balanceCents)" == "$BAL0" ]] || fail "deduct=false leaves balance alone"
request DELETE "/v1/expenses/$(echo "$BODY" | jq -r .id)" "$A_TOKEN" >/dev/null 2>&1 || true

echo "--- v2: budgets"
request PUT "/v1/budgets/$HOUSING" "$A_TOKEN" '{"amountCents":-1}'; expect 400 "negative budget → 400"
request PUT "/v1/budgets/00000000-0000-7000-8000-000000000000" "$A_TOKEN" '{"amountCents":1}'; expect 404 "foreign category → 404"
request PUT "/v1/budgets/$HOUSING?month=2024-02" "$A_TOKEN" '{"amountCents":200000}'; expect 200 "set Housing budget (row for Feb 2024)"
[[ "$(echo "$BODY" | jq -r .budgetCents)" == "200000" ]] || fail "budgetCents"
[[ "$(echo "$BODY" | jq -r .recurringCents)" == "185000" ]] || fail "recurringCents = Rent monthly equivalent"
[[ "$(echo "$BODY" | jq -r .percent)" == "93" ]] || fail "percent 185000/200000 → 93: $(echo "$BODY" | jq -r .percent)"
request PUT "/v1/budgets/$HOUSING?month=2023-12" "$A_TOKEN" '{"amountCents":200000}'; expect 200 "budget row for a month before Rent started"
[[ "$(echo "$BODY" | jq -r .recurringCents)" == "0" && "$(echo "$BODY" | jq -r .percent)" == "0" ]] || fail "Rent not active in Dec 2023"
request PUT "/v1/budgets/$HOUSING?month=nope" "$A_TOKEN" '{"amountCents":200000}'; expect 400 "bad month → 400"
request GET "/v1/budgets?month=2024-02" "$B_TOKEN"; expect 200 "budget rows for Feb 2024"
[[ "$(echo "$BODY" | jq -r '.rows | length')" == "12" ]] || fail "every category gets a row"
[[ "$(echo "$BODY" | jq -r --arg h "$HOUSING" '.rows[] | select(.categoryId==$h) | .remainingCents')" == "15000" ]] || fail "remaining 15000"
[[ "$(echo "$BODY" | jq -r --arg g "$GROCERIES" '.rows[] | select(.categoryId==$g) | .budgetCents')" == "null" ]] || fail "unbudgeted → null"
[[ "$(echo "$BODY" | jq -r .totalBudgetCents)" == "200000" && "$(echo "$BODY" | jq -r .totalSpentCents)" == "185000" ]] || fail "totals"
request DELETE "/v1/budgets/$HOUSING" "$A_TOKEN"; expect 204 "clear budget"
request DELETE "/v1/budgets/$HOUSING" "$A_TOKEN"; expect 404 "clear again → 404"
request GET "/v1/budgets" "$A_TOKEN"
[[ "$(echo "$BODY" | jq -r --arg h "$HOUSING" '.rows[] | select(.categoryId==$h) | .budgetCents')" == "null" ]] || fail "budget cleared"

echo "--- v2: to-pay checklist"
request POST /v1/todos "$A_TOKEN" '{"title":""}'; expect 400 "empty title → 400"
request POST /v1/todos "$A_TOKEN" '{"title":"Transfer $200 to savings","amountCents":20000}'; expect 201 "create todo 1"
T1="$(echo "$BODY" | jq -r .id)"
request POST /v1/todos "$B_TOKEN" '{"title":"Call insurance","dueDate":"2026-12-01"}'; expect 201 "create todo 2 (Sam)"
T2="$(echo "$BODY" | jq -r .id)"
[[ "$(echo "$BODY" | jq -r .sortOrder)" == "1" ]] || fail "second todo sortOrder 1"
request PUT /v1/todos/order "$A_TOKEN" "$(jq -nc --arg a "$T2" --arg b "$T1" '{ids:[$a,$b]}')"; expect 204 "reorder"
request PUT /v1/todos/order "$A_TOKEN" '{"ids":["00000000-0000-7000-8000-000000000000"]}'; expect 400 "reorder with foreign id → 400"
request GET /v1/todos "$A_TOKEN"; expect 200 "list open todos"
[[ "$(echo "$BODY" | jq -r '.[0].id')" == "$T2" && "$(echo "$BODY" | jq length)" == "2" ]] || fail "order applied"
request POST "/v1/todos/$T1/done" "$B_TOKEN"; expect 200 "Sam checks off todo 1"
[[ "$(echo "$BODY" | jq -r .doneByName)" == "Sam" && "$(echo "$BODY" | jq -r .doneAt)" != "null" ]] || fail "doneBy/doneAt"
request GET /v1/todos "$A_TOKEN"
[[ "$(echo "$BODY" | jq length)" == "1" ]] || fail "done items hidden by default"
request GET "/v1/todos?includeDone=true" "$A_TOKEN"
[[ "$(echo "$BODY" | jq length)" == "2" && "$(echo "$BODY" | jq -r '.[1].id')" == "$T1" ]] || fail "includeDone appends done items after open ones"
request DELETE "/v1/todos/$T1/done" "$A_TOKEN"; expect 200 "undo done"
[[ "$(echo "$BODY" | jq -r .doneAt)" == "null" ]] || fail "undone"
request PUT "/v1/todos/$T2" "$A_TOKEN" '{"title":"Call insurance (renewal)","dueDate":"2026-12-02"}'; expect 200 "edit todo"
request DELETE "/v1/todos/$T2" "$A_TOKEN"; expect 204 "delete todo"
request DELETE "/v1/todos/$T2" "$A_TOKEN"; expect 404 "delete again → 404"

echo "--- v2: savings goals"
request POST /v1/goals "$A_TOKEN" '{"name":"Vacation","targetCents":0}'; expect 400 "target 0 → 400"
TARGET_DATE="$(date -u -d '+6 months' +%F 2>/dev/null || date -u -v+6m +%F)"
request POST /v1/goals "$A_TOKEN" "$(jq -nc --arg d "$TARGET_DATE" '{name:"Vacation",targetCents:300000,targetDate:$d,color:"#f43f5e",icon:"plane"}')"; expect 201 "create manual goal"
GOAL="$(echo "$BODY" | jq -r .id)"
[[ "$(echo "$BODY" | jq -r .currentCents)" == "0" && "$(echo "$BODY" | jq -r .percent)" == "0" ]] || fail "new goal at 0"
[[ "$(echo "$BODY" | jq -r '.history | length')" == "1" ]] || fail "manual goal history starts at creation point"
[[ "$(echo "$BODY" | jq -r .monthlyNeededCents)" != "null" ]] || fail "monthlyNeededCents with a target date"
request POST "/v1/goals/$GOAL/contribute" "$B_TOKEN" '{"amountCents":50000}'; expect 200 "Sam contributes 500.00"
[[ "$(echo "$BODY" | jq -r .currentCents)" == "50000" && "$(echo "$BODY" | jq -r .percent)" == "17" ]] || fail "current/percent after contribution"
[[ "$(echo "$BODY" | jq -r '.history | length')" == "2" ]] || fail "history grows with contributions"
echo "$BODY" | jq -c '{monthlyPaceCents,projectedDate,monthlyNeededCents}'
echo "$BODY" | jq -e 'has("monthlyPaceCents") and has("projectedDate") and has("monthlyNeededCents")' >/dev/null || fail "projection fields present"
request POST "/v1/goals/$GOAL/contribute" "$A_TOKEN" '{"amountCents":-60000}'; expect 400 "cannot take out more than saved"
request POST "/v1/goals/$GOAL/contribute" "$A_TOKEN" '{"amountCents":0}'; expect 400 "contribute 0 → 400"
request POST /v1/goals "$A_TOKEN" "$(jq -nc --arg a "$ACC" '{name:"Emergency fund",targetCents:1000000,accountId:$a}')"; expect 201 "create account-linked goal"
LINKED="$(echo "$BODY" | jq -r .id)"
[[ "$(echo "$BODY" | jq -r .currentCents)" == "$BAL0" && "$(echo "$BODY" | jq -r .accountName)" == "Joint checking" ]] || fail "linked goal reads the account balance"
[[ "$(echo "$BODY" | jq -r '.history | length')" -ge 2 ]] || fail "linked goal history = account snapshots"
request POST "/v1/goals/$LINKED/contribute" "$A_TOKEN" '{"amountCents":100}'; expect 400 "contribute to linked goal → 400"
[[ "$(echo "$BODY" | jq -r '.errors | keys | join(",")')" == "amountCents" ]] || fail "field error on amountCents"
request GET /v1/goals "$B_TOKEN"; expect 200 "list goals"
[[ "$(echo "$BODY" | jq length)" == "2" ]] || fail "two goals"
request DELETE "/v1/goals/$LINKED" "$A_TOKEN"; expect 204 "archive goal"
request GET "/v1/goals/$LINKED" "$A_TOKEN"; expect 404 "archived goal → 404"

echo "--- v2: activity feed"
request GET "/v1/activity?limit=5" "$A_TOKEN"; expect 200 "activity page 1"
[[ "$(echo "$BODY" | jq -r '.events | length')" == "5" && "$(echo "$BODY" | jq -r .nextBefore)" != "null" ]] || fail "5 events + cursor"
NEXT="$(echo "$BODY" | jq -r .nextBefore)"; FIRST_IDS="$(echo "$BODY" | jq -r '[.events[].id] | join(",")')"
request GET "/v1/activity?limit=100&before=$NEXT" "$A_TOKEN"; expect 200 "activity page 2 (keyset)"
for id in ${FIRST_IDS//,/ }; do echo "$BODY" | jq -e --arg id "$id" '.events[] | select(.id==$id)' >/dev/null && fail "page 2 overlaps page 1"; done
request GET "/v1/activity?limit=100" "$A_TOKEN"
echo "$BODY" | jq -r '.events[:8][] | "  \(.actorName // "system") \(.summary)  [\(.kind)]"'
[[ "$(echo "$BODY" | jq -r '[.events[] | select(.kind=="occurrence.paid")][0].summary')" == "marked Rent paid · \$1,850.00" ]] || fail "paid summary wording"
[[ "$(echo "$BODY" | jq -r '[.events[] | select(.kind=="todo.done")][0].actorName')" == "Sam" ]] || fail "actor name joined (Sam checked off)"
[[ "$(echo "$BODY" | jq -r '[.events[] | select(.kind=="goal.contributed")][0].data.amountCents')" == "50000" ]] || fail "data carries amounts"
for k in account.created account.balance recurring.created occurrence.unpaid expense.created expense.updated expense.deleted budget.set budget.cleared todo.created todo.undone todo.deleted goal.created goal.deleted couple.joined couple.settings; do
  echo "$BODY" | jq -e --arg k "$k" '.events[] | select(.kind==$k)' >/dev/null || fail "missing activity kind $k"
done
pass "all expected activity kinds present"

echo "--- v2: push"
request GET /v1/push/vapid-public-key "$A_TOKEN"
if [[ "$STATUS" == "503" ]]; then
  pass "push not configured → 503"
  [[ "$(echo "$BODY" | jq -r .type)" == *push_not_configured && "$(echo "$BODY" | jq -r .title)" == "Push not configured" ]] || fail "503 problem shape: $BODY"
  request GET /v1/push/prefs "$A_TOKEN"; expect 503 "prefs also 503 when unconfigured"
else
  expect 200 "vapid public key"
  [[ "$(echo "$BODY" | jq -r .publicKey | wc -c)" -ge 80 ]] || fail "public key length"
  ENDPOINT="https://push.example.invalid/sub/$RUN"
  request POST /v1/push/subscriptions "$A_TOKEN" "$(jq -nc --arg e "$ENDPOINT" '{endpoint:$e,keys:{p256dh:"BPUBKEY",auth:"AUTHKEY"},userAgent:"smoke/1.0"}')"; expect 201 "create subscription"
  SUB="$(echo "$BODY" | jq -r .id)"
  request POST /v1/push/subscriptions "$A_TOKEN" "$(jq -nc --arg e "$ENDPOINT" '{endpoint:$e,keys:{p256dh:"BPUBKEY2",auth:"AUTHKEY2"}}')"; expect 201 "upsert same endpoint"
  [[ "$(echo "$BODY" | jq -r .id)" == "$SUB" ]] || fail "upsert keeps id"
  request POST /v1/push/subscriptions "$A_TOKEN" '{"endpoint":"http://insecure.example","keys":{"p256dh":"a","auth":"b"}}'; expect 400 "http endpoint → 400"
  request GET /v1/push/prefs "$A_TOKEN"; expect 200 "prefs (defaults created)"
  [[ "$(echo "$BODY" | jq -r .hourLocal)" == "9" && "$(echo "$BODY" | jq -c .daysBefore)" == "[1]" && "$(echo "$BODY" | jq -r '.subscriptions | length')" == "1" ]] || fail "default prefs: $BODY"
  request PUT /v1/push/prefs "$A_TOKEN" '{"remindersEnabled":true,"daysBefore":[0,1,3],"hourLocal":20,"activityEnabled":false}'; expect 200 "PUT prefs"
  [[ "$(echo "$BODY" | jq -c .daysBefore)" == "[0,1,3]" && "$(echo "$BODY" | jq -r .activityEnabled)" == "false" ]] || fail "prefs saved"
  request PUT /v1/push/prefs "$A_TOKEN" '{"remindersEnabled":true,"daysBefore":[0,1,2,3,4,5],"hourLocal":9,"activityEnabled":true}'; expect 400 "> 5 daysBefore → 400"
  request PUT /v1/push/prefs "$A_TOKEN" '{"remindersEnabled":true,"daysBefore":[1],"hourLocal":24,"activityEnabled":true}'; expect 400 "hourLocal 24 → 400"
  request POST /v1/push/test "$A_TOKEN"; expect 202 "test push accepted (delivery to a fake endpoint fails in the background)"
  request DELETE /v1/push/subscriptions "$A_TOKEN" '{"endpoint":"https://push.example.invalid/nope"}'; expect 404 "delete unknown subscription → 404"
  request DELETE /v1/push/subscriptions "$A_TOKEN" "$(jq -nc --arg e "$ENDPOINT" '{endpoint:$e}')"; expect 204 "delete subscription by endpoint"
  request POST /v1/push/subscriptions "$A_TOKEN" "$(jq -nc --arg e "$ENDPOINT-2" '{endpoint:$e,keys:{p256dh:"BPUBKEY",auth:"AUTHKEY"}}')"; expect 201 "create second subscription"
  SUB2="$(echo "$BODY" | jq -r .id)"
  request DELETE /v1/push/subscriptions "$B_TOKEN" "$(jq -nc --arg i "$SUB2" '{id:$i}')"; expect 404 "Sam cannot delete Alex's subscription"
  request DELETE /v1/push/subscriptions "$A_TOKEN" '{}'; expect 400 "neither endpoint nor id → 400"
  request DELETE /v1/push/subscriptions "$A_TOKEN" "$(jq -nc --arg i "$SUB2" '{id:$i}')"; expect 204 "delete subscription by id"
  request GET /v1/push/prefs "$A_TOKEN"
  [[ "$(echo "$BODY" | jq -r '.subscriptions | length')" == "0" ]] || fail "subscriptions removed"
fi

echo "--- v2: passkeys"
request GET /v1/auth/passkeys/config; expect 200 "passkeys config (anonymous)"
PK_ENABLED="$(echo "$BODY" | jq -r .enabled)"
if [[ "$PK_ENABLED" == "false" ]]; then
  [[ "$(echo "$BODY" | jq -r .rpId)" == "null" ]] || fail "rpId null when disabled"
  request POST /v1/auth/passkeys/register/options "$A_TOKEN" '{"name":"Phone"}'; expect 503 "register options → 503 when disabled"
  [[ "$(echo "$BODY" | jq -r .type)" == *passkeys_not_configured ]] || fail "503 type"
  request POST /v1/auth/passkeys/login/options '' '{}'; expect 503 "login options → 503 when disabled"
else
  pass "passkeys enabled for rpId $(echo "$BODY" | jq -r .rpId)"
  request POST /v1/auth/passkeys/register/options "$A_TOKEN" '{"name":"Phone"}'; expect 200 "register options"
  [[ "$(echo "$BODY" | jq -r .options.authenticatorSelection.residentKey)" == "required" && "$(echo "$BODY" | jq -r .options.authenticatorSelection.userVerification)" == "required" ]] || fail "resident key + UV required"
  [[ "$(echo "$BODY" | jq -r .options.user.name)" == "$A_EMAIL" ]] || fail "user.name"
  CH="$(echo "$BODY" | jq -r .challengeId)"
  request POST /v1/auth/passkeys/register "$A_TOKEN" "$(jq -nc --arg c "$CH" '{challengeId:$c,name:"Phone",credential:{id:"AQID",rawId:"AQID",type:"public-key",response:{clientDataJSON:"AQID",attestationObject:"AQID"}}}')"; expect 400 "bogus attestation → 400"
  request POST /v1/auth/passkeys/register "$A_TOKEN" "$(jq -nc --arg c "$CH" '{challengeId:$c,name:"Phone",credential:{id:"AQID",rawId:"AQID",type:"public-key",response:{clientDataJSON:"AQID",attestationObject:"AQID"}}}')"; expect 400 "challenge is single-use"
  request POST /v1/auth/passkeys/login/options '' '{}'; expect 200 "login options (anonymous)"
  [[ "$(echo "$BODY" | jq -c .options.allowCredentials)" == "[]" && "$(echo "$BODY" | jq -r .options.userVerification)" == "required" ]] || fail "discoverable login options"
  request POST /v1/auth/passkeys/login '' "$(jq -nc --arg c "$(echo "$BODY" | jq -r .challengeId)" '{challengeId:$c,credential:{id:"AQID",rawId:"AQID",type:"public-key",response:{clientDataJSON:"AQID",authenticatorData:"AQID",signature:"AQID"}}}')"; expect 401 "unknown credential → 401"
  request GET /v1/auth/passkeys "$A_TOKEN"; expect 200 "list passkeys"
fi

echo "--- v2: month report"
request GET "/v1/reports/month?month=2024-02" "$A_TOKEN"; expect 200 "month report"
echo "$BODY" | jq -e '(.month=="2024-02") and has("summary") and has("previousNetWorthCents") and has("netWorthDeltaCents") and has("expensesTotalCents") and (.topExpenses|type=="array") and (.budgets|length==12) and (.autoPaid|type=="array") and has("paidCount") and has("unpaidCount") and has("generatedAt")' >/dev/null || fail "report shape: $(echo "$BODY" | jq -c 'del(.summary,.budgets)')"
[[ "$(echo "$BODY" | jq -r .paidCount)" == "0" && "$(echo "$BODY" | jq -r .unpaidCount)" == "2" ]] || fail "Feb 2024: rent + netflix unpaid"
[[ "$(echo "$BODY" | jq -r .previousNetWorthCents)" == "null" ]] || fail "no snapshots before Feb 2024 → null"
request GET "/v1/reports/month?month=2024-2" "$A_TOKEN"; expect 400 "bad month → 400"

if [[ "${SMOKE_SCHEDULER_WAIT:-0}" -gt 0 ]]; then
  echo "--- v2: autopay by the scheduler (waiting up to ${SMOKE_SCHEDULER_WAIT}s)"
  TODAY_LOCAL="$(TZ=America/Chicago date +%F)"
  request POST /v1/recurring "$A_TOKEN" "$(jq -nc --arg d "$TODAY_LOCAL" '{name:"Gym",kind:"subscription",amountCents:2999,frequency:"monthly",anchorDate:$d,autoPay:true}')"; expect 201 "create autopay subscription due today ($TODAY_LOCAL)"
  GYM="$(echo "$BODY" | jq -r .id)"
  AUTO="null"
  for _ in $(seq 1 "$SMOKE_SCHEDULER_WAIT"); do
    request GET "/v1/calendar?from=$TODAY_LOCAL&to=$TODAY_LOCAL" "$A_TOKEN"
    AUTO="$(echo "$BODY" | jq -r --arg id "$GYM" '.occurrences[] | select(.itemId==$id) | .paid.auto')"
    [[ "$AUTO" == "true" ]] && break
    sleep 1
  done
  [[ "$AUTO" == "true" ]] || fail "scheduler did not auto-pay within ${SMOKE_SCHEDULER_WAIT}s"
  pass "scheduler auto-paid Gym (paid.auto=true, paidBy=$(echo "$BODY" | jq -r --arg id "$GYM" '.occurrences[] | select(.itemId==$id) | .paid.paidBy'))"
  request GET "/v1/activity?limit=50" "$A_TOKEN"
  [[ "$(echo "$BODY" | jq -r '[.events[] | select(.kind=="occurrence.autopaid")][0].actorId')" == "null" ]] || fail "autopaid event has no actor"
  request DELETE "/v1/recurring/$GYM/occurrences/$TODAY_LOCAL/paid" "$A_TOKEN"; expect 200 "undo autopay"
  sleep "$SMOKE_SCHEDULER_WAIT"
  request GET "/v1/calendar?from=$TODAY_LOCAL&to=$TODAY_LOCAL" "$A_TOKEN"
  [[ "$(echo "$BODY" | jq -r --arg id "$GYM" '.occurrences[] | select(.itemId==$id) | .paid')" == "null" ]] || fail "undo guard: scheduler re-paid the undone occurrence"
  pass "undo guard holds: still unpaid after another tick"
  request PUT "/v1/recurring/$GYM/occurrences/$TODAY_LOCAL/paid" "$A_TOKEN" '{}'; expect 200 "manual pay after undo (clears the skip)"
  request DELETE "/v1/recurring/$GYM" "$A_TOKEN"; expect 204 "archive Gym"
fi


echo "--- v4: weekly target, weekly budgets, week view, planned one-off"
TODAY_UTC="$(date -u +%F)"
TRANSPORT="$(request GET /v1/categories "$A_TOKEN" >/dev/null; echo "$BODY" | jq -r '.[] | select(.name=="Transportation") | .id')"
request PATCH /v1/couple "$A_TOKEN" '{"weeklySpendCents":-1}'; expect 400 "negative weekly target → 400"
[[ "$(echo "$BODY" | jq -r '.errors | keys | join(",")')" == "weeklySpendCents" ]] || fail "errors should name 'weeklySpendCents'"
request PATCH /v1/couple "$A_TOKEN" '{"weeklySpendCents":20000}'; expect 200 "set weekly spending target 200.00"
[[ "$(echo "$BODY" | jq -r .weeklySpendCents)" == "20000" ]] || fail "weeklySpendCents saved"
request GET /v1/couple "$B_TOKEN"; [[ "$(echo "$BODY" | jq -r .weeklySpendCents)" == "20000" ]] || fail "GET couple carries weeklySpendCents"
request PUT "/v1/budgets/$TRANSPORT?period=daily" "$A_TOKEN" '{"amountCents":5000}'; expect 400 "period=daily → 400"
[[ "$(echo "$BODY" | jq -r '.errors | keys | join(",")')" == "period" ]] || fail "errors should name 'period'"
request PUT "/v1/budgets/$TRANSPORT?period=weekly" "$A_TOKEN" '{"amountCents":5000}'; expect 200 "set weekly Transportation budget (returns WeekBudgetRow)"
echo "$BODY" | jq -e --arg c "$TRANSPORT" '.categoryId==$c and .budgetCents==5000 and has("spentCents") and has("varianceCents") and has("percent") and (has("recurringCents")|not)' >/dev/null || fail "WeekBudgetRow shape: $BODY"
request PUT "/v1/budgets/$GROCERIES?period=weekly" "$A_TOKEN" '{"amountCents":10000}'; expect 200 "set weekly Groceries budget"
request GET "/v1/budgets?month=$(date -u +%Y-%m)" "$A_TOKEN"; expect 200 "monthly budgets unchanged by weekly rows"
[[ "$(echo "$BODY" | jq -r --arg g "$GROCERIES" '.rows[] | select(.categoryId==$g) | .budgetCents')" == "null" ]] || fail "GET /budgets?month= must list monthly rows only"
request GET /v1/budgets/week "$A_TOKEN"; expect 200 "weekly budget view"
echo "$BODY" | jq -e '.overall.categoryId==null and .overall.budgetCents==20000 and (.categories|length==2) and (.history|length==8) and .totalBudgetCents==15000' >/dev/null || fail "weekly budget view: $BODY"
[[ "$(echo "$BODY" | jq -r '.history | map(.weekStart) | . == sort')" == "true" ]] || fail "history oldest first"
request GET "/v1/week?start=nope" "$A_TOKEN"; expect 400 "bad week start → 400"
request GET "/v1/week?start=$TODAY_UTC" "$A_TOKEN"; expect 200 "week view (week of $TODAY_UTC)"
WEEK0="$BODY"
PAYDAY_WD="$(request GET /v1/couple "$A_TOKEN" >/dev/null; echo "$BODY" | jq -r .paydayWeekday)"
echo "$WEEK0" | jq -e --argjson p "$PAYDAY_WD" '(.days|length==7) and .weekStart==$p and (.isCurrent or .projected) and (.isPast|not) and .leftAtEndCents!=null and .budget.overall.budgetCents==20000 and (.overdue|type=="array")' >/dev/null || fail "week view shape: $(echo "$WEEK0" | jq -c 'del(.days,.budget,.overdue)')"
WEEK_START="$(echo "$WEEK0" | jq -r .start)"; WEEK_END="$(echo "$WEEK0" | jq -r .end)"
[[ "$(date -u -d "$WEEK_START" +%w)" == "$PAYDAY_WD" && "$(date -u -d "$WEEK_START +6 days" +%F)" == "$WEEK_END" ]] || fail "v7 payday-anchored week bounds (payday $PAYDAY_WD): $WEEK_START … $WEEK_END"
[[ "$(echo "$WEEK0" | jq -r '.days[0].date')" == "$WEEK_START" && "$(echo "$WEEK0" | jq -r '.days[6].date')" == "$WEEK_END" ]] || fail "days run start → end"
LEFT0="$(echo "$WEEK0" | jq -r .leftAtEndCents)"
request POST /v1/recurring "$A_TOKEN" "$(jq -nc --arg c "$TRANSPORT" --arg a "$ACC" --arg d "$TODAY_UTC" --arg h "$(printf 'x%.0s' $(seq 1 81))" '{name:"Oil change",kind:"bill",amountCents:6000,categoryId:$c,accountId:$a,frequency:"once",anchorDate:$d,payeeHint:$h}')"; expect 400 "payeeHint > 80 chars → 400"
[[ "$(echo "$BODY" | jq -r '.errors | keys | join(",")')" == "payeeHint" ]] || fail "errors should name 'payeeHint'"
request POST /v1/recurring "$A_TOKEN" "$(jq -nc --arg c "$TRANSPORT" --arg a "$ACC" --arg d "$TODAY_UTC" '{name:"Oil change",kind:"bill",amountCents:6000,categoryId:$c,accountId:$a,frequency:"once",anchorDate:$d,payeeHint:"JIFFY LUBE"}')"; expect 201 "plan a one-off: Oil change 60.00 on $TODAY_UTC, bank shows it as JIFFY LUBE"
ONEOFF="$(echo "$BODY" | jq -r .id)"
echo "$BODY" | jq -e --arg d "$TODAY_UTC" '.frequency=="once" and .payeeHint=="JIFFY LUBE" and .monthlyEquivalentCents==0 and .nextDueDate==$d' >/dev/null || fail "one-off shape: $BODY"
request GET "/v1/week?start=$TODAY_UTC" "$A_TOKEN"; expect 200 "week view after planning"
[[ "$(echo "$BODY" | jq -r .leftAtEndCents)" == "$((LEFT0 - 6000))" ]] || fail "leftAtEnd should drop by 6000: $LEFT0 → $(echo "$BODY" | jq -r .leftAtEndCents)"
echo "$BODY" | jq -e --arg id "$ONEOFF" --arg d "$TODAY_UTC" '.days[] | select(.date==$d) | .planned[] | select(.itemId==$id) | .status=="planned" and .varianceCents==null and .paid==null' >/dev/null || fail "one-off should be planned on $TODAY_UTC"
[[ "$(echo "$BODY" | jq -r --arg d "$TODAY_UTC" '.days[] | select(.date==$d) | .plannedOutCents')" -ge 6000 ]] || fail "day plannedOutCents includes the one-off"
pass "one-off planned · leftAtEnd −60.00"
request GET "/v1/calendar?from=$TODAY_UTC&to=$TODAY_UTC" "$A_TOKEN"
echo "$BODY" | jq -e --arg id "$ONEOFF" '.occurrences[] | select(.itemId==$id) | .status=="planned" and .varianceCents==null' >/dev/null || fail "calendar occurrences carry status/varianceCents"
request GET "/v1/recurring/$ONEOFF" "$A_TOKEN"; [[ "$(echo "$BODY" | jq -r .payeeHint)" == "JIFFY LUBE" ]] || fail "GET recurring carries payeeHint"
request PUT "/v1/recurring/$ONEOFF" "$A_TOKEN" "$(jq -nc --arg c "$TRANSPORT" --arg a "$ACC" --arg d "$TODAY_UTC" '{name:"Oil change",kind:"bill",amountCents:6000,categoryId:$c,accountId:$a,frequency:"once",anchorDate:$d}')"; expect 200 "PUT without payeeHint clears it"
[[ "$(echo "$BODY" | jq -r .payeeHint)" == "null" ]] || fail "payeeHint cleared"
request PUT "/v1/recurring/$ONEOFF" "$A_TOKEN" "$(jq -nc --arg c "$TRANSPORT" --arg a "$ACC" --arg d "$TODAY_UTC" '{name:"Oil change",kind:"bill",amountCents:6000,categoryId:$c,accountId:$a,frequency:"once",anchorDate:$d,payeeHint:"Jiffy Lube"}')"; expect 200 "PUT sets payeeHint again"
PAST_START="$(date -u -d "$TODAY_UTC -14 days" +%F)"
request GET "/v1/week?start=$PAST_START" "$A_TOKEN"; expect 200 "past week (start $PAST_START)"
echo "$BODY" | jq -e '.isPast==true and .isCurrent==false and .projected==false and .startingBalanceCents==null and .leftAtEndCents==null' >/dev/null || fail "past week without snapshots: $(echo "$BODY" | jq -c 'del(.days,.budget,.overdue)')"
FUTURE_START="$(date -u -d "$TODAY_UTC +14 days" +%F)"
request GET "/v1/week?start=$FUTURE_START" "$A_TOKEN"; expect 200 "future week (start $FUTURE_START)"
echo "$BODY" | jq -e '.projected==true and .isPast==false and .isCurrent==false and .leftAtEndCents!=null and .startingBalanceCents==null' >/dev/null || fail "future week projected: $(echo "$BODY" | jq -c 'del(.days,.budget,.overdue)')"
request GET "/v1/budgets/week?start=$FUTURE_START" "$A_TOKEN"; expect 200 "weekly budget view for a future week"
[[ "$(echo "$BODY" | jq -r '.history[-1].weekStart')" == "$(echo "$BODY" | jq -r '.history[-1].weekStart')" && "$(echo "$BODY" | jq -r '.history|length')" == "8" ]] || fail "history for the viewed week"
pass "week view: current/past/future, weekly budgets"

# ---------------------------------------------------------------------------------------------
# v6 (always on, any engine): bank names + legacy payeeHint, amountVaries, auto-match level, matcher status, rematch rate limit,
# pending view, transactions feed, found-bill routes, WeekView.pendingOutCents.
# ---------------------------------------------------------------------------------------------
echo "--- v6: bank names, auto-match level, matcher status, pending, transactions, found bills"
v6check() { # v6check LABEL JQ-FILTER — on the last $BODY
  if echo "$BODY" | jq -e "$2" >/dev/null 2>&1; then pass "$1"; else echo "$BODY" | head -c 1200; echo; fail "$1"; fi
}
request GET /v1/couple "$A_TOKEN"; expect 200 "household carries autoMatchLevel"
v6check "autoMatchLevel defaults to all" '.autoMatchLevel=="all"'
request PATCH /v1/couple "$A_TOKEN" '{"autoMatchLevel":"sometimes"}'; expect 400 "unknown autoMatchLevel → 400"
request PATCH /v1/couple "$A_TOKEN" '{"autoMatchLevel":"bills"}'; expect 200 "autoMatchLevel → bills"
v6check "Household.autoMatchLevel bills" '.autoMatchLevel=="bills"'
request PATCH /v1/couple "$A_TOKEN" '{"autoMatchLevel":"all"}'; expect 200 "autoMatchLevel back to all"

ELEVEN="$(jq -nc '[range(11) | "Bank name \(.)"]')"
request POST /v1/recurring "$A_TOKEN" "$(jq -nc --arg a "$ACC" --arg d "$TODAY_UTC" --argjson n "$ELEVEN" '{name:"Power",kind:"bill",amountCents:9000,accountId:$a,frequency:"monthly",anchorDate:$d,bankNames:$n}')"
expect 400 "11 bank names → 400"
v6check "errors name bankNames" '.errors | has("bankNames")'
request POST /v1/recurring "$A_TOKEN" "$(jq -nc --arg d "$TODAY_UTC" '{name:"Power",kind:"bill",amountCents:9000,frequency:"monthly",anchorDate:$d,bankNames:["x"]}')"
expect 400 "a 1-character bank name → 400"
request POST /v1/recurring "$A_TOKEN" "$(jq -nc --arg a "$ACC" --arg d "$TODAY_UTC" '{name:"Power",kind:"bill",amountCents:9000,accountId:$a,frequency:"monthly",anchorDate:$d,bankNames:["  City Power ","CITY POWER","PWR CO"],amountVaries:true}')"
expect 201 "bill with bank names (trimmed, case-insensitive dupes dropped) and amountVaries"
POWER="$(echo "$BODY" | jq -r .id)"
v6check "bankNames, payeeHint = first, no learned names, amountVaries" '.bankNames==["City Power","PWR CO"] and .payeeHint=="City Power" and .bankNamesLearned==[] and .amountVaries==true'
request PUT "/v1/recurring/$POWER" "$A_TOKEN" "$(jq -nc --arg a "$ACC" --arg d "$TODAY_UTC" '{name:"Power",kind:"bill",amountCents:9000,accountId:$a,frequency:"monthly",anchorDate:$d,payeeHint:"Power Co"}')"
expect 200 "legacy PUT with payeeHint only"
v6check "payeeHint maps into bankNames; amountVaries defaults to false" '.bankNames==["Power Co"] and .payeeHint=="Power Co" and .amountVaries==false'
request DELETE "/v1/recurring/$POWER/bank-names/learned/0" "$A_TOKEN"; expect 404 "forget a learned name that does not exist → 404"
request DELETE "/v1/recurring/$POWER" "$A_TOKEN"; expect 204 "archive Power"

request GET /v1/matcher/status "$A_TOKEN"; expect 200 "matcher status"
v6check "MatcherStatus shape" 'has("engine") and has("reachable") and has("version") and has("lastRunAt") and (.lastAutoMatched|type=="number") and (.engine=="fuzzy" or .engine=="builtin")'
if [[ -z "${SMOKE_MATCHER_URL:-}" ]]; then
  v6check "no matcher configured → engine builtin, unreachable" '.engine=="builtin" and .reachable==false and .version==null'
fi
request POST /v1/inbox/rematch "$A_TOKEN"; expect 202 "rematch queued"
v6check "202 { queued: true }" '.queued==true'
request POST /v1/inbox/rematch "$A_TOKEN"; expect 429 "second rematch within a minute → 429"
REMATCH_RETRY="$(curl -s -o /dev/null -D - -X POST "$BASE/v1/inbox/rematch" -H "Authorization: Bearer $A_TOKEN" | tr -d '\r' | awk 'tolower($1)=="retry-after:" {print $2}')"
[[ "$REMATCH_RETRY" =~ ^[0-9]+$ && "$REMATCH_RETRY" -ge 1 && "$REMATCH_RETRY" -le 60 ]] || fail "rematch Retry-After header: '$REMATCH_RETRY'"
pass "rematch Retry-After: ${REMATCH_RETRY}s"

request GET /v1/pending "$A_TOKEN"; expect 200 "pending view"
v6check "PendingView shape" '(.totalOutCents|type=="number") and (.totalInCents|type=="number") and (.count|type=="number") and (.groups|type=="array")'
request GET /v1/transactions "$A_TOKEN"; expect 200 "transactions feed"
v6check "TransactionFeed shape" '(.pending|type=="array") and (.items|type=="array") and has("nextBefore") and (.toReviewCount|type=="number")'
request GET "/v1/transactions?accountId=00000000-0000-7000-8000-000000000000" "$A_TOKEN"; expect 400 "feed with a foreign accountId → 400"
request GET "/v1/transactions?before=yesterday" "$A_TOKEN"; expect 400 "feed with a bad cursor → 400"
request GET /v1/bill-suggestions "$A_TOKEN"; expect 200 "found bills (none yet)"
v6check "empty list" 'type=="array" and length==0'
request POST /v1/bill-suggestions/00000000-0000-7000-8000-000000000000/accept "$A_TOKEN" '{}'; expect 404 "accept an unknown suggestion → 404"
request POST /v1/bill-suggestions/00000000-0000-7000-8000-000000000000/dismiss "$A_TOKEN"; expect 404 "dismiss an unknown suggestion → 404"
request GET "/v1/week?start=$TODAY_UTC" "$A_TOKEN"; expect 200 "week view"
v6check "WeekView.pendingOutCents" '.pendingOutCents|type=="number"'

# ---------------------------------------------------------------------------------------------
# v7 (always on): payday weekday, account roles, recurring transfers, bulk create, the Friday plan, sweep routes.
# ---------------------------------------------------------------------------------------------
echo "--- v7: payday, roles, transfers, bulk, plan"
request PATCH /v1/couple "$A_TOKEN" '{"paydayWeekday":7}'; expect 400 "paydayWeekday 7 → 400"
v6check "errors name paydayWeekday" '.errors | has("paydayWeekday")'
request PATCH /v1/couple "$A_TOKEN" '{"paydayWeekday":null}'; expect 400 "paydayWeekday null → 400"
request PATCH /v1/couple "$A_TOKEN" '{"paydayWeekday":5}'; expect 200 "payday Friday"
v6check "Household.paydayWeekday 5, weekStart derived" '.paydayWeekday==5 and .weekStart==5'
request GET "/v1/week?start=2026-11-05" "$A_TOKEN"; expect 200 "week of Thursday 2026-11-05"
v6check "pay week Fri 2026-10-30 … Thu 2026-11-05" '.start=="2026-10-30" and .end=="2026-11-05" and .weekStart==5'
request GET "/v1/plan?date=2026-11-06" "$A_TOKEN"; expect 200 "plan for payday 2026-11-06"
v6check "Plan shape (no bills account yet → billsAccount null, setup.rolesSet false)" \
  '.start=="2026-11-06" and .end=="2026-11-12" and .nextPayday=="2026-11-13" and .billsAccount==null and (.accounts|type=="array") and (.bills|type=="array") and (.funMoney|type=="array") and (.oneOffs|type=="array") and (.income|type=="array") and (.savingsBills|type=="array") and (.checklist|type=="array") and (.leftAtEndCents|type=="number") and (has("sweepOutCents")|not) and (.toReviewCount|type=="number") and .setup.rolesSet==false and (.setup|has("banksLinked") and has("paycheckKnown") and has("billsAdded") and has("partnerJoined"))'
request GET "/v1/plan?date=nope" "$A_TOKEN"; expect 400 "plan with a bad date → 400"
request GET /v1/plan "$A_TOKEN"; expect 200 "plan (today)"
v6check "today's plan" '.isCurrent==true and (.isPayday|type=="boolean")'

request PUT "/v1/accounts/$ACC/role" "$A_TOKEN" '{"role":"boss"}'; expect 400 "unknown role → 400"
request PUT "/v1/accounts/00000000-0000-7000-8000-000000000000/role" "$A_TOKEN" '{"role":"bills"}'; expect 404 "role of a foreign account → 404"
request PUT "/v1/accounts/$ACC/role" "$A_TOKEN" '{"role":"fun","ownerUserId":"00000000-0000-7000-8000-000000000000"}'; expect 400 "owner who is not a member → 400"
v6check "errors name ownerUserId" '.errors | has("ownerUserId")'
request POST /v1/accounts "$A_TOKEN" '{"name":"Fun checking","institution":"Ally Bank","type":"checking","balanceCents":5000}'; expect 201 "a fun-money account"
FUNACC="$(echo "$BODY" | jq -r .id)"
v6check "Account.role defaults to other" '.role=="other" and .ownerUserId==null and .ownerName==null'
A_ID="$(request GET /v1/auth/me "$A_TOKEN" >/dev/null; echo "$BODY" | jq -r .user.id)"
request PUT "/v1/accounts/$FUNACC/role" "$A_TOKEN" "$(jq -nc --arg u "$A_ID" '{role:"fun",ownerUserId:$u}')"; expect 200 "fun money owned by Alex"
v6check "role fun, owner Alex" ".role==\"fun\" and .ownerUserId==\"$A_ID\" and .ownerName==\"Alex\""

request POST /v1/recurring "$A_TOKEN" "$(jq -nc --arg a "$ACC" '{name:"Fun money",kind:"transfer",amountCents:2000,frequency:"weekly",anchorDate:"2026-10-30",fromAccountId:$a}')"
expect 400 "transfer without toAccountId → 400"
v6check "errors name toAccountId" '.errors | has("toAccountId")'
request POST /v1/recurring "$A_TOKEN" "$(jq -nc --arg a "$ACC" '{name:"Fun money",kind:"transfer",amountCents:2000,frequency:"weekly",anchorDate:"2026-10-30",fromAccountId:$a,toAccountId:$a}')"
expect 400 "transfer to the same account → 400"
request POST /v1/recurring "$A_TOKEN" "$(jq -nc --arg a "$ACC" '{name:"Fun money",kind:"transfer",amountCents:2000,frequency:"weekly",anchorDate:"2026-10-30",fromAccountId:$a,toAccountId:"00000000-0000-7000-8000-000000000000"}')"
expect 400 "transfer to a foreign account → 400"
request POST /v1/recurring "$A_TOKEN" "$(jq -nc --arg a "$ACC" --arg f "$FUNACC" '{name:"Fun money (Alex)",kind:"transfer",amountCents:2000,frequency:"weekly",anchorDate:"2026-10-30",fromAccountId:$a,toAccountId:$f}')"
expect 201 "weekly \$20 transfer → Alex's fun account"
XFER="$(echo "$BODY" | jq -r .id)"
v6check "RecurringItem carries from/to (accountId = from)" ".kind==\"transfer\" and .fromAccountId==\"$ACC\" and .toAccountId==\"$FUNACC\" and .accountId==\"$ACC\" and .toAccountName==\"Fun checking\" and (.fromAccountName|type==\"string\")"
request GET "/v1/recurring?kind=transfer" "$A_TOKEN"; expect 200 "list transfers"
v6check "one transfer listed" "length==1 and .[0].id==\"$XFER\""
request GET /v1/summary "$A_TOKEN"; expect 200 "summary with a transfer item"
v6check "byKind keeps bill/subscription/income only (transfers are not spending)" '[.byKind[].kind] == ["bill","subscription","income"]'

BULK_BAD="$(jq -nc '{items:([range(5) | {name:"Line \(.)",kind:"bill",amountCents:1000,frequency:"monthly",anchorDate:"2026-11-01"}] | .[3].amountCents = 0)}')"
request POST /v1/recurring/bulk "$A_TOKEN" "$BULK_BAD"; expect 400 "bulk with a bad line → 400, nothing written"
v6check "per-index errors" '.errors | has("items[3].amountCents") and (keys|length==1)'
request POST /v1/recurring/bulk "$A_TOKEN" "$(jq -nc '{items:[range(101) | {name:"x",kind:"bill",amountCents:100,frequency:"monthly",anchorDate:"2026-11-01"}]}')"; expect 400 "bulk of 101 → 400"
request POST /v1/recurring/bulk "$A_TOKEN" "$(jq -nc --arg c "00000000-0000-7000-8000-000000000000" '{items:[{name:"A",kind:"bill",amountCents:100,frequency:"monthly",anchorDate:"2026-11-01"},{name:"B",kind:"bill",amountCents:100,frequency:"monthly",anchorDate:"2026-11-01",categoryId:$c}]}')"
expect 400 "bulk with a foreign categoryId → 400"
v6check "reference error per index" '.errors | has("items[1].categoryId")'
request GET "/v1/recurring?kind=bill" "$A_TOKEN"; BILLS_BEFORE="$(echo "$BODY" | jq length)"
request POST /v1/recurring/bulk "$A_TOKEN" '{"items":[{"name":"Gym","kind":"bill","amountCents":2999,"frequency":"monthly","anchorDate":"2026-11-03","bankNames":["Planet Fitness"]},{"name":"Water","kind":"bill","amountCents":6000,"frequency":"monthly","anchorDate":"2026-11-20","amountVaries":true}]}'
expect 201 "bulk create 2 bills"
v6check "201 { items } in order" '(.items|length==2) and .items[0].name=="Gym" and .items[0].bankNames==["Planet Fitness"] and .items[1].amountVaries==true'
BULK_IDS="$(echo "$BODY" | jq -r '.items[].id')"
request GET "/v1/recurring?kind=bill" "$A_TOKEN"; [[ "$(echo "$BODY" | jq length)" == "$((BILLS_BEFORE + 2))" ]] || fail "bulk added exactly 2 bills (the failed bulk wrote nothing)"
request GET "/v1/activity?limit=20" "$A_TOKEN"
v6check "one activity row 'added 2 bills'" '[.events[] | select(.kind=="recurring.created" and .data.bulk==true and .data.count==2 and .summary=="added 2 bills")] | length==1'
for id in $BULK_IDS; do request DELETE "/v1/recurring/$id" "$A_TOKEN"; done
request DELETE "/v1/recurring/$XFER" "$A_TOKEN"; expect 204 "archive the transfer"

request POST /v1/plan/sweep/done "$A_TOKEN" '{}'; expect 400 "sweep done without weekStart → 400"
request POST /v1/plan/sweep/done "$A_TOKEN" '{"weekStart":"2026-01-02"}'; expect 404 "sweep done for a week without a frozen sweep → 404"
request DELETE "/v1/plan/sweep/done?weekStart=2026-01-02" "$A_TOKEN"; expect 404 "untick a week without a sweep → 404"
request DELETE /v1/plan/sweep/done "$A_TOKEN"; expect 400 "untick without weekStart → 400"
request PUT "/v1/accounts/$FUNACC/role" "$A_TOKEN" '{"role":"other"}'; expect 200 "fun account back to other"
request DELETE "/v1/accounts/$FUNACC" "$A_TOKEN"; expect 204 "archive the fun account"

# v6 matcher flow (SMOKE_MATCHER_URL + SMOKE_SIMPLEFIN_PORT + SMOKE_SCHEDULER_WAIT): a fresh household against the real matcher
# and the fake Bridge's v6 dataset. Defined here, run inside the SimpleFIN block below.
v6_matcher_flow() {
  local C_EMAIL="casey+$RUN@example.com" C_TOKEN C_ACC RENT6 OIL6 WATER6 GYM6 LINK6 EXT6 TX_RENT6 TX_JIFFY6 TX_WATER6 NETFLIX6 SPOTIFY6 NETFLIX_ITEM
  local MURL="${SMOKE_MATCHER_URL%/}" MATCHER_STARTED=""
  v6wait() { # v6wait LABEL PATH JQ-FILTER [SECONDS]
    local label="$1" path="$2" filter="$3" seconds="${4:-$SMOKE_SCHEDULER_WAIT}"
    for _ in $(seq 1 "$seconds"); do
      request GET "$path" "$C_TOKEN"
      if [[ "$STATUS" == "200" ]] && echo "$BODY" | jq -e "$filter" >/dev/null 2>&1; then pass "$label"; return 0; fi
      sleep 1
    done
    echo "$BODY" | head -c 1500; echo; fail "$label: not observed within ${seconds}s"
  }

  echo "--- v6: fuzzy matcher at $MURL (fake Bridge v6 dataset)"
  if ! curl -fs "$MURL/health" >/dev/null 2>&1; then
    local mport="${MURL##*:}" mdir
    mdir="$(cd "$SCRIPT_DIR/../../matcher" 2>/dev/null && pwd)" || fail "matcher not reachable at $MURL and apps/matcher not found"
    command -v uv >/dev/null || fail "matcher not reachable at $MURL and uv is not installed to start it"
    (cd "$mdir" && MATCHER_KEY="${SMOKE_MATCHER_KEY:?SMOKE_MATCHER_KEY is required}" exec uv run --frozen uvicorn matcher.app:app --host 127.0.0.1 --port "$mport" >/dev/null 2>&1) &
    MATCHER_STARTED="$mport"
    for _ in $(seq 1 100); do curl -fs "$MURL/health" >/dev/null 2>&1 && break; sleep 0.2; done
  fi
  curl -fs "$MURL/health" | jq -e '.status=="ok"' >/dev/null || fail "matcher /health"
  pass "matcher up ($(curl -fs "$MURL/health" | jq -r .version))"
  if [[ -n "${SMOKE_MATCHER_KEY:-}" ]]; then
    [[ "$(curl -s -o /dev/null -w '%{http_code}' -X POST "$MURL/v1/match" -H 'Content-Type: application/json' -d '{}')" == "401" ]] || fail "matcher without X-Matcher-Key → 401"
    pass "matcher refuses requests without X-Matcher-Key (401)"
  fi

  request POST /v1/auth/register '' "$(jq -nc --arg e "$C_EMAIL" --arg p "$PASSWORD" '{email:$e,password:$p,displayName:"Casey"}')"; expect 201 "register Casey"
  C_TOKEN="$(echo "$BODY" | jq -r .accessToken)"; local C_REFRESH; C_REFRESH="$(echo "$BODY" | jq -r .refreshToken)"
  request POST /v1/couple/code "$C_TOKEN" '{"name":"Casey household"}'; expect 201 "Casey creates a household"
  request POST /v1/auth/refresh '' "$(jq -nc --arg r "$C_REFRESH" '{refreshToken:$r}')"; expect 200 "refresh Casey"
  C_TOKEN="$(echo "$BODY" | jq -r .accessToken)"
  request POST /v1/accounts "$C_TOKEN" '{"name":"Casey checking","institution":"Demo Bank","type":"checking","balanceCents":300000}'; expect 201 "Casey checking"
  C_ACC="$(echo "$BODY" | jq -r .id)"
  v6wait "matcher status: configured → engine fuzzy, reachable, version (an 'unreachable' probe is cached 3 s)" /v1/matcher/status \
    '.engine=="fuzzy" and .reachable==true and .configured==true and (.version|type=="string") and .lastRunAt==null' 15

  request POST /v1/recurring "$C_TOKEN" "$(jq -nc --arg a "$C_ACC" --arg d "$TODAY_UTC" '{name:"Rent",kind:"bill",amountCents:185000,accountId:$a,frequency:"monthly",anchorDate:$d,bankNames:["Oakwood"]}')"
  expect 201 "Rent 1850.00 monthly, bank shows it as Oakwood"
  RENT6="$(echo "$BODY" | jq -r .id)"
  request POST /v1/recurring "$C_TOKEN" "$(jq -nc --arg a "$C_ACC" --arg d "$TODAY_UTC" '{name:"Oil change",kind:"bill",amountCents:6000,accountId:$a,frequency:"once",anchorDate:$d}')"
  expect 201 "one-off Oil change 60.00 today, no bank name"
  OIL6="$(echo "$BODY" | jq -r .id)"

  local V6TOKEN; V6TOKEN="$(printf 'http://127.0.0.1:%s/simplefin/claim/v6' "$SMOKE_SIMPLEFIN_PORT" | base64 -w0)"
  request POST /v1/banks/simplefin/claim "$C_TOKEN" "$(jq -nc --arg t "$V6TOKEN" '{setupToken:$t,name:"Casey bank"}')"; expect 201 "claim the v6 Bridge (first sync inline, fuzzy matcher)"
  LINK6="$(echo "$BODY" | jq -r .id)"; EXT6="$(echo "$BODY" | jq -r '.accounts[0].id')"
  request PUT "/v1/banks/accounts/$EXT6" "$C_TOKEN" "$(jq -nc --arg a "$C_ACC" '{accountId:$a}')"; expect 200 "map Everyday Checking → Casey checking"

  request GET "/v1/inbox?limit=200" "$C_TOKEN"; expect 200 "inbox after the first sync"
  TX_RENT6="$(echo "$BODY" | jq -r '.items[] | select(.description|startswith("ACH DEBIT OAKWOOD")) | .id')"
  TX_JIFFY6="$(echo "$BODY" | jq -r '.items[] | select(.description=="JIFFY LUBE #42") | .id')"
  v6check "Rent row auto-matched by the fuzzy matcher (confirmed auto, no reviewer, engine fuzzy, reason, score ≥ 85)" \
    ".items[] | select(.id==\"$TX_RENT6\") | .status==\"confirmed\" and .confirmed.auto==true and .confirmed.by==null and .match.itemId==\"$RENT6\" and .matchDetail.engine==\"fuzzy\" and (.matchDetail.reason|length>0) and .matchScore>=85 and .matchDetail.name>=90"
  echo "$BODY" | jq -r ".items[] | select(.id==\"$TX_RENT6\") | \"       Rent: \\(.matchScore) · \\(.matchDetail.reason)\""
  echo "$BODY" | jq -r ".items[] | select(.id==\"$TX_JIFFY6\") | \"       Jiffy Lube: \\(.status) \\(.matchScore) · \\(.matchDetail.reason // \"-\")\""
  v6check "JIFFY LUBE −54.20 is only a suggestion for Oil change (name too different to auto-apply)" \
    ".items[] | select(.id==\"$TX_JIFFY6\") | .status==\"pending\" and .suggestion.kind==\"occurrence\" and .suggestion.itemId==\"$OIL6\" and .matchScore<85 and .matchDetail.engine==\"fuzzy\""
  request GET "/v1/calendar?from=$TODAY_UTC&to=$TODAY_UTC" "$C_TOKEN"
  v6check "Rent occurrence paid by the bank automatically (paidBy null, auto, source bank, 1850.00)" \
    ".occurrences[] | select(.itemId==\"$RENT6\") | .paid.auto==true and .paid.paidBy==null and .paid.source==\"bank\" and .paid.amountCents==185000"
  request GET "/v1/recurring/$RENT6" "$C_TOKEN"
  v6check "learned bank name appended (user names untouched)" '.bankNames==["Oakwood"] and (.bankNamesLearned|index("OAKWOOD PROPERTY MGMT RENT"))!=null'
  request GET "/v1/activity?limit=50" "$C_TOKEN"
  v6check "transaction.automatched carries data.reason (and engine fuzzy)" '[.events[] | select(.kind=="transaction.automatched" and (.data.reason|length>0) and .data.engine=="fuzzy")] | length>=1'
  request GET /v1/matcher/status "$C_TOKEN"
  v6check "status: last run recorded with its auto-matched count" '.lastRunAt!=null and .lastAutoMatched>=1 and .lastRunEngine=="fuzzy"'

  echo "--- v6: pending per account, transactions feed"
  request GET /v1/pending "$C_TOKEN"; expect 200 "pending view"
  v6check "pending grouped by account: Casey checking first with STARBUCKS −5.25, balance → available" \
    ".count==1 and .totalOutCents==525 and .groups[0].accountId==\"$C_ACC\" and .groups[0].pendingCount==1 and .groups[0].pendingOutCents==525 and .groups[0].pendingInCents==0 and .groups[0].availableCents==319475 and .groups[0].balanceCents==320000 and .groups[0].items[0].pending==true"
  request GET /v1/transactions "$C_TOKEN"; expect 200 "transactions feed"
  v6check "feed: pending pinned (STARBUCKS), items posted only (newest first), toReviewCount" \
    '(.pending|length==1) and .pending[0].description=="STARBUCKS STORE 1234" and (.items|length>=9) and ([.items[] | select(.pending)] | length==0) and (.items[0].postedOn >= .items[-1].postedOn) and .toReviewCount>=8'
  request GET "/v1/transactions?limit=3" "$C_TOKEN"; expect 200 "feed page 1 (limit 3)"
  local NEXT6; NEXT6="$(echo "$BODY" | jq -r .nextBefore)"
  [[ "$NEXT6" != "null" && "$(echo "$BODY" | jq '.items|length')" == "3" ]] || fail "feed page 1 should have a cursor"
  request GET "/v1/transactions?limit=3&before=$NEXT6" "$C_TOKEN"; expect 200 "feed page 2"
  v6check "page 2 carries no pending list (first page only)" '(.pending|length==0) and (.items|length==3)'
  request GET "/v1/transactions?q=netflix&accountId=$C_ACC" "$C_TOKEN"; expect 200 "feed search + account filter"
  v6check "4 NETFLIX rows" '(.items|length==4) and all(.items[]; .description|test("NETFLIX"))'
  request GET "/v1/week?start=$TODAY_UTC" "$C_TOKEN"
  v6check "WeekView.pendingOutCents = 525" '.pendingOutCents==525'

  echo "--- v6: add a bank name → immediate rematch → auto"
  request PUT "/v1/recurring/$OIL6" "$C_TOKEN" "$(jq -nc --arg a "$C_ACC" --arg d "$TODAY_UTC" '{name:"Oil change",kind:"bill",amountCents:6000,accountId:$a,frequency:"once",anchorDate:$d,bankNames:["Jiffy Lube"]}')"
  expect 200 "Oil change: bank shows it as Jiffy Lube"
  v6wait "rematch (debounced) auto-matches JIFFY LUBE to Oil change at 54.20" "/v1/inbox?limit=200" \
    ".items[] | select(.id==\"$TX_JIFFY6\") | .status==\"confirmed\" and .confirmed.auto==true and .match.itemId==\"$OIL6\" and .matchDetail.engine==\"fuzzy\" and .matchScore>=85" 40
  request GET "/v1/calendar?from=$TODAY_UTC&to=$TODAY_UTC" "$C_TOKEN"
  v6check "one-off paid at the bank amount (variance −5.80)" ".occurrences[] | select(.itemId==\"$OIL6\") | .paid.amountCents==5420 and .varianceCents==-580 and .paid.auto==true"

  echo "--- v6: found bills"
  v6wait "found bills: Netflix monthly 15.49 and Spotify suggested" "/v1/bill-suggestions" \
    '([.[] | select(.name=="Netflix" and .frequency=="monthly" and .amountCents==1549 and .kind=="subscription" and .sampleCount==4)] | length==1) and ([.[] | select(.bankName|test("SPOTIFY"))] | length==1)' 40
  NETFLIX6="$(echo "$BODY" | jq -r '.[] | select(.name=="Netflix") | .id')"
  SPOTIFY6="$(echo "$BODY" | jq -r '.[] | select(.bankName|test("SPOTIFY")) | .id')"
  echo "$BODY" | jq -r '.[] | "       \(.name) · \(.amountCents) \(.frequency) · next \(.nextDue) · bank \(.bankName) · score \(.score) · last \(.lastSeen)"'
  request POST "/v1/bill-suggestions/$NETFLIX6/accept" "$C_TOKEN" '{"kind":"income"}'; expect 400 "accept as income (money out) → 400"
  request POST "/v1/bill-suggestions/$NETFLIX6/accept" "$C_TOKEN" '{}'; expect 201 "accept Netflix"
  NETFLIX_ITEM="$(echo "$BODY" | jq -r .item.id)"
  v6check "item created with the detected bank name; the 4 past payments matched" \
    '.matched==4 and .item.name=="Netflix" and .item.kind=="subscription" and .item.frequency=="monthly" and .item.amountCents==1549 and (.item.bankNames|length==1) and (.item.bankNames[0]|test("NETFLIX"))'
  request POST "/v1/bill-suggestions/$NETFLIX6/accept" "$C_TOKEN" '{}'; expect 409 "accept twice → 409"
  request GET "/v1/recurring/$NETFLIX_ITEM/payments?limit=6" "$C_TOKEN"; expect 200 "Netflix payments"
  v6check "4 payments linked to their bank transactions" '[.[] | select(.transaction!=null and .paid.source=="bank")] | length==4'
  request POST "/v1/bill-suggestions/$SPOTIFY6/dismiss" "$C_TOKEN"; expect 204 "Spotify: not a bill"
  request POST "/v1/bill-suggestions/$SPOTIFY6/dismiss" "$C_TOKEN"; expect 204 "dismiss twice is a no-op"
  request GET /v1/bill-suggestions "$C_TOKEN"
  v6check "neither Netflix nor Spotify is listed any more" "[.[] | select(.id==\"$NETFLIX6\" or .id==\"$SPOTIFY6\")] | length==0"

  echo "--- v6: auto-match level off → suggestions only; bills → auto"
  request PATCH /v1/couple "$C_TOKEN" '{"autoMatchLevel":"off"}'; expect 200 "auto-match level off"
  request POST /v1/recurring "$C_TOKEN" "$(jq -nc --arg a "$C_ACC" --arg d "$TODAY_UTC" '{name:"Water",kind:"bill",amountCents:4500,accountId:$a,frequency:"monthly",anchorDate:$d,bankNames:["City Water"],amountVaries:true}')"
  expect 201 "Water ~45.00 monthly (amount varies), bank shows it as City Water"
  WATER6="$(echo "$BODY" | jq -r .id)"
  curl -fs -X POST "$FAKE/__phase/v6/2" >/dev/null || fail "could not enable v6 phase 2"
  v6wait "CITY WATER UTIL synced as a suggestion only (level off)" "/v1/inbox?limit=200" \
    ".items[] | select(.description==\"CITY WATER UTIL\") | .status==\"pending\" and .suggestion.kind==\"occurrence\" and .suggestion.itemId==\"$WATER6\" and .matchScore>=85 and .confirmed==null"
  TX_WATER6="$(echo "$BODY" | jq -r '.items[] | select(.description=="CITY WATER UTIL") | .id')"
  sleep 14 # the sync queued found bills again (10 s debounce): Spotify must stay dismissed, Netflix is now a known bill
  request GET /v1/bill-suggestions "$C_TOKEN"
  v6check "dismissed Spotify never returns; Netflix not suggested again" '[.[] | select((.bankName|test("SPOTIFY|NETFLIX")) or .name=="Netflix")] | length==0'
  request GET "/v1/inbox?limit=200" "$C_TOKEN"
  v6check "still a suggestion after the rematches (level off)" ".items[] | select(.id==\"$TX_WATER6\") | .status==\"pending\""
  request PATCH /v1/couple "$C_TOKEN" '{"autoMatchLevel":"bills"}'; expect 200 "auto-match level bills (queues a rematch)"
  v6wait "rematch auto-matches CITY WATER UTIL to Water" "/v1/inbox?limit=200" \
    ".items[] | select(.id==\"$TX_WATER6\") | .status==\"confirmed\" and .confirmed.auto==true and .match.itemId==\"$WATER6\"" 40

  echo "--- v6: the matcher goes away → built-in fallback"
  if [[ -n "$MATCHER_STARTED" ]]; then
    pkill -f "uvicorn matcher.app:app --host 127.0.0.1 --port $MATCHER_STARTED" 2>/dev/null || true
    for _ in $(seq 1 50); do curl -fs "$MURL/health" >/dev/null 2>&1 || break; sleep 0.2; done
    ! curl -fs "$MURL/health" >/dev/null 2>&1 || fail "matcher still running"
    pass "matcher stopped"
    v6wait "status: unreachable, engine builtin, found bills paused" /v1/matcher/status '.reachable==false and .engine=="builtin" and .foundBills==false' 30
    request POST /v1/recurring "$C_TOKEN" "$(jq -nc --arg a "$C_ACC" --arg d "$TODAY_UTC" '{name:"Gym",kind:"bill",amountCents:2999,accountId:$a,frequency:"weekly",anchorDate:$d,bankNames:["Planet Fitness"]}')"
    expect 201 "Gym 29.99 weekly, bank shows it as Planet Fitness"
    GYM6="$(echo "$BODY" | jq -r .id)"
    curl -fs -X POST "$FAKE/__phase/v6/3" >/dev/null || fail "could not enable v6 phase 3"
    v6wait "sync still works: PLANET FITNESS auto-matched by the built-in engine (bank-name rule)" "/v1/inbox?limit=200" \
      ".items[] | select(.description==\"PLANET FITNESS\") | .status==\"confirmed\" and .confirmed.auto==true and .match.itemId==\"$GYM6\" and .matchDetail.engine==\"builtin\""
    request GET /v1/banks "$C_TOKEN"
    v6check "bank link healthy" '.[0].lastError==null'
    request GET /v1/matcher/status "$C_TOKEN"
    v6check "last run engine builtin" '.lastRunEngine=="builtin" and .engine=="builtin"'
  else
    echo "       (matcher was already running when the smoke started: stop-the-matcher fallback checks skipped)"
  fi

  request GET "/v1/activity?limit=100" "$C_TOKEN"
  v6check "v6 activity: dismissed found bill, found bill added, auto-match level setting" \
    '[.events[].kind] as $k | ($k | index("bill_suggestion.dismissed")) != null and ([.events[] | select(.kind=="recurring.created" and .data.fromSuggestion==true)] | length==1) and ([.events[] | select(.kind=="couple.settings" and .data.autoMatchLevel=="off")] | length==1)'
  request DELETE "/v1/banks/$LINK6" "$C_TOKEN"; expect 204 "unlink Casey's bank"
  request POST /v1/couple/leave "$C_TOKEN"; expect 200 "Casey leaves (household deleted)"
}

# v7 Friday flow (SMOKE_SIMPLEFIN_PORT + SMOKE_SCHEDULER_WAIT): a fresh couple with their real shape on the fake Bridge's v7 dataset —
# US Bank (bills) with 3 pending and an available balance, Ally savings with a student loan, two Ally fun accounts, a Friday paycheck,
# $20 to each fun account, the sweep. Today is made their payday. Defined here, run inside the SimpleFIN block below (any engine).
v7_friday_flow() {
  local T_EMAIL="travis+$RUN@example.com" J_EMAIL="jordan+$RUN@example.com" T_TOKEN T_REFRESH J_TOKEN T_ID J_ID LINK7 USB SAV F1 F2
  local PAY7 LOAN7 FUNT FUNJ D0 D1 D2 D3 D4 D10 D12 PAYDAY7 PLANNED7 SWEEP_TX SW0
  v7wait() { # v7wait LABEL PATH JQ-FILTER [SECONDS]
    local label="$1" path="$2" filter="$3" seconds="${4:-$SMOKE_SCHEDULER_WAIT}"
    for _ in $(seq 1 "$seconds"); do
      request GET "$path" "$T_TOKEN"
      if [[ "$STATUS" == "200" ]] && echo "$BODY" | jq -e "$filter" >/dev/null 2>&1; then pass "$label"; return 0; fi
      sleep 1
    done
    echo "$BODY" | head -c 2500; echo; fail "$label: not observed within ${seconds}s"
  }
  v7check() { if echo "$BODY" | jq -e "$2" >/dev/null 2>&1; then pass "$1"; else echo "$BODY" | head -c 2500; echo; fail "$1"; fi; }

  echo "--- v7: the Friday routine (fake Bridge v7 dataset)"
  D0="$TODAY_UTC"; PAYDAY7="$(date -u -d "$D0" +%w)"
  for n in 1 2 3 4 10 12; do printf -v "D$n" '%s' "$(date -u -d "$D0 +$n days" +%F)"; done
  request POST /v1/auth/register '' "$(jq -nc --arg e "$T_EMAIL" --arg p "$PASSWORD" '{email:$e,password:$p,displayName:"Travis"}')"; expect 201 "register Travis"
  T_TOKEN="$(echo "$BODY" | jq -r .accessToken)"; T_REFRESH="$(echo "$BODY" | jq -r .refreshToken)"; T_ID="$(echo "$BODY" | jq -r .user.id)"
  request POST /v1/couple/code "$T_TOKEN" '{"name":"Travis & Jordan"}'; expect 201 "Travis creates the household"
  local CODE7; CODE7="$(echo "$BODY" | jq -r .code)"
  request POST /v1/auth/refresh '' "$(jq -nc --arg r "$T_REFRESH" '{refreshToken:$r}')"; expect 200 "refresh Travis"
  T_TOKEN="$(echo "$BODY" | jq -r .accessToken)"
  request POST /v1/auth/register '' "$(jq -nc --arg e "$J_EMAIL" --arg p "$PASSWORD" '{email:$e,password:$p,displayName:"Jordan"}')"; expect 201 "register Jordan"
  J_TOKEN="$(echo "$BODY" | jq -r .accessToken)"; J_ID="$(echo "$BODY" | jq -r .user.id)"
  request POST /v1/couple/join "$J_TOKEN" "$(jq -nc --arg c "$CODE7" '{code:$c}')"; expect 200 "Jordan joins"
  J_TOKEN="$(echo "$BODY" | jq -r .accessToken)"
  request PATCH /v1/couple "$T_TOKEN" "$(jq -nc --argjson p "$PAYDAY7" '{paydayWeekday:$p}')"; expect 200 "payday = today's weekday ($PAYDAY7)"

  local V7TOKEN; V7TOKEN="$(printf 'http://127.0.0.1:%s/simplefin/claim/v7' "$SMOKE_SIMPLEFIN_PORT" | base64 -w0)"
  request POST /v1/banks/simplefin/claim "$T_TOKEN" "$(jq -nc --arg t "$V7TOKEN" '{setupToken:$t,name:"Their banks"}')"; expect 201 "claim the v7 Bridge (US Bank + Ally ×3)"
  LINK7="$(echo "$BODY" | jq -r .id)"
  v7check "the first sync stays inside the Bridge's limits (no 45/90-day notices)" '.lastError==null and (.bridgeErrors|length==0)'
  local EXTS; EXTS="$BODY"
  map7() { # map7 EXTERNAL-ID NAME TYPE INSTITUTION → prints the new account id
    local ext; ext="$(echo "$EXTS" | jq -r --arg x "$1" '.accounts[] | select(.externalId==$x) | .id')"
    request PUT "/v1/banks/accounts/$ext" "$T_TOKEN" "$(jq -nc --arg n "$2" --arg t "$3" --arg i "$4" '{create:{name:$n,type:$t,institution:$i}}')"
    [[ "$STATUS" == "200" ]] || { echo "$BODY"; fail "map $1"; }
    echo "$BODY" | jq -r .accountId
  }
  USB="$(map7 ACT-V7-USB "US Bank" checking "U.S. Bank")"; SAV="$(map7 ACT-V7-SAV "Ally High Yield Savings" savings "Ally Bank")"
  F1="$(map7 ACT-V7-FUN1 "Fun (Travis)" checking "Ally Bank")"; F2="$(map7 ACT-V7-FUN2 "Fun (Jordan)" checking "Ally Bank")"
  [[ "$USB" =~ ^[0-9a-f-]{36}$ && "$SAV" =~ ^[0-9a-f-]{36}$ && "$F1" =~ ^[0-9a-f-]{36}$ && "$F2" =~ ^[0-9a-f-]{36}$ ]] || fail "four accounts created by mapping"
  pass "four bank accounts mapped (US Bank, Ally savings, two Ally fun accounts)"

  # Her notes, as the client-side parser turns them into items (Paste from notes → POST /v1/recurring/bulk).
  request POST /v1/recurring/bulk "$T_TOKEN" "$(jq -nc --arg d2 "$D2" --arg d3 "$D3" --arg d4 "$D4" --arg d10 "$D10" --arg d12 "$D12" --arg d0 "$D0" --arg sav "$SAV" '{items:[
      {name:"Rent",kind:"bill",amountCents:185000,frequency:"monthly",anchorDate:$d2,bankNames:["Rent"]},
      {name:"Phone",kind:"bill",amountCents:8500,frequency:"monthly",anchorDate:$d3,bankNames:["Phone"]},
      {name:"Internet",kind:"subscription",amountCents:5750,frequency:"monthly",anchorDate:$d4,bankNames:["Internet"]},
      {name:"Netflix",kind:"subscription",amountCents:1549,frequency:"monthly",anchorDate:$d10,bankNames:["Netflix"]},
      {name:"Water",kind:"bill",amountCents:6000,frequency:"monthly",anchorDate:$d12,amountVaries:true,bankNames:["Water"]},
      {name:"Student loan",kind:"bill",amountCents:25000,frequency:"monthly",anchorDate:$d0,accountId:$sav,bankNames:["NELNET STUDENT LOAN"]}]}')"
  expect 201 "bulk create the 6 lines of her note"
  LOAN7="$(echo "$BODY" | jq -r '.items[] | select(.name=="Student loan") | .id')"
  [[ "$(echo "$BODY" | jq '.items|length')" == "6" ]] || fail "6 items created"
  request POST /v1/recurring "$T_TOKEN" "$(jq -nc --arg a "$USB" --arg d "$D0" '{name:"Paycheck",kind:"income",amountCents:150000,frequency:"weekly",anchorDate:$d,accountId:$a,bankNames:["ACME CORP PAYROLL"]}')"
  expect 201 "Travis's weekly paycheck (Friday)"; PAY7="$(echo "$BODY" | jq -r .id)"
  request POST /v1/recurring "$T_TOKEN" "$(jq -nc --arg a "$USB" --arg d "$D1" '{name:"Oil change",kind:"bill",amountCents:6000,frequency:"once",anchorDate:$d,accountId:$a}')"
  expect 201 "one-off: oil change 60 tomorrow"
  request POST /v1/recurring "$T_TOKEN" "$(jq -nc --arg a "$USB" --arg f "$F1" --arg d "$D0" '{name:"Fun money · Travis",kind:"transfer",amountCents:2000,frequency:"weekly",anchorDate:$d,fromAccountId:$a,toAccountId:$f}')"
  expect 201 "\$20 a week → Fun (Travis)"; FUNT="$(echo "$BODY" | jq -r .id)"
  request POST /v1/recurring "$T_TOKEN" "$(jq -nc --arg a "$USB" --arg f "$F2" --arg d "$D0" '{name:"Fun money · Jordan",kind:"transfer",amountCents:2000,frequency:"weekly",anchorDate:$d,fromAccountId:$a,toAccountId:$f}')"
  expect 201 "\$20 a week → Fun (Jordan)"; FUNJ="$(echo "$BODY" | jq -r .id)"

  v7wait "the paycheck posted and auto-matched (rematch after the item was added)" "/v1/calendar?from=$D0&to=$D0" \
    ".occurrences[] | select(.itemId==\"$PAY7\") | .paid.source==\"bank\" and .paid.amountCents==150000"

  # Roles: bills, savings, fun × 2 with owners.
  request PUT "/v1/accounts/$USB/role" "$T_TOKEN" '{"role":"bills"}'; expect 200 "US Bank = bills"
  v7check "Account.role bills" '.role=="bills"'
  request PUT "/v1/accounts/$SAV/role" "$T_TOKEN" '{"role":"savings"}'; expect 200 "Ally High Yield Savings = savings"
  request PUT "/v1/accounts/$F1/role" "$T_TOKEN" "$(jq -nc --arg u "$T_ID" '{role:"fun",ownerUserId:$u}')"; expect 200 "Fun (Travis) = fun, owner Travis"
  v7check "fun owner" ".role==\"fun\" and .ownerUserId==\"$T_ID\" and .ownerName==\"Travis\""
  request PUT "/v1/accounts/$F2/role" "$J_TOKEN" "$(jq -nc --arg u "$J_ID" '{role:"fun",ownerUserId:$u}')"; expect 200 "Fun (Jordan) = fun, owner Jordan (set by Jordan)"

  request GET "/v1/inbox?status=pending&limit=200" "$T_TOKEN"; expect 200 "To review"
  v7check "fun-account transactions are never in To review" "[.items[] | select(.accountId==\"$F1\" or .accountId==\"$F2\")] | length==0"
  request GET "/v1/accounts/$F1/transactions" "$T_TOKEN"; expect 200 "Fun (Travis) transactions"
  v7check "… but they are listed on the account (TARGET, confirmed, nothing attached)" '[.items[] | select(.description=="TARGET 0042" and .status=="confirmed")] | length==1'

  # The Friday plan: her note.
  v7wait "plan: Left on Thursday = \$253.17" /v1/plan '.leftAtEndCents==25317' 15
  v7check "available now = US Bank available \$2,345.67 (bank-reported), 3 pending −\$62.40 informational" \
    ".billsAccount.id==\"$USB\" and .billsAccount.availableCents==234567 and .billsAccount.availableFromBank==true and .billsAccount.balanceCents==240807 and (.billsAccount.pending|length==3) and .billsAccount.pendingOutCents==6240"
  v7check "bills before next payday \$1,992.50 (Rent, Phone, Internet), fun money \$40.00, one-offs \$60.00, no income still to come" \
    '.billsOutCents==199250 and .funOutCents==4000 and .oneOffOutCents==6000 and .incomeInCents==0 and .paidThisWeekCents==0 and ([.bills[].occurrence.name]|sort)==["Internet","Phone","Rent"] and (.funMoney|length==2) and (.oneOffs|length==1) and .oneOffs[0].kind=="oneoff"'
  v7check "student loan is a savings bill (shown, not in the math); paycheck line ticked" \
    "(.savingsBills|length==1) and .savingsBills[0].occurrence.itemId==\"$LOAN7\" and (.income|length==1) and .income[0].occurrence.paid!=null"
  v7check "pay week = payday … payday + 6, isPayday, accounts strip bills · savings · fun · fun" \
    ".start==\"$D0\" and .end==\"$(date -u -d "$D0 +6 days" +%F)\" and .isPayday==true and .isCurrent==true and ([.accounts[].role][:4])==[\"bills\",\"savings\",\"fun\",\"fun\"] and .setup.rolesSet==true and .setup.banksLinked==true and .setup.paycheckKnown==true and .setup.billsAdded==true and .setup.partnerJoined==true"
  v7check "payday checklist: paycheck ✓ (auto, its transaction), two \$20 fun transfers open" \
    '([.checklist[] | select(.key=="paycheck")][0] | .done==true and .auto==true and .transactionId!=null and .amountCents==150000) and ([.checklist[] | select(.key=="fun" and .done==false and .amountCents==2000)] | length==2)'

  v7check "payday: no savings move (it is the bottom line on the last day of the week), nothing taken out of the plan" \
    '.sweep==null and ([.checklist[] | select(.key=="sweep")] | length==0) and .leftAtEndCents==25317'

  request GET /v1/banks "$T_TOKEN"; expect 200 "linked banks"
  v7check "feed diagnostics: US Bank sent 3 pending, available balance reported; savings without available" \
    '(.[0].accounts[] | select(.externalId=="ACT-V7-USB") | .lastFeed.pending==3 and .lastFeed.posted>=1 and .lastFeed.availableReported==true and (.lastFeed.at|type=="string")) and (.[0].accounts[] | select(.externalId=="ACT-V7-SAV") | .lastFeed.availableReported==false)'
  request GET "/v1/transactions?accountId=$USB" "$T_TOKEN"; expect 200 "US Bank transactions feed"
  v7check "/v1/transactions lists all 3 pending" '(.pending|length==3) and ([.pending[].description]|sort)==["SHELL OIL 5741","STARBUCKS 0091","WALMART SUPERCENTER"]'
  request GET /v1/pending "$T_TOKEN"; expect 200 "pending view"
  v7check "/v1/pending: US Bank group with all 3" ".groups[] | select(.accountId==\"$USB\") | .pendingCount==3 and .pendingOutCents==6240"

  # Friday: Travis moves $20 to each fun account → the bank feed shows both legs → the planned transfers tick themselves.
  curl -fs -X POST "$FAKE/__phase/v7/2" >/dev/null || fail "phase v7/2"
  v7wait "fun transfers auto-paid from the bank feed (transfer pairs to each fun account)" /v1/plan \
    '([.funMoney[] | select(.occurrence.paid.source=="bank" and .occurrence.paid.auto==true)] | length==2)'
  v7check "projected savings unchanged: fun money still \$40 in the plan, added back as already paid; available \$2,305.67" '.leftAtEndCents==25317 and .funOutCents==4000 and .paidThisWeekCents==4000 and .billsAccount.availableCents==230567'
  v7check "checklist: both fun transfers ticked automatically with their transactions" '[.checklist[] | select(.key=="fun" and .done==true and .auto==true and .transactionId!=null)] | length==2'
  request GET "/v1/accounts/$USB/transactions?filter=transfers" "$T_TOKEN"; expect 200 "US Bank transfers"
  v7check "both −\$20 rows are ignored transfers paired with a fun account and linked to their planned transfer" \
    "[.items[] | select(.amountCents==-2000 and .ignoreReason==\"transfer\" and (.transfer.peerAccountId==\"$F1\" or .transfer.peerAccountId==\"$F2\") and (.match.itemId==\"$FUNT\" or .match.itemId==\"$FUNJ\"))] | length==2"
  request GET "/v1/activity?limit=50" "$T_TOKEN"
  v7check "activity transfer.planned_paid × 2 (system)" '[.events[] | select(.kind=="transfer.planned_paid" and .actorId==null)] | length==2'
  request GET "/v1/inbox?status=pending&limit=200" "$T_TOKEN"
  v7check "still nothing from the fun accounts (or the \$20 transfers) in To review" "[.items[] | select(.accountId==\"$F1\" or .accountId==\"$F2\" or .amountCents==-2000)] | length==0"

  # The sweep: on the last day of the pay week what is left moves to savings. Make today that day (payday = tomorrow's weekday): the
  # week is D0 − 6 … D0, everything in it is paid, so what is left is US Bank's available $2,305.67.
  SW0="$(date -u -d "$D0 -6 days" +%F)"
  request PATCH /v1/couple "$T_TOKEN" "$(jq -nc --argjson p "$(( (PAYDAY7 + 1) % 7 ))" '{paydayWeekday:$p}')"; expect 200 "payday = tomorrow: today is the sweep day"
  v7wait "the sweep is frozen on the sweep day from the week's Left on Thursday (\$2,305.67)" /v1/plan \
    ".start==\"$SW0\" and .end==\"$D0\" and .sweep!=null and .sweep.weekStart==\"$SW0\" and .sweep.plannedCents==230567 and .sweep.leftCents==230567 and .sweep.doneAt==null and .leftAtEndCents==230567"
  PLANNED7="$(echo "$BODY" | jq -r .sweep.plannedCents)"
  curl -fs -X POST "$FAKE/__phase/v7/3?cents=$PLANNED7" >/dev/null || fail "phase v7/3"
  v7wait "sweep auto-done from the bills → savings transfer" /v1/plan '.sweep.doneAt!=null and .sweep.auto==true'
  v7check "sweep: actual = planned, transaction attached" ".sweep.actualCents==$PLANNED7 and .sweep.transactionId!=null"
  v7check "moved: left on Thursday is what is left after it (\$0.00)" ".leftAtEndCents==(230567 - $PLANNED7) and .billsAccount.availableCents==0"
  SWEEP_TX="$(echo "$BODY" | jq -r .sweep.transactionId)"
  request GET "/v1/accounts/$USB/transactions?filter=transfers" "$T_TOKEN"
  v7check "the sweep rows are a transfer pair to savings" ".items[] | select(.id==\"$SWEEP_TX\") | .ignoreReason==\"transfer\" and .transfer.peerAccountId==\"$SAV\""
  request GET "/v1/activity?limit=50" "$T_TOKEN"
  v7check "activity sweep.planned + sweep.auto" '([.events[] | select(.kind=="sweep.auto")] | length==1) and ([.events[] | select(.kind=="sweep.planned")] | length==1)'

  # Manual untick / tick: the Undo sticks (the detection never re-ticks that week).
  request DELETE /v1/plan/sweep/done "$T_TOKEN" "$(jq -nc --arg w "$SW0" '{weekStart:$w}')"; expect 200 "untick the sweep"
  v7check "not done any more" '.doneAt==null and .auto==false and .actualCents==null and .transactionId==null'
  sleep 6
  request GET /v1/plan "$T_TOKEN"; v7check "a scheduler tick later it is still unticked (auto-detection blocked)" '.sweep.doneAt==null'
  request POST /v1/plan/sweep/done "$J_TOKEN" "$(jq -nc --arg w "$SW0" '{weekStart:$w,actualCents:80000}')"; expect 200 "Jordan ticks it by hand (\$800.00)"
  v7check "manual tick" '.doneAt!=null and .auto==false and .actualCents==80000'
  request PATCH /v1/couple "$T_TOKEN" "$(jq -nc --argjson p "$PAYDAY7" '{paydayWeekday:$p}')"; expect 200 "payday back to today"
  request GET "/v1/activity?limit=50" "$T_TOKEN"
  v7check "activity sweep.done × 2 (untick, tick)" '[.events[] | select(.kind=="sweep.done")] | length==2'

  # bills is exclusive: moving it clears the previous one (then back).
  request PUT "/v1/accounts/$SAV/role" "$T_TOKEN" '{"role":"bills"}'; expect 200 "savings → bills"
  request GET /v1/accounts "$T_TOKEN"
  v7check "exactly one bills account; US Bank became other" "([.[] | select(.role==\"bills\")] | length==1) and (.[] | select(.id==\"$USB\") | .role==\"other\")"
  request PUT "/v1/accounts/$USB/role" "$T_TOKEN" '{"role":"bills"}'; expect 200 "US Bank → bills again"
  request PUT "/v1/accounts/$SAV/role" "$T_TOKEN" '{"role":"savings"}'; expect 200 "savings again"
  request GET "/v1/week" "$T_TOKEN"; expect 200 "week view"
  v7check "/v1/week is the pay week too" ".start==\"$D0\" and .weekStart==$PAYDAY7"

  request DELETE "/v1/banks/$LINK7" "$T_TOKEN"; expect 204 "unlink the v7 Bridge"
}

# v7 Plaid flow (SMOKE_PLAID_PORT): a fresh household links a bank through scripts/fake-plaid.py — link token, a refused and an
# accepted public token, "still loading" on the first sync, Sync now (refresh + transactions), mapping, Plaid rows in the pipeline;
# with SMOKE_SCHEDULER_WAIT (API: Plaid__IntervalMinutes / Plaid__RefreshPollMinutes small) the scheduler picks up a posted
# transaction that names its pending row, then the bank wants a new sign-in → Reconnect (update mode) → unlink revokes the Item.
plaid_flow() {
  local P_EMAIL="pat+$RUN@example.com" P_TOKEN P_REFRESH PLINK PEXT PACC PDINER FAKEP="http://127.0.0.1:$SMOKE_PLAID_PORT"
  pwait() { # pwait LABEL PATH JQ-FILTER
    for _ in $(seq 1 "$SMOKE_SCHEDULER_WAIT"); do
      request GET "$2" "$P_TOKEN"
      if [[ "$STATUS" == "200" ]] && echo "$BODY" | jq -e "$3" >/dev/null 2>&1; then pass "$1"; return 0; fi
      sleep 1
    done
    echo "$BODY" | head -c 2500; echo; fail "$1: not observed within ${SMOKE_SCHEDULER_WAIT}s"
  }

  echo "--- v7: Plaid via fake Plaid on port $SMOKE_PLAID_PORT"
  command -v python3 >/dev/null || fail "python3 is required for the fake Plaid server"
  local SCRIPT_DIR; SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  python3 -I "$SCRIPT_DIR/fake-plaid.py" --port "$SMOKE_PLAID_PORT" &
  PLAID_PID=$!
  for _ in $(seq 1 50); do curl -fs "$FAKEP/__health" >/dev/null 2>&1 && break; sleep 0.1; done
  curl -fs "$FAKEP/__health" >/dev/null || fail "fake Plaid server did not start"

  request POST /v1/auth/register '' "$(jq -nc --arg e "$P_EMAIL" --arg p "$PASSWORD" '{email:$e,password:$p,displayName:"Pat"}')"; expect 201 "register Pat"
  P_TOKEN="$(echo "$BODY" | jq -r .accessToken)"; P_REFRESH="$(echo "$BODY" | jq -r .refreshToken)"
  request POST /v1/couple/code "$P_TOKEN" '{"name":"Pat & Co"}'; expect 201 "Pat creates a household"
  request POST /v1/auth/refresh '' "$(jq -nc --arg r "$P_REFRESH" '{refreshToken:$r}')"; expect 200 "refresh Pat"
  P_TOKEN="$(echo "$BODY" | jq -r .accessToken)"

  request GET /v1/banks/plaid "$P_TOKEN"; expect 200 "Plaid status"
  v6check "Plaid is offered" '.configured==true'
  request POST /v1/banks/plaid/link-token "$P_TOKEN" '{}'; expect 200 "a Link token for a new connection"
  v6check "link token handed to the page" '.linkToken=="link-sandbox-smoke" and (.expiration|length>0)'
  request POST /v1/banks/plaid/link-token "$P_TOKEN" '{"linkId":"00000000-0000-7000-8000-000000000000"}'; expect 404 "update mode for an unknown link → 404"
  request POST /v1/banks/plaid/exchange "$P_TOKEN" '{"publicToken":""}'; expect 400 "empty public token → 400"
  request POST /v1/banks/plaid/exchange "$P_TOKEN" '{"publicToken":"public-sandbox-expired"}'; expect 400 "refused public token → 400"
  v6check "error names publicToken" '.errors | has("publicToken")'
  request POST /v1/banks/plaid/exchange "$P_TOKEN" '{"publicToken":"public-sandbox-good","institutionName":"U.S. Bank"}'; expect 201 "Pat connects U.S. Bank through Plaid"
  PLINK="$(echo "$BODY" | jq -r .id)"
  v6check "a Plaid link named after the bank; Plaid still loading; accounts already there" \
    '.provider=="plaid" and .name=="U.S. Bank" and .needsReconnect==false and (.bridgeErrors[0]|test("still loading")) and (.accounts|length==2)'
  [[ "$BODY" != *access-sandbox* ]] || fail "the access token must never be returned"
  PEXT="$(echo "$BODY" | jq -r '.accounts[] | select(.externalId=="acc-chk") | .id')"
  v6check "checking ··0000 with Plaid's available balance" '.accounts[] | select(.externalId=="acc-chk") | .name=="Plaid Checking ··0000" and .balanceCents==240807 and .availableCents==234567'

  request POST "/v1/banks/$PLINK/sync" "$P_TOKEN"; expect 200 "Sync now (asks Plaid to refresh, then reads transactions)"
  v6check "no Plaid errors (the scheduler may already have read the transactions)" '(.errors|length==0)'
  [[ "$(curl -fs "$FAKEP/__fake/state" | jq -r .refreshes)" == "1" ]] || fail "Sync now calls /transactions/refresh"
  pass "Sync now asked Plaid to refresh"
  request POST "/v1/banks/$PLINK/sync" "$P_TOKEN"; expect 429 "Sync now again inside the cooldown → 429"

  request PUT "/v1/banks/accounts/$PEXT" "$P_TOKEN" '{"create":{"name":"US Bank","type":"checking","institution":"U.S. Bank"}}'; expect 200 "map Plaid checking to a new account"
  PACC="$(echo "$BODY" | jq -r .accountId)"
  request GET "/v1/accounts/$PACC/transactions?filter=all" "$P_TOKEN"; expect 200 "the account's transactions"
  v6check "Plaid rows: money out negative, the bank's wording, the merchant as payee" \
    'any(.items[]; .source=="plaid" and .description=="SQ *BLUE BOTTLE 0042" and .payee=="Blue Bottle Coffee" and .amountCents==-425) and any(.items[]; .description=="ACME CORP PAYROLL" and .amountCents==150000)'
  v6check "Plaid's VERY_HIGH coffee category filed it under Fun by itself" \
    '.items[] | select(.description=="SQ *BLUE BOTTLE 0042") | .status=="confirmed" and (.matchDetail.reason|test("bank category: food and drink coffee"))'
  PDINER="$(echo "$BODY" | jq -r '.items[] | select(.description=="SQ *DINER") | .id')"
  v6check "the pending diner is dated by its authorization" ".items[] | select(.id==\"$PDINER\") | .pending==true and .amountCents==-4000 and .postedOn==\"$(date -u -d "$TODAY_UTC -1 day" +%F)\""

  if [[ "${SMOKE_SCHEDULER_WAIT:-0}" -gt 0 ]]; then
    curl -fs -X POST "$FAKEP/__fake/phase/2" >/dev/null || fail "fake Plaid phase 2"
    pwait "the scheduler picks up the posted diner: it takes over its pending row (tip, new wording)" "/v1/accounts/$PACC/transactions?filter=all" \
      "any(.items[]; .id==\"$PDINER\" and .pending==false and .amountCents==-4800 and .description==\"THE DINER LLC\") and ([.items[] | select(.description|test(\"DINER\"))] | length==1)"
    request GET "/v1/accounts/$PACC" "$P_TOKEN"; v6check "available follows Plaid" '.availableCents==229767'

    curl -fs -X POST "$FAKEP/__fake/login-required" >/dev/null || fail "fake Plaid login-required"
    pwait "the bank wants a new sign-in: the link says so" /v1/banks \
      ".[] | select(.id==\"$PLINK\") | .needsReconnect==true and (.lastError|test(\"sign in again\"))"
    request POST /v1/banks/plaid/link-token "$P_TOKEN" "$(jq -nc --arg l "$PLINK" '{linkId:$l}')"; expect 200 "a Link token in update mode"
    [[ "$(curl -fs "$FAKEP/__fake/state" | jq -r .updateMode)" == "1" ]] || fail "update mode passes the Item's access token"
    request POST "/v1/banks/$PLINK/plaid/reconnected" "$P_TOKEN"; expect 200 "Reconnect finished: synced without the cooldown"
    request GET /v1/banks "$P_TOKEN"; v6check "reconnected" ".[] | select(.id==\"$PLINK\") | .needsReconnect==false and .lastError==null"
  else
    echo "--- v7 Plaid scheduler checks skipped (set SMOKE_SCHEDULER_WAIT > 0)"
  fi

  request DELETE "/v1/banks/$PLINK" "$P_TOKEN"; expect 204 "unlink the Plaid bank"
  [[ "$(curl -fs "$FAKEP/__fake/state" | jq -r .removed)" == "1" ]] || fail "unlinking revokes the Item at Plaid"
  pass "unlinking revoked the Item at Plaid"
  kill "$PLAID_PID" 2>/dev/null; wait "$PLAID_PID" 2>/dev/null || true
}

# ---------------------------------------------------------------------------------------------
# v3: bank sync, review inbox, CSV import
#   SMOKE_SIMPLEFIN_PORT=<port>  start scripts/fake-simplefin.py on that port and run the full SimpleFIN flow.
#                                The API must have been started with Security__DataKey set and
#                                SimpleFin__BaseUrl=http://127.0.0.1:<port>; set Sync__IntervalHours=0 and
#                                Scheduler__IntervalSeconds=5 to also exercise scheduler auto-match.
#   Without it only the always-on inbox/import checks run and the claim route must answer 503 (no key) or 400/502.
# ---------------------------------------------------------------------------------------------
echo "--- v3: inbox & CSV import"
request GET /v1/inbox/summary "$A_TOKEN"; expect 200 "inbox summary"
echo "$BODY" | jq -e '.pending==0 and .suggested==0 and .linkCount==0 and .lastSyncedAt==null' >/dev/null || fail "empty inbox summary: $BODY"
request GET "/v1/inbox?status=bogus" "$A_TOKEN"; expect 400 "bad inbox status → 400"
request GET "/v1/inbox?before=yesterday" "$A_TOKEN"; expect 400 "bad inbox cursor → 400"
request GET /v1/banks "$A_TOKEN"; expect 200 "bank links (none yet)"
[[ "$(echo "$BODY" | jq length)" == "0" ]] || fail "expected no bank links"
request POST /v1/imports/csv "$A_TOKEN" "$(jq -nc --arg a "$ACC" '{accountId:$a,filename:"bad.csv",rows:[{postedOn:"2026-10-01",amountCents:0,description:""}]}')"; expect 400 "CSV row validation → 400"
[[ "$(echo "$BODY" | jq -r '.errors | keys | join(",")')" == *rows* ]] || fail "errors should name rows[...]"
request POST /v1/imports/csv "$A_TOKEN" '{"accountId":"00000000-0000-7000-8000-000000000000","filename":"x.csv","rows":[{"postedOn":"2026-10-01","amountCents":-100,"description":"X"}]}'; expect 400 "foreign CSV accountId → 400"
request POST /v1/inbox/bulk "$A_TOKEN" '{"action":"confirm-all"}'; expect 400 "bad bulk action → 400"
request POST /v1/inbox/00000000-0000-7000-8000-000000000000/ignore "$A_TOKEN"; expect 404 "unknown inbox row → 404"

if [[ -n "${SMOKE_SIMPLEFIN_PORT:-}" ]]; then
  echo "--- v3: SimpleFIN via fake Bridge on port $SMOKE_SIMPLEFIN_PORT"
  command -v python3 >/dev/null || fail "python3 is required for the fake SimpleFIN server"
  TODAY_UTC="$(date -u +%F)"
  RENT_ANCHOR="$(date -u -d "$TODAY_UTC -7 days" +%F)"
  SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  python3 -I "$SCRIPT_DIR/fake-simplefin.py" --port "$SMOKE_SIMPLEFIN_PORT" --today "$TODAY_UTC" --rent-date "$TODAY_UTC" --rent2-date "$RENT_ANCHOR" &
  FAKE_PID=$!
  trap 'kill "$FAKE_PID" 2>/dev/null; rm -f "$TMP_BODY" "$TMP_REQ"' EXIT
  FAKE="http://127.0.0.1:$SMOKE_SIMPLEFIN_PORT"
  for _ in $(seq 1 50); do curl -fs "$FAKE/__health" >/dev/null 2>&1 && break; sleep 0.1; done
  curl -fs "$FAKE/__health" >/dev/null || fail "fake SimpleFIN server did not start"
  pass "fake SimpleFIN server up"

  if [[ -n "${SMOKE_MATCHER_URL:-}" ]]; then
    echo "--- v3–v5 bank flows skipped: SMOKE_MATCHER_URL is set and they assert the built-in engine's scores (run them without it)"
  else

  # A weekly "Rent" anchored 7 days ago: occurrences on $RENT_ANCHOR and $TODAY_UTC, both unpaid.
  request POST /v1/recurring "$A_TOKEN" "$(jq -nc --arg c "$HOUSING" --arg a "$ACC" --arg d "$RENT_ANCHOR" '{name:"Rent (weekly test)",kind:"bill",amountCents:185000,categoryId:$c,accountId:$a,frequency:"weekly",anchorDate:$d}')"; expect 201 "create weekly Rent due $RENT_ANCHOR and $TODAY_UTC"
  RENT_V3="$(echo "$BODY" | jq -r .id)"

  request GET "/v1/accounts/$ACC" "$A_TOKEN"; expect 200 "account before mapping"
  ACC_BALANCE_BEFORE="$(echo "$BODY" | jq -r .balanceCents)"
  request GET "/v1/accounts/$ACC/history?limit=100" "$A_TOKEN"; HIST_BEFORE="$(echo "$BODY" | jq length)"

  TOKEN="$(printf 'http://127.0.0.1:%s/simplefin/claim/demo' "$SMOKE_SIMPLEFIN_PORT" | base64 -w0)"
  request POST /v1/banks/simplefin/claim "$A_TOKEN" '{"setupToken":"bm90IGEgdXJs"}'; expect 400 "garbage setup token → 400"
  [[ "$(echo "$BODY" | jq -r '.errors | keys | join(",")')" == "setupToken" ]] || fail "errors should name setupToken"
  EXPIRED="$(printf 'http://127.0.0.1:%s/simplefin/claim/expired' "$SMOKE_SIMPLEFIN_PORT" | base64 -w0)"
  request POST /v1/banks/simplefin/claim "$A_TOKEN" "$(jq -nc --arg t "$EXPIRED" '{setupToken:$t}')"; expect 400 "already-claimed token (Bridge 403) → 400"
  request POST /v1/banks/simplefin/claim "$A_TOKEN" "$(jq -nc --arg t "$TOKEN" '{setupToken:$t,name:"Demo Bank"}')"; expect 201 "claim setup token (first sync inline)"
  LINK="$(echo "$BODY" | jq -r .id)"
  echo "$BODY" | jq -e '.provider=="simplefin" and .name=="Demo Bank" and .lastSyncedAt!=null and .lastError==null and (.bridgeErrors|length==0) and (.accounts|length==2)' >/dev/null || fail "link shape: $BODY"
  [[ "$(echo "$BODY" | jq -r 'tostring')" != *demo-pass* ]] || fail "access URL credentials leaked into the response"
  EXT_CHK="$(echo "$BODY" | jq -r '.accounts[] | select(.externalId=="ACT-CHECKING") | .id')"
  EXT_VISA="$(echo "$BODY" | jq -r '.accounts[] | select(.externalId=="ACT-VISA") | .id')"
  [[ "$(echo "$BODY" | jq -r '.accounts[] | select(.externalId=="ACT-VISA") | .balanceCents')" == "-54000" ]] || fail "external credit balance is the raw Bridge value"
  [[ "$(echo "$BODY" | jq -r '.accounts[] | select(.externalId=="ACT-CHECKING") | .accountId')" == "null" ]] || fail "not mapped yet"

  request GET /v1/banks "$A_TOKEN"; expect 200 "list links"
  [[ "$(echo "$BODY" | jq length)" == "1" ]] || fail "one link"

  request PUT "/v1/banks/accounts/$EXT_CHK" "$A_TOKEN" '{"accountId":"00000000-0000-7000-8000-000000000000"}'; expect 400 "map to foreign account → 400"
  request PUT "/v1/banks/accounts/$EXT_CHK" "$A_TOKEN" "$(jq -nc --arg a "$ACC" '{accountId:$a}')"; expect 200 "map checking to Joint checking"
  [[ "$(echo "$BODY" | jq -r .accountId)" == "$ACC" && "$(echo "$BODY" | jq -r .accountName)" == "Joint checking" ]] || fail "mapping response: $BODY"
  request GET "/v1/accounts/$ACC" "$A_TOKEN"
  [[ "$(echo "$BODY" | jq -r .balanceCents)" == "245050" ]] || fail "mapped account balance should be 245050 (was $ACC_BALANCE_BEFORE), got $(echo "$BODY" | jq -r .balanceCents)"
  request GET "/v1/accounts/$ACC/history?limit=100" "$A_TOKEN"
  [[ "$(echo "$BODY" | jq length)" == "$((HIST_BEFORE + 1))" ]] || fail "mapping should add one snapshot"
  pass "mapping applied balance + snapshot"

  request PUT "/v1/banks/accounts/$EXT_VISA" "$A_TOKEN" '{"create":{"name":"Demo Visa","type":"credit"}}'; expect 200 "map credit card by creating an account"
  VISA_ACC="$(echo "$BODY" | jq -r .accountId)"
  [[ "$VISA_ACC" != "null" && "$(echo "$BODY" | jq -r .accountName)" == "Demo Visa" ]] || fail "create mapping response: $BODY"
  request GET "/v1/accounts/$VISA_ACC" "$A_TOKEN"; expect 200 "created credit account"
  [[ "$(echo "$BODY" | jq -r .balanceCents)" == "54000" && "$(echo "$BODY" | jq -r .type)" == "credit" ]] || fail "credit: Bridge -540.00 → 54000 owed, got $BODY"

  request GET /v1/inbox/summary "$A_TOKEN"; expect 200 "inbox summary after sync"
  echo "$BODY" | jq -e '.pending==5 and .linkCount==1 and .lastSyncedAt!=null' >/dev/null || fail "summary: $BODY"
  request GET "/v1/inbox?status=pending" "$A_TOKEN"; expect 200 "pending inbox"
  INBOX="$BODY"
  [[ "$(echo "$INBOX" | jq '.items|length')" == "5" ]] || fail "5 pending rows"
  TX_RENT="$(echo "$INBOX" | jq -r '.items[] | select(.description=="RENT PAYMENT") | .id')"
  TX_KROGER="$(echo "$INBOX" | jq -r '.items[] | select(.description=="KROGER #123") | .id')"
  TX_NETFLIX="$(echo "$INBOX" | jq -r '.items[] | select(.description=="NETFLIX.COM") | .id')"
  TX_PAYROLL="$(echo "$INBOX" | jq -r '.items[] | select(.description|startswith("ACME")) | .id')"
  TX_COFFEE="$(echo "$INBOX" | jq -r '.items[] | select(.description|contains("COFFEE")) | .id')"
  echo "$INBOX" | jq -e --arg id "$TX_RENT" --arg item "$RENT_V3" --arg d "$TODAY_UTC" '.items[] | select(.id==$id) | .suggestion.kind=="occurrence" and .suggestion.itemId==$item and .suggestion.dueDate==$d and .suggestion.confidence>=90 and .amountCents==-185000 and .accountName=="Joint checking"' >/dev/null || fail "rent suggestion: $(echo "$INBOX" | jq -c --arg id "$TX_RENT" '.items[]|select(.id==$id)')"
  echo "$INBOX" | jq -e --arg id "$TX_KROGER" '.items[] | select(.id==$id) | .suggestion.kind=="category" and .suggestion.categoryName=="Groceries" and .suggestion.confidence==40 and .payee=="Kroger"' >/dev/null || fail "kroger suggestion"
  echo "$INBOX" | jq -e --arg id "$TX_NETFLIX" '.items[] | select(.id==$id) | .suggestion.kind=="category" and .suggestion.categoryName=="Subscriptions" and .accountName=="Demo Visa"' >/dev/null || fail "netflix suggestion"
  echo "$INBOX" | jq -e --arg id "$TX_PAYROLL" '.items[] | select(.id==$id) | .suggestion.categoryName=="Income" and .amountCents==320000' >/dev/null || fail "payroll suggestion"
  echo "$INBOX" | jq -e --arg id "$TX_COFFEE" '.items[] | select(.id==$id) | .pending==true and .suggestion.categoryName=="Fun"' >/dev/null || fail "coffee pending + Fun"
  pass "suggestions: Rent→occurrence, Kroger→Groceries, Netflix→Subscriptions, payroll→Income, coffee pending"
  request GET "/v1/inbox?status=pending&limit=2" "$A_TOKEN"; expect 200 "inbox page 1 (limit 2)"
  NEXT="$(echo "$BODY" | jq -r .nextBefore)"; [[ "$NEXT" != "null" && "$(echo "$BODY" | jq '.items|length')" == "2" ]] || fail "page 1 should have a cursor"
  request GET "/v1/inbox?status=pending&limit=2&before=$NEXT" "$A_TOKEN"; expect 200 "inbox page 2 via nextBefore"
  [[ "$(echo "$BODY" | jq '.items|length')" == "2" ]] || fail "page 2"

  request POST "/v1/inbox/$TX_RENT/confirm" "$A_TOKEN" "$(jq -nc --arg i "$RENT_V3" --arg d "$RENT_ANCHOR" '{as:"occurrence",itemId:$i,dueDate:"2020-01-01"}')"; expect 400 "confirm with a non-occurrence date → 400"
  request POST "/v1/inbox/$TX_PAYROLL/confirm" "$A_TOKEN" "$(jq -nc --arg i "$RENT_V3" --arg d "$TODAY_UTC" '{as:"occurrence",itemId:$i,dueDate:$d}')"; expect 400 "inflow against a bill → 400 (sign/kind)"
  request POST "/v1/inbox/$TX_RENT/confirm" "$A_TOKEN" "$(jq -nc --arg i "$RENT_V3" --arg d "$TODAY_UTC" '{as:"occurrence",itemId:$i,dueDate:$d}')"; expect 200 "confirm Rent as occurrence"
  echo "$BODY" | jq -e --arg i "$RENT_V3" --arg d "$TODAY_UTC" '.status=="confirmed" and .confirmed.itemId==$i and .confirmed.dueDate==$d and .confirmed.auto==false and .confirmed.by!=null and .confirmed.itemName=="Rent (weekly test)"' >/dev/null || fail "confirmed rent: $BODY"
  request POST "/v1/inbox/$TX_RENT/confirm" "$A_TOKEN" "$(jq -nc --arg i "$RENT_V3" --arg d "$TODAY_UTC" '{as:"occurrence",itemId:$i,dueDate:$d}')"; expect 400 "confirm twice → 400"
  request GET "/v1/calendar?from=$TODAY_UTC&to=$TODAY_UTC" "$A_TOKEN"
  echo "$BODY" | jq -e --arg i "$RENT_V3" '.occurrences[] | select(.itemId==$i) | .paid.amountCents==185000 and .paid.auto==false and .paid.source=="bank" and .paid.paidBy!=null' >/dev/null || fail "occurrence paid from bank: $(echo "$BODY" | jq -c --arg i "$RENT_V3" '.occurrences[]|select(.itemId==$i)')"
  pass "Rent occurrence paid · source=bank"

  request POST "/v1/inbox/$TX_PAYROLL/confirm" "$A_TOKEN" '{"as":"expense"}'; expect 400 "inflow as expense → 400"
  request POST "/v1/inbox/$TX_KROGER/confirm" "$A_TOKEN" '{"as":"expense"}'; expect 200 "confirm Kroger as expense (suggested category)"
  KROGER_EXP="$(echo "$BODY" | jq -r .confirmed.expenseId)"
  [[ "$(echo "$BODY" | jq -r .confirmed.categoryName)" == "Groceries" ]] || fail "confirmed.categoryName"
  request GET "/v1/expenses/$KROGER_EXP" "$A_TOKEN"; expect 200 "expense exists"
  echo "$BODY" | jq -e --arg c "$GROCERIES" --arg a "$ACC" --arg d "$TODAY_UTC" '.deducted==false and .categoryId==$c and .accountId==$a and .amountCents==1249 and .name=="Kroger" and .spentOn==$d' >/dev/null || fail "expense from inbox: $BODY"
  request GET "/v1/accounts/$ACC" "$A_TOKEN"
  [[ "$(echo "$BODY" | jq -r .balanceCents)" == "245050" ]] || fail "deduct=false must not touch the balance"
  pass "expense created with deduct=false"

  request POST "/v1/inbox/$TX_COFFEE/ignore" "$A_TOKEN"; expect 200 "ignore coffee"
  [[ "$(echo "$BODY" | jq -r .status)" == "ignored" ]] || fail "ignored status"
  request POST "/v1/inbox/$TX_COFFEE/ignore" "$A_TOKEN"; expect 400 "ignore twice → 400"
  request GET "/v1/inbox?status=ignored" "$A_TOKEN"; [[ "$(echo "$BODY" | jq '.items|length')" == "1" ]] || fail "ignored filter"
  request POST "/v1/inbox/$TX_COFFEE/restore" "$A_TOKEN"; expect 200 "restore coffee"
  [[ "$(echo "$BODY" | jq -r .status)" == "pending" && "$(echo "$BODY" | jq -r .confirmed)" == "null" ]] || fail "restored → pending"

  request POST /v1/imports/csv "$A_TOKEN" "$(jq -nc --arg a "$ACC" --arg d "$TODAY_UTC" '{accountId:$a,filename:"statement.csv",rows:[{postedOn:$d,amountCents:-3010,description:"KROGER #456"},{postedOn:$d,amountCents:-3010,description:"KROGER #456"},{postedOn:$d,amountCents:-4000,description:"SHELL OIL 12345",memo:"fuel"}]}')"; expect 201 "CSV import (3 rows, 1 duplicate)"
  echo "$BODY" | jq -e '.rowCount==3 and .imported==2 and .duplicates==1 and .skipped==0 and .inboxPending==5' >/dev/null || fail "csv result: $BODY"
  request GET "/v1/inbox?status=pending" "$A_TOKEN"
  echo "$BODY" | jq -e '.items[] | select(.description=="KROGER #456") | .source=="csv" and .suggestion.kind=="category" and .suggestion.categoryName=="Groceries" and .suggestion.confidence==71' >/dev/null || fail "learned Kroger rule should suggest Groceries at 71: $(echo "$BODY" | jq -c '.items[]|select(.description=="KROGER #456")')"
  echo "$BODY" | jq -e '.items[] | select(.description|startswith("SHELL")) | .suggestion.categoryName=="Transportation" and .memo=="fuel"' >/dev/null || fail "shell → Transportation"
  pass "payee rule learned from the Kroger confirmation applies to the CSV row"
  request GET /v1/imports/csv "$A_TOKEN"; expect 200 "CSV import history"
  echo "$BODY" | jq -e 'length==1 and .[0].filename=="statement.csv" and .[0].imported==2 and .[0].duplicates==1 and .[0].createdByName=="Alex"' >/dev/null || fail "history: $BODY"

  request POST /v1/inbox/bulk "$A_TOKEN" '{"action":"confirm-suggested"}'; expect 200 "bulk confirm-suggested"
  echo "$BODY" | jq -e '.confirmed==1 and .ignored==0 and .skipped==4' >/dev/null || fail "bulk: expected 1 confirmed (Kroger #456 @71), 4 skipped (<70), got $BODY"
  request GET /v1/inbox/summary "$A_TOKEN"; [[ "$(echo "$BODY" | jq -r .pending)" == "4" ]] || fail "4 pending after bulk"
  request POST /v1/inbox/bulk "$A_TOKEN" "$(jq -nc --arg a "$TX_PAYROLL" '{action:"ignore",ids:[$a]}')"; expect 200 "bulk ignore by id"
  echo "$BODY" | jq -e '.ignored==1' >/dev/null || fail "bulk ignore: $BODY"

  echo "--- v4: the Bridge clears the planned one-off (phase 3: JIFFY LUBE #42 −54.20 today)"
  curl -fs -X POST "$FAKE/__phase/3" >/dev/null || fail "could not enable phase 3 on the fake Bridge"
  request POST "/v1/banks/$LINK/sync" "$A_TOKEN"; expect 200 "manual sync (allowed right after linking)"
  echo "$BODY" | jq -e --arg l "$LINK" '.linkId==$l and .newTransactions==1 and .autoMatched==1 and (.errors|length==0)' >/dev/null || fail "sync should import only JIFFY LUBE and auto-match it via the payee hint: $BODY"
  request GET "/v1/inbox?status=confirmed" "$A_TOKEN"
  echo "$BODY" | jq -e '.items[] | select(.description=="JIFFY LUBE #42") | .confirmed.auto==true and .confirmed.by==null and .confirmed.itemName=="Oil change" and .amountCents==-5420' >/dev/null || fail "JIFFY LUBE row should be auto-confirmed against the one-off: $(echo "$BODY" | jq -c '.items[]|select(.description=="JIFFY LUBE #42")')"
  request GET "/v1/week?start=$TODAY_UTC" "$A_TOKEN"; expect 200 "week view after the bank cleared the one-off"
  echo "$BODY" | jq -e --arg id "$ONEOFF" --arg d "$TODAY_UTC" '.days[] | select(.date==$d) | .planned[] | select(.itemId==$id) | .status=="cleared" and .paid.amountCents==5420 and .paid.source=="bank" and .paid.auto==true and .varianceCents==-580' >/dev/null || fail "one-off should be cleared at 54.20 with variance −5.80: $(echo "$BODY" | jq -c --arg id "$ONEOFF" '.days[].planned[]|select(.itemId==$id)')"
  pass "one-off cleared · actual 54.20 · variance −5.80"
  TRANSPORT_ROW="$(echo "$BODY" | jq -c --arg c "$TRANSPORT" '.budget.categories[] | select(.categoryId==$c)')"
  GROCERIES_ROW="$(echo "$BODY" | jq -c --arg g "$GROCERIES" '.budget.categories[] | select(.categoryId==$g)')"
  echo "$TRANSPORT_ROW" | jq -e '.budgetCents==5000 and .spentCents==5420 and .varianceCents==420 and .percent==108' >/dev/null || fail "Transportation weekly row over by 4.20: $TRANSPORT_ROW"
  echo "$GROCERIES_ROW" | jq -e '.budgetCents==10000 and .spentCents>0 and .varianceCents==(.spentCents-10000) and .varianceCents<0' >/dev/null || fail "Groceries weekly row under (confirmed Kroger expenses): $GROCERIES_ROW"
  GROC_SPENT="$(echo "$GROCERIES_ROW" | jq -r .spentCents)"
  echo "$BODY" | jq -e --argjson g "$GROC_SPENT" '.budget.overall.spentCents==(185000+5420+$g) and .budget.overall.varianceCents==(.budget.overall.spentCents-20000) and .budget.overall.varianceCents>0 and .budget.totalSpentCents==(5420+$g)' >/dev/null || fail "overall weekly spent = paid Rent + Oil change actual + Kroger expenses: $(echo "$BODY" | jq -c .budget.overall)"
  [[ "$(echo "$BODY" | jq -r .paidOutCents)" -ge $((185000 + 5420)) ]] || fail "paidOutCents carries actuals"
  pass "weekly budgets: overall over (+ sign), Transportation over by 4.20, Groceries under"
  request GET "/v1/budgets/week?start=$TODAY_UTC" "$A_TOKEN"; expect 200 "GET /budgets/week equals WeekView.budget"
  [[ "$(echo "$BODY" | jq -c .)" == "$(request GET "/v1/week?start=$TODAY_UTC" "$A_TOKEN" >/dev/null; echo "$BODY" | jq -c .budget)" ]] || fail "/budgets/week should equal /week .budget"
  request POST "/v1/banks/$LINK/sync" "$A_TOKEN"; expect 429 "manual sync again inside the cooldown → 429"
  RETRY_AFTER="$(curl -s -o /dev/null -D - -X POST "$BASE/v1/banks/$LINK/sync" -H "Authorization: Bearer $A_TOKEN" | tr -d '\r' | awk 'tolower($1)=="retry-after:" {print $2}')"
  [[ "$RETRY_AFTER" =~ ^[0-9]+$ && "$RETRY_AFTER" -ge 1 ]] || fail "Retry-After header missing: '$RETRY_AFTER'"
  pass "Retry-After: ${RETRY_AFTER}s"

  if [[ "${SMOKE_SCHEDULER_WAIT:-0}" -gt 0 ]]; then
    echo "--- v3: scheduler auto-match (phase 2: a second RENT PAYMENT posted $RENT_ANCHOR; waiting up to ${SMOKE_SCHEDULER_WAIT}s)"
    curl -fs -X POST "$FAKE/__phase/2" >/dev/null || fail "could not switch the fake Bridge to phase 2"
    AUTO="null"
    for _ in $(seq 1 "$SMOKE_SCHEDULER_WAIT"); do
      request GET "/v1/calendar?from=$RENT_ANCHOR&to=$RENT_ANCHOR" "$A_TOKEN"
      AUTO="$(echo "$BODY" | jq -r --arg i "$RENT_V3" '.occurrences[] | select(.itemId==$i) | .paid.auto')"
      [[ "$AUTO" == "true" ]] && break
      sleep 1
    done
    [[ "$AUTO" == "true" ]] || fail "scheduler did not auto-match the second rent payment within ${SMOKE_SCHEDULER_WAIT}s (needs Sync__IntervalHours=0, Scheduler__IntervalSeconds=5)"
    echo "$BODY" | jq -e --arg i "$RENT_V3" '.occurrences[] | select(.itemId==$i) | .paid.paidBy==null and .paid.source=="bank" and .paid.amountCents==185000' >/dev/null || fail "auto-matched payment shape"
    pass "auto-matched: paidBy=null, auto=true, source=bank"
    request GET "/v1/inbox?status=confirmed" "$A_TOKEN"
    TX_RENT2="$(echo "$BODY" | jq -r --arg d "$RENT_ANCHOR" '.items[] | select(.description=="RENT PAYMENT" and .postedOn==$d) | .id')"
    echo "$BODY" | jq -e --arg id "$TX_RENT2" '.items[] | select(.id==$id) | .confirmed.auto==true and .confirmed.by==null and .confirmed.itemName=="Rent (weekly test)"' >/dev/null || fail "inbox row confirmed with auto"
    request GET "/v1/activity?limit=50" "$A_TOKEN"
    [[ "$(echo "$BODY" | jq -r '[.events[] | select(.kind=="transaction.automatched")] | length')" -ge 1 ]] || fail "transaction.automatched activity"
    [[ "$(echo "$BODY" | jq -r '[.events[] | select(.kind=="bank.synced")] | length')" -ge 1 ]] || fail "bank.synced activity"
    request DELETE "/v1/recurring/$RENT_V3/occurrences/$RENT_ANCHOR/paid" "$A_TOKEN"; expect 200 "undo the auto-matched occurrence"
    request GET "/v1/inbox/$TX_RENT2" "$A_TOKEN" 2>/dev/null || true
    request GET "/v1/inbox?status=pending" "$A_TOKEN"
    echo "$BODY" | jq -e --arg id "$TX_RENT2" '.items[] | select(.id==$id) | .status=="pending" and .confirmed==null' >/dev/null || fail "undo should restore the inbox row to pending"
    pass "undo restored the auto-matched row to pending"
    sleep 6
    request GET "/v1/calendar?from=$RENT_ANCHOR&to=$RENT_ANCHOR" "$A_TOKEN"
    [[ "$(echo "$BODY" | jq -r --arg i "$RENT_V3" '.occurrences[] | select(.itemId==$i) | .paid')" == "null" ]] || fail "a later sync must not re-match the undone row (fingerprint dedupe)"
    pass "later syncs leave the undone occurrence alone"
  fi

  request DELETE "/v1/recurring/$RENT_V3/occurrences/$TODAY_UTC/paid" "$A_TOKEN"; expect 200 "unpay the Rent occurrence confirmed from the inbox"
  request GET "/v1/inbox?status=pending" "$A_TOKEN"
  echo "$BODY" | jq -e --arg id "$TX_RENT" '.items[] | select(.id==$id) | .status=="pending" and .confirmed==null and .suggestion.kind=="occurrence"' >/dev/null || fail "unpaying restores the inbox row (suggestion kept)"
  pass "unpay → inbox row pending again"
  request POST "/v1/inbox/$TX_KROGER/restore" "$A_TOKEN"; expect 200 "restore the confirmed Kroger expense"
  request GET "/v1/expenses/$KROGER_EXP" "$A_TOKEN"; expect 404 "restored expense is gone"

  # -------------------------------------------------------------------------------------------
  # v5: pending lifecycle, transfers, per-account transactions, matching from anywhere.
  # The fake Bridge's phases are picked up by the scheduler (Sync__IntervalHours=0), so every step polls for its effect.
  # -------------------------------------------------------------------------------------------
  if [[ "${SMOKE_SCHEDULER_WAIT:-0}" -gt 0 ]]; then
    phase() { curl -fs -X POST "$FAKE/__phase/$1" >/dev/null || fail "could not enable phase $1 on the fake Bridge"; }
    wait_for() { # wait_for LABEL PATH JQ-FILTER — polls (the scheduler syncs every tick) until the filter holds
      local label="$1" path="$2" filter="$3"
      for _ in $(seq 1 "$SMOKE_SCHEDULER_WAIT"); do
        request GET "$path" "$A_TOKEN"
        if [[ "$STATUS" == "200" ]] && echo "$BODY" | jq -e "$filter" >/dev/null 2>&1; then pass "$label"; return 0; fi
        sleep 1
      done
      echo "$BODY" | head -c 1200; echo; fail "$label: not observed within ${SMOKE_SCHEDULER_WAIT}s"
    }
    check() { # check LABEL JQ-FILTER — on the last $BODY
      if echo "$BODY" | jq -e "$2" >/dev/null 2>&1; then pass "$1"; else echo "$BODY" | head -c 1200; echo; fail "$1"; fi
    }
    FUN="$(request GET /v1/categories "$A_TOKEN"; echo "$BODY" | jq -r '.[] | select(.name=="Fun") | .id')"
    FILL_DATE="$(date -u -d "$TODAY_UTC -6 days" +%F)"
    MONTH_UTC="$(date -u +%Y-%m)"

    echo "--- v5: pending lifecycle (phase 4: pending THE DINER −40.00 + a \$1.00 fuel hold)"
    phase 4
    wait_for "pending THE DINER and the fuel hold synced" "/v1/inbox?status=pending&limit=200" \
      '([.items[] | select(.description=="THE DINER" and .pending and .amountCents==-4000)] | length==1) and ([.items[] | select(.description=="SHELL OIL 12345" and .pending and .amountCents==-100)] | length==1)'
    TX_DINER="$(echo "$BODY" | jq -r '.items[] | select(.description=="THE DINER") | .id')"
    TX_HOLD="$(echo "$BODY" | jq -r '.items[] | select(.description=="SHELL OIL 12345" and .amountCents==-100) | .id')"
    request POST "/v1/inbox/$TX_DINER/confirm" "$A_TOKEN" "$(jq -nc --arg c "$FUN" '{as:"expense",categoryId:$c}')"; expect 200 "confirm the pending diner charge as a Fun expense"
    DINER_EXP="$(echo "$BODY" | jq -r .expense.expenseId)"
    check "InboxItem v5 fields: expense set, no match/transfer/ignoreReason" '.expense.categoryName=="Fun" and .expense.expenseId==.confirmed.expenseId and .match==null and .transfer==null and .ignoreReason==null'
    request GET "/v1/accounts/$ACC" "$A_TOKEN"; expect 200 "linked account carries v5 balances"
    check "availableCents 240000 (Bridge available-balance), linked, pendingOut = diner 40.00 + hold 1.00 + coffee 4.50" '.linked==true and .availableCents==240000 and .balanceCents==245050 and .pendingOutCents==4550'

    echo "--- v5: phase 5 — the diner charge posts as a new id at −48.00 (tip), the hold vanishes, the fill-up posts at −45.12"
    phase 5
    wait_for "THE DINER posted in place: same row, −48.00, pending false, still confirmed" "/v1/inbox?status=confirmed&limit=200" \
      ".items[] | select(.id==\"$TX_DINER\") | .pending==false and .amountCents==-4800 and .postedOn==\"$TODAY_UTC\" and .expense.expenseId==\"$DINER_EXP\""
    request GET "/v1/inbox?includeHidden=1&limit=200" "$A_TOKEN"
    check "exactly one THE DINER row (no duplicate)" '[.items[] | select(.description=="THE DINER")] | length==1'
    request GET "/v1/expenses/$DINER_EXP" "$A_TOKEN"; expect 200 "the confirmed expense survives the re-issue"
    check "expense amount follows the posted amount (4800) and date" ".amountCents==4800 and .spentOn==\"$TODAY_UTC\""
    wait_for "the \$1.00 fuel hold is dropped after > 5 days absent" "/v1/inbox?status=ignored&includeHidden=1&limit=200" \
      ".items[] | select(.id==\"$TX_HOLD\") | .ignoreReason==\"dropped\" and .status==\"ignored\""
    request GET "/v1/inbox?limit=200" "$A_TOKEN"
    check "dropped hold hidden from the inbox by default" "[.items[] | select(.id==\"$TX_HOLD\")] | length==0"
    check "the \$45.12 fill-up did not replace the \$1.00 hold (new row)" '[.items[] | select(.description=="SHELL OIL 12345" and .amountCents==-4512 and .pending==false and .status=="pending")] | length==1'
    TX_FILL="$(echo "$BODY" | jq -r '.items[] | select(.description=="SHELL OIL 12345" and .amountCents==-4512) | .id')"
    request GET "/v1/accounts/$ACC" "$A_TOKEN"
    check "pendingOut drops to the coffee only (450)" '.pendingOutCents==450'
    request GET "/v1/activity?limit=100" "$A_TOKEN"
    check "transaction.posted (−40.00 → −48.00) and transaction.dropped activity" \
      "any(.events[]; .kind==\"transaction.posted\" and .data.fromCents==-4000 and .data.toCents==-4800 and .actorId==null) and any(.events[]; .kind==\"transaction.dropped\" and .data.reason==\"dropped\")"

    echo "--- v5: transfers (phase 6: a savings account; 7: checking −500 / savings +500)"
    phase 6
    wait_for "savings account appears on the link" /v1/banks '.[0].accounts | any(.externalId=="ACT-SAVINGS")'
    EXT_SAV="$(echo "$BODY" | jq -r '.[0].accounts[] | select(.externalId=="ACT-SAVINGS") | .id')"
    request PUT "/v1/banks/accounts/$EXT_SAV" "$A_TOKEN" '{"create":{"name":"Demo Savings","type":"savings"}}'; expect 200 "create-and-map Demo Savings"
    SAV_ACC="$(echo "$BODY" | jq -r .accountId)"
    request GET /v1/couple "$A_TOKEN"; check "autoIgnoreTransfers defaults to true" '.autoIgnoreTransfers==true'
    phase 7
    wait_for "detected pair auto-ignored as a transfer (peers linked, auto)" "/v1/inbox?status=ignored&limit=200" \
      '[.items[] | select(.ignoreReason=="transfer" and .transfer.auto==true and (.description|startswith("ONLINE TRANSFER")))] | length==2'
    XFER_OUT1="$(echo "$BODY" | jq -r '.items[] | select(.description=="ONLINE TRANSFER TO SAV" and .amountCents==-50000) | .id')"
    XFER_IN1="$(echo "$BODY" | jq -r '.items[] | select(.description=="ONLINE TRANSFER FROM CHK" and .amountCents==50000) | .id')"
    check "peers point at each other with the other account's name" \
      ".items[] | select(.id==\"$XFER_OUT1\") | .transfer.peerId==\"$XFER_IN1\" and .transfer.peerAccountId==\"$SAV_ACC\" and .transfer.peerAccountName==\"Demo Savings\""
    request GET "/v1/transfers?month=$MONTH_UTC" "$A_TOKEN"; expect 200 "transfer report"
    check "report: Joint checking → Demo Savings 500.00 · 1 pair (auto)" \
      ".month==\"$MONTH_UTC\" and (.pairs|length==1) and .pairs[0].amountCents==50000 and .pairs[0].from.name==\"Joint checking\" and .pairs[0].to.name==\"Demo Savings\" and .pairs[0].auto and .pairs[0].from.transactionId==\"$XFER_OUT1\" and (.flows[] | select(.toName==\"Demo Savings\") | .totalCents==50000 and .count==1) and (.netByAccount[] | select(.accountId==\"$SAV_ACC\") | .inCents==50000 and .netCents==50000)"
    request GET "/v1/transfers?month=2026-13" "$A_TOKEN"; expect 400 "bad transfer month → 400"
    request GET "/v1/activity?limit=50" "$A_TOKEN"
    check "one transfer.auto activity for the pair" 'any(.events[]; .kind=="transfer.auto" and .data.count==2 and .actorId==null)'

    request PATCH /v1/couple "$A_TOKEN" '{"autoIgnoreTransfers":false}'; expect 200 "turn auto-ignore transfers off"
    check "Household.autoIgnoreTransfers false" '.autoIgnoreTransfers==false'
    request PATCH /v1/couple "$A_TOKEN" '{"autoIgnoreTransfers":null}'; expect 400 "autoIgnoreTransfers null → 400"
    phase 8
    wait_for "with auto-ignore off the next pair stays a transfer suggestion (80)" "/v1/inbox?status=pending&limit=200" \
      '[.items[] | select((.description|startswith("ONLINE TRANSFER")) and .suggestion.kind=="transfer" and .suggestion.confidence==80)] | length==2'
    XFER_OUT2="$(echo "$BODY" | jq -r '.items[] | select(.description=="ONLINE TRANSFER TO SAV" and .amountCents==-25000) | .id')"
    XFER_IN2="$(echo "$BODY" | jq -r '.items[] | select(.description=="ONLINE TRANSFER FROM CHK" and .amountCents==25000) | .id')"
    request GET "/v1/inbox/$XFER_OUT2/candidates" "$A_TOKEN"; expect 200 "candidates for the transfer leg"
    check "transfer peer candidate is the savings leg" ".transferPeers[0].id==\"$XFER_IN2\""
    request POST "/v1/inbox/$XFER_OUT2/transfer" "$A_TOKEN" "$(jq -nc --arg p "$TX_KROGER" '{peerId:$p}')"; expect 400 "peer with the same sign on the same account → 400"
    check "errors name peerId" '.errors | has("peerId")'
    request POST "/v1/inbox/$XFER_OUT2/transfer" "$A_TOKEN" '{"peerId":"00000000-0000-7000-8000-000000000000"}'; expect 400 "unknown peer → 400"
    request POST "/v1/inbox/$XFER_OUT2/transfer" "$A_TOKEN" "$(jq -nc --arg p "$XFER_IN2" '{peerId:$p}')"; expect 200 "mark the pair as a transfer by hand"
    check "manual pair: ignored/transfer, peer set, not auto" ".status==\"ignored\" and .ignoreReason==\"transfer\" and .transfer.peerId==\"$XFER_IN2\" and .transfer.auto==false"
    request GET "/v1/transfers?month=$MONTH_UTC" "$A_TOKEN"
    check "report now has 2 pairs (750.00)" '(.pairs|length==2) and .totalCents>=75000'
    request POST "/v1/inbox/$XFER_IN2/unmatch" "$A_TOKEN"; expect 200 "Not a transfer (from the other leg)"
    check "unmatched leg is pending again" '.status=="pending" and .transfer==null and .ignoreReason==null'
    request GET "/v1/inbox?status=pending&limit=200" "$A_TOKEN"
    check "…and so is its peer" "any(.items[]; .id==\"$XFER_OUT2\" and .status==\"pending\")"
    request PATCH /v1/couple "$A_TOKEN" '{"autoIgnoreTransfers":true}'; expect 200 "turn auto-ignore transfers back on"

    echo "--- v5: one-sided transfers (phase 9: TRANSFER TO BROKERAGE −1000)"
    phase 9
    wait_for "one-sided transfer keyword suggested at 60" "/v1/inbox?status=pending&limit=200" \
      '.items[] | select(.description=="TRANSFER TO BROKERAGE") | .suggestion.kind=="transfer" and .suggestion.confidence==60'
    TX_BROK1="$(echo "$BODY" | jq -r '.items[] | select(.description=="TRANSFER TO BROKERAGE") | .id')"
    request POST /v1/inbox/bulk "$A_TOKEN" "$(jq -nc --arg a "$TX_BROK1" '{action:"confirm-suggested",ids:[$a]}')"; expect 200 "bulk confirm-suggested on the keyword transfer"
    check "keyword transfers (60) are never bulk-confirmed" '.confirmed==0 and .ignored==0 and .skipped==1'
    request POST "/v1/inbox/$TX_BROK1/transfer" "$A_TOKEN" '{}'; expect 200 "mark one-sided transfer (learns the payee)"
    check "one-sided: ignored/transfer with no peer" '.status=="ignored" and .ignoreReason=="transfer" and .transfer.peerId==null and .transfer.auto==false'
    phase 10
    wait_for "learned transfer payee: the next one is auto-ignored at 85" "/v1/inbox?status=ignored&limit=200" \
      ".items[] | select(.description==\"TRANSFER TO BROKERAGE\" and .id!=\"$TX_BROK1\") | .ignoreReason==\"transfer\" and .transfer.auto==true and .suggestion.confidence==85"
    request GET "/v1/transfers?month=$MONTH_UTC" "$A_TOKEN"
    check "report: one-sided brokerage transfers flow to Other account" \
      '([.oneSided[] | select(.description=="TRANSFER TO BROKERAGE")] | length>=1) and any(.flows[]; .fromName=="Joint checking" and .toName=="Other account" and .totalCents>=100000)'

    echo "--- v5: per-account transactions"
    request POST /v1/expenses "$A_TOKEN" "$(jq -nc --arg c "$GROCERIES" --arg a "$ACC" --arg d "$TODAY_UTC" '{name:"Farmers market",amountCents:2300,categoryId:$c,accountId:$a,spentOn:$d,deduct:false}')"; expect 201 "manual expense on the linked account"
    MANUAL_EXP="$(echo "$BODY" | jq -r .id)"
    request GET "/v1/accounts/$ACC/transactions?limit=200" "$A_TOKEN"; expect 200 "account transactions (all)"
    check "bank rows ∪ manual expense; hidden hold excluded; diner shows its expense" \
      "any(.items[]; .id==\"$MANUAL_EXP\" and .kind==\"manual\" and .source==\"manual\" and .amountCents==-2300 and .expense.categoryName==\"Groceries\") and any(.items[]; .id==\"$TX_DINER\" and .kind==\"bank\" and .expense.expenseId==\"$DINER_EXP\") and ([.items[] | select(.id==\"$TX_HOLD\")] | length==0)"
    check "totals over the set: in/out/count" '.totals.count==(.items|length) and .totals.inCents==([.items[].amountCents | select(.>0)] | add // 0) and .totals.outCents==([.items[].amountCents | select(.<0) | -.] | add // 0) and .totals.pendingOutCents==450'
    # shellcheck disable=SC2016 # jq variables, not shell ones
    check "newest first" '[.items[].postedOn] as $d | $d == ($d | sort | reverse)'
    request GET "/v1/accounts/$ACC/transactions?filter=pending" "$A_TOKEN"; expect 200 "filter=pending"
    check "pending filter = bank-pending rows only" '(.items|length>=1) and all(.items[]; .pending)'
    request GET "/v1/accounts/$ACC/transactions?filter=review&limit=200" "$A_TOKEN"; expect 200 "filter=review"
    check "review filter = to review only" "(.items|length>=1) and all(.items[]; .status==\"pending\" and .kind==\"bank\") and any(.items[]; .id==\"$TX_FILL\")"
    request GET "/v1/accounts/$ACC/transactions?filter=matched&limit=200" "$A_TOKEN"; expect 200 "filter=matched"
    check "matched filter = confirmed + manual expenses" "all(.items[]; .status==\"confirmed\") and any(.items[]; .id==\"$MANUAL_EXP\") and any(.items[]; .id==\"$TX_DINER\")"
    request GET "/v1/accounts/$ACC/transactions?filter=transfers&limit=200" "$A_TOKEN"; expect 200 "filter=transfers"
    check "transfers filter" "(.items|length>=3) and all(.items[]; .ignoreReason==\"transfer\") and any(.items[]; .id==\"$XFER_OUT1\") and any(.items[]; .id==\"$TX_BROK1\")"
    request GET "/v1/accounts/$ACC/transactions?q=diner" "$A_TOKEN"; expect 200 "search q=diner"
    check "search finds the diner row only" "(.items|length==1) and .items[0].id==\"$TX_DINER\" and .totals.count==1"
    request GET "/v1/accounts/$ACC/transactions?q=market" "$A_TOKEN"; check "search covers manual expenses" "(.items|length==1) and .items[0].id==\"$MANUAL_EXP\""
    request GET "/v1/accounts/$ACC/transactions?q=%25%27%3B%20DROP%20TABLE%20x%3B--" "$A_TOKEN"; expect 200 "search is parameterised (wildcards/quotes are literal)"
    check "no rows for a literal %'; DROP…" '.items|length==0'
    request GET "/v1/accounts/$ACC/transactions?month=$MONTH_UTC&limit=200" "$A_TOKEN"; expect 200 "month filter"
    check "month filter keeps only that month" "all(.items[]; .postedOn|startswith(\"$MONTH_UTC\"))"
    request GET "/v1/accounts/$ACC/transactions?includeHidden=1&limit=200" "$A_TOKEN"
    check "includeHidden=1 shows the dropped hold" "any(.items[]; .id==\"$TX_HOLD\" and .ignoreReason==\"dropped\")"
    request GET "/v1/accounts/$ACC/transactions?limit=3" "$A_TOKEN"; expect 200 "page 1 (limit 3)"
    PAGE1="$(echo "$BODY" | jq -c '[.items[].id]')"; NEXT_TX="$(echo "$BODY" | jq -r .nextBefore)"
    [[ "$NEXT_TX" != "null" ]] || fail "account transactions page 1 should carry nextBefore"
    request GET "/v1/accounts/$ACC/transactions?limit=3&before=$NEXT_TX" "$A_TOKEN"; expect 200 "page 2 via keyset"
    check "page 2 does not repeat page 1" "([.items[].id] - $PAGE1 | length) == (.items|length) and (.items|length) > 0"
    request GET "/v1/accounts/$ACC/transactions?filter=bogus" "$A_TOKEN"; expect 400 "bad filter → 400"
    request GET "/v1/accounts/$ACC/transactions?month=2026-1" "$A_TOKEN"; expect 400 "bad month → 400"
    request GET /v1/accounts/00000000-0000-7000-8000-000000000000/transactions "$A_TOKEN"; expect 404 "foreign account → 404"

    echo "--- v5: candidates, match from any status, unmatch"
    request POST /v1/recurring "$A_TOKEN" "$(jq -nc --arg d "$TODAY_UTC" '{name:"Diner club",kind:"bill",amountCents:4800,frequency:"monthly",anchorDate:$d}')"; expect 201 "create a 48.00 monthly bill due today"
    DINER_BILL="$(echo "$BODY" | jq -r .id)"
    request GET "/v1/inbox/$TX_DINER/candidates" "$A_TOKEN"; expect 200 "candidates for the confirmed-expense row"
    check "Diner club ranks first (exact amount, same day), sign-compatible only" \
      ".occurrences[0].itemId==\"$DINER_BILL\" and .occurrences[0].dueDate==\"$TODAY_UTC\" and .occurrences[0].score>=80 and .occurrences[0].paid==null and .occurrences[0].paidByThis==false and all(.occurrences[]; .kind!=\"income\") and all(.occurrences[]; .score>=0 and .score<=100)"
    request GET "/v1/inbox/$TX_DINER/candidates?days=0" "$A_TOKEN"; expect 400 "days=0 → 400"
    request POST "/v1/inbox/$TX_DINER/match" "$A_TOKEN" "$(jq -nc --arg i "$DINER_BILL" '{itemId:$i,dueDate:"2020-01-01"}')"; expect 400 "match to a non-occurrence date → 400"
    request POST "/v1/inbox/$TX_DINER/match" "$A_TOKEN" "$(jq -nc --arg i "$DINER_BILL" --arg d "$TODAY_UTC" '{itemId:$i,dueDate:$d}')"; expect 200 "match the confirmed-expense row to the bill"
    check "row is now a bill payment (match set, expense gone)" ".status==\"confirmed\" and .match.itemId==\"$DINER_BILL\" and .match.itemName==\"Diner club\" and .match.itemKind==\"bill\" and .match.dueDate==\"$TODAY_UTC\" and .expense==null"
    request GET "/v1/expenses/$DINER_EXP" "$A_TOKEN"; expect 404 "the previous expense was deleted"
    request GET "/v1/calendar?from=$TODAY_UTC&to=$TODAY_UTC" "$A_TOKEN"
    check "occurrence paid from the bank at 48.00" ".occurrences[] | select(.itemId==\"$DINER_BILL\") | .paid.amountCents==4800 and .paid.source==\"bank\" and .varianceCents==0"
    request GET "/v1/inbox/$TX_DINER/candidates" "$A_TOKEN"
    # (the bill's earlier date, before it started, is listed too — unpaid)
    check "candidates mark the occurrence paidByThis" "[.occurrences[] | select(.itemId==\"$DINER_BILL\" and .paidByThis)] | length==1 and .[0].paid!=null and .[0].dueDate==\"$TODAY_UTC\""
    request POST "/v1/inbox/$TX_DINER/unmatch" "$A_TOKEN"; expect 200 "unmatch"
    check "back to To review" '.status=="pending" and .match==null and .expense==null and .confirmed==null'
    request GET "/v1/calendar?from=$TODAY_UTC&to=$TODAY_UTC" "$A_TOKEN"
    check "occurrence unpaid again" ".occurrences[] | select(.itemId==\"$DINER_BILL\") | .paid==null"
    request POST /v1/inbox/00000000-0000-7000-8000-000000000000/unmatch "$A_TOKEN"; expect 404 "unmatch unknown row → 404"

    echo "--- v5: bill from a transaction, payments, link from the occurrence side"
    request POST /v1/recurring/from-transaction "$A_TOKEN" "$(jq -nc --arg t "$TX_PAYROLL" '{transactionId:$t,kind:"bill"}')"; expect 400 "money in as a bill → 400"
    check "errors name kind" '.errors | has("kind")'
    request POST /v1/recurring/from-transaction "$A_TOKEN" '{"transactionId":"00000000-0000-7000-8000-000000000000"}'; expect 400 "unknown transaction → 400"
    request POST /v1/recurring/from-transaction "$A_TOKEN" "$(jq -nc --arg t "$TX_FILL" '{transactionId:$t,name:"Fuel"}')"; expect 201 "create a bill from the fill-up"
    FUEL="$(echo "$BODY" | jq -r .item.id)"
    check "item: bill, monthly, 45.12, anchor = transaction date, payeeHint = description; transaction matched to it" \
      ".item.name==\"Fuel\" and .item.kind==\"bill\" and .item.frequency==\"monthly\" and .item.amountCents==4512 and .item.anchorDate==\"$FILL_DATE\" and .item.payeeHint==\"SHELL OIL 12345\" and .transaction.status==\"confirmed\" and .transaction.match.itemId==.item.id and .transaction.match.dueDate==\"$FILL_DATE\""
    request GET "/v1/recurring/$FUEL/payments" "$A_TOKEN"; expect 200 "bill payments"
    check "next occurrence first, then the paid one with its transaction" \
      "(.[0].paid==null) and (.[] | select(.dueDate==\"$FILL_DATE\") | .paid.amountCents==4512 and .paid.source==\"bank\" and .varianceCents==0 and .transaction.id==\"$TX_FILL\" and .transaction.accountName==\"Joint checking\" and .plannedCents==4512)"
    request GET "/v1/recurring/$FUEL/payments?limit=0" "$A_TOKEN"; expect 400 "payments limit=0 → 400"
    request GET "/v1/recurring/$DINER_BILL/occurrences/$TODAY_UTC/candidates" "$A_TOKEN"; expect 200 "transaction candidates from the occurrence side"
    check "the unmatched diner row ranks first" ".[0].id==\"$TX_DINER\""
    request POST "/v1/recurring/$DINER_BILL/occurrences/$TODAY_UTC/link" "$A_TOKEN" "$(jq -nc --arg t "$TX_DINER" '{transactionId:$t}')"; expect 200 "link the diner row to the occurrence"
    check "Occurrence paid from the bank" '.paid.amountCents==4800 and .paid.source=="bank" and .status=="cleared"'
    request POST "/v1/recurring/$DINER_BILL/occurrences/$TODAY_UTC/link" "$A_TOKEN" "$(jq -nc --arg t "$TX_KROGER" '{transactionId:$t}')"; expect 409 "occurrence already paid by another transaction → 409"
    request POST "/v1/inbox/$TX_KROGER/match" "$A_TOKEN" "$(jq -nc --arg i "$DINER_BILL" --arg d "$TODAY_UTC" '{itemId:$i,dueDate:$d}')"; expect 409 "match to an occurrence paid by another transaction → 409"
    request POST "/v1/recurring/$DINER_BILL/occurrences/2020-01-01/link" "$A_TOKEN" "$(jq -nc --arg t "$TX_KROGER" '{transactionId:$t}')"; expect 404 "link on a non-occurrence date → 404"
    request GET "/v1/recurring/$DINER_BILL/payments?limit=3" "$A_TOKEN"
    check "payments show the linked transaction" ".[] | select(.dueDate==\"$TODAY_UTC\") | .transaction.id==\"$TX_DINER\" and .transaction.amountCents==-4800"
    request GET "/v1/activity?limit=100" "$A_TOKEN"
    # shellcheck disable=SC2016 # jq variables, not shell ones
    check "v5 activity kinds" '[.events[].kind] as $k | all(("transaction.matched","transaction.unmatched","transfer.paired","transfer.unpaired","recurring.from_transaction"); . as $x | $k | index($x))'

    echo "--- v5: spendable uses the available balance"
    request GET /v1/accounts "$A_TOKEN"
    SPENDABLE="$(echo "$BODY" | jq '[.[] | select(.type=="checking" or .type=="cash") | (.availableCents // .balanceCents)] | add')"
    BALANCES="$(echo "$BODY" | jq '[.[] | select(.type=="checking" or .type=="cash") | .balanceCents] | add')"
    [[ "$SPENDABLE" -lt "$BALANCES" ]] || fail "available balance (2400.00) should be below the ledger balance (2450.50)"
    request GET /v1/week "$A_TOKEN"; expect 200 "week view"
    check "week spendableNowCents = Σ checking/cash (availableCents ?? balanceCents) = $SPENDABLE" ".spendableNowCents==$SPENDABLE"
    request GET /v1/payperiod "$A_TOKEN"; expect 200 "pay period view"
    check "pay-period spendable uses the available balance too" ".spendableCents==($SPENDABLE - .remainingCents)"

    request DELETE "/v1/recurring/$DINER_BILL" "$A_TOKEN"; expect 204 "archive Diner club"
    request DELETE "/v1/recurring/$FUEL" "$A_TOKEN"; expect 204 "archive Fuel"
    request DELETE "/v1/expenses/$MANUAL_EXP" "$A_TOKEN"; expect 204 "delete the manual expense"
  else
    echo "--- v5 bank flows skipped (set SMOKE_SCHEDULER_WAIT > 0 with Sync__IntervalHours=0 to run them)"
  fi


  BIG="$(jq -nc --arg a "$ACC" '{accountId:$a,filename:"big.csv",rows:[range(1500) | {postedOn:"2024-01-15",amountCents:(-(100+.)),description:("BULK STATEMENT ROW NUMBER " + tostring + " WITH A LONG DESCRIPTION TO MAKE THE BODY LARGE")}]}')"
  [[ "${#BIG}" -gt 70000 ]] || fail "big CSV body should exceed the 64 KB global limit (is ${#BIG} bytes)"
  request POST /v1/imports/csv "$A_TOKEN" "$BIG"; expect 201 "CSV body > 64 KB accepted on the import route (2 MB limit)"
  [[ "$(echo "$BODY" | jq -r .imported)" == "1500" ]] || fail "1500 imported"
  request POST /v1/imports/csv "$A_TOKEN" "$(jq -nc --arg a "$ACC" '{accountId:$a,filename:"huge.csv",rows:[range(5001) | {postedOn:"2024-01-15",amountCents:-1,description:"x"}]}')"; expect 400 "> 5000 rows → 400"

  request PATCH "/v1/banks/$LINK" "$A_TOKEN" '{"syncEnabled":false}'; expect 200 "pause sync"
  [[ "$(echo "$BODY" | jq -r .syncEnabled)" == "false" ]] || fail "syncEnabled false"
  request PATCH "/v1/banks/$LINK" "$A_TOKEN" '{}'; expect 400 "empty patch → 400"
  request DELETE "/v1/banks/$LINK" "$B_TOKEN"; expect 204 "Sam unlinks the bank (shared household)"
  request GET /v1/banks "$A_TOKEN"; [[ "$(echo "$BODY" | jq length)" == "0" ]] || fail "link removed"
  request GET /v1/inbox/summary "$A_TOKEN"
  echo "$BODY" | jq -e '.linkCount==0 and .pending>0' >/dev/null || fail "inbox rows survive unlinking: $BODY"
  request GET "/v1/accounts/$VISA_ACC" "$A_TOKEN"; expect 200 "mapped account survives unlinking"
  request DELETE "/v1/banks/$LINK" "$A_TOKEN"; expect 404 "deleted link → 404"
  request DELETE "/v1/recurring/$RENT_V3" "$A_TOKEN"; expect 204 "archive the test Rent"
  fi # end of the built-in-engine bank flows

  if [[ -n "${SMOKE_MATCHER_URL:-}" ]]; then
    [[ "${SMOKE_SCHEDULER_WAIT:-0}" -gt 0 ]] || fail "the v6 matcher flow needs SMOKE_SCHEDULER_WAIT > 0 (and Sync__IntervalHours=0, Scheduler__IntervalSeconds=5)"
    v6_matcher_flow
  fi
  if [[ "${SMOKE_SCHEDULER_WAIT:-0}" -gt 0 ]]; then
    v7_friday_flow
  else
    echo "--- v7 Friday flow skipped: it needs SMOKE_SCHEDULER_WAIT > 0 (the sweep freeze and the bank sync run in the scheduler)"
  fi
  kill "$FAKE_PID" 2>/dev/null; wait "$FAKE_PID" 2>/dev/null || true
  trap 'rm -f "$TMP_BODY" "$TMP_REQ"' EXIT
else
  request POST /v1/banks/simplefin/claim "$A_TOKEN" '{"setupToken":"aHR0cHM6Ly9iZXRhLWJyaWRnZS5zaW1wbGVmaW4ub3JnL3NpbXBsZWZpbi9jbGFpbS9ub3BlLW5vcGU"}'
  case "$STATUS" in
    503) [[ "$(echo "$BODY" | jq -r .type)" == *bank_sync_not_configured ]] || fail "503 type"; pass "claim → 503 bank_sync_not_configured (no Security__DataKey)" ;;
    400|502) pass "claim against the real Bridge with a bogus token → $STATUS (Security__DataKey is set)" ;;
    *) fail "claim: unexpected $STATUS $BODY" ;;
  esac
fi

echo "--- v4: cleanup"
request DELETE "/v1/budgets/$TRANSPORT?period=weekly" "$A_TOKEN"; expect 204 "clear weekly Transportation budget"
request DELETE "/v1/budgets/$TRANSPORT?period=weekly" "$A_TOKEN"; expect 404 "clear again → 404"
request DELETE "/v1/budgets/$TRANSPORT" "$A_TOKEN"; expect 404 "no monthly Transportation budget to clear (periods are independent)"
request GET /v1/budgets/week "$A_TOKEN"; [[ "$(echo "$BODY" | jq -r '.categories|length')" == "1" ]] || fail "one weekly category budget left"
request DELETE "/v1/budgets/$GROCERIES?period=weekly" "$A_TOKEN"; expect 204 "clear weekly Groceries budget"
request PATCH /v1/couple "$A_TOKEN" '{"weeklySpendCents":null}'; expect 200 "clear weekly target with null"
[[ "$(echo "$BODY" | jq -r .weeklySpendCents)" == "null" ]] || fail "weeklySpendCents cleared"
request GET /v1/budgets/week "$A_TOKEN"; expect 200 "weekly budget view without budgets"
echo "$BODY" | jq -e '.overall==null and (.categories|length==0) and (.history|length==0) and .totalBudgetCents==0' >/dev/null || fail "empty weekly budget view: $BODY"
request DELETE "/v1/recurring/$ONEOFF" "$A_TOKEN"; expect 204 "archive the one-off (drops its payee rule)"
request GET "/v1/activity?limit=100" "$A_TOKEN"
echo "$BODY" | jq -e '.events[] | select(.kind=="budget.set" and .data.period=="weekly")' >/dev/null || fail "budget.set activity carries period=weekly"
echo "$BODY" | jq -e '.events[] | select(.kind=="couple.settings" and .data.weeklySpendCents==20000)' >/dev/null || fail "couple.settings activity carries weeklySpendCents"

request POST /v1/auth/password "$A_TOKEN" "$(jq -nc --arg p "$PASSWORD" '{currentPassword:$p,newPassword:"a brand new passphrase"}')"; expect 204 "change password"
request POST /v1/auth/refresh '' "$(jq -nc --arg r "$A_REFRESH" '{refreshToken:$r}')"; expect 401 "all refresh tokens revoked after password change"

if [[ -n "${SMOKE_PLAID_PORT:-}" ]]; then plaid_flow; else echo "--- v7 Plaid flow skipped (set SMOKE_PLAID_PORT; API needs Plaid__BaseUrl=http://127.0.0.1:<port>, Plaid__ClientId, Plaid__Secret)"; fi

echo "--- added by hand: POST/DELETE /v1/transactions"
request POST /v1/transactions "$A_TOKEN" "$(jq -nc --arg a "$ACC" --arg d "$(date -u +%F)" '{accountId:$a,amountCents:0,description:"Coffee",postedOn:$d}')"; expect 400 "zero amount → 400"
echo "$BODY" | jq -e '.errors | has("amountCents")' >/dev/null || fail "error names amountCents"
request POST /v1/transactions "$A_TOKEN" "$(jq -nc --arg d "$(date -u +%F)" '{accountId:"00000000-0000-7000-8000-000000000000",amountCents:-425,description:"Coffee",postedOn:$d}')"; expect 400 "unknown account → 400"
request POST /v1/transactions "$A_TOKEN" "$(jq -nc --arg a "$ACC" --arg d "$(date -u +%F)" '{accountId:$a,amountCents:-425,description:"Coffee",postedOn:$d}')"
if [[ "$STATUS" == "409" ]]; then # the fake Bridge's −4.50 coffee is already on the account today
  echo "$BODY" | jq -e '(.type|endswith("likely_duplicate")) and .duplicate.source=="simplefin"' >/dev/null || fail "409 likely_duplicate names the bank row: $BODY"
  pass "the bank already shows a coffee → 409 likely_duplicate"
  request POST /v1/transactions "$A_TOKEN" "$(jq -nc --arg a "$ACC" --arg d "$(date -u +%F)" '{accountId:$a,amountCents:-425,description:"Coffee",postedOn:$d,force:true}')"
fi
expect 201 "Alex adds a coffee by hand"
ADDED="$(echo "$BODY" | jq -r .id)"
echo "$BODY" | jq -e '.source=="added" and .pending==true and .amountCents==-425 and .description=="Coffee"' >/dev/null || fail "added row shape: $BODY"
request GET "/v1/transactions?accountId=$ACC" "$B_TOKEN"; expect 200 "Sam sees the feed"
echo "$BODY" | jq -e --arg id "$ADDED" 'any(.pending[]; .id==$id)' >/dev/null || fail "added row listed with the pending rows: $BODY"
request POST "/v1/inbox/$ADDED/confirm" "$A_TOKEN" '{"as":"expense"}'; expect 404 "an added row is not an inbox row → 404"
request GET "/v1/activity?limit=20" "$A_TOKEN"
echo "$BODY" | jq -e 'any(.events[]; .kind=="transaction.added")' >/dev/null || fail "transaction.added activity"
# it can pay a bill (counted once), be unlinked (stays added), and deleting it unpays the bill
request POST /v1/recurring "$A_TOKEN" "$(jq -nc --arg a "$ACC" --arg d "$(date -u +%F)" '{name:"Coffee club",kind:"subscription",amountCents:425,accountId:$a,frequency:"monthly",anchorDate:$d}')"; expect 201 "create Coffee club"
CLUB="$(echo "$BODY" | jq -r .id)"
request GET "/v1/inbox/$ADDED/candidates?days=30" "$A_TOKEN"; expect 200 "candidates for an added row"
echo "$BODY" | jq -e --arg i "$CLUB" '(.transferPeers|length==0) and any(.occurrences[]; .itemId==$i)' >/dev/null || fail "added row: bills only, no transfer peers: $BODY"
request POST "/v1/inbox/$ADDED/transfer" "$A_TOKEN" '{"peerId":null}'; expect 404 "an added row is never a transfer leg → 404"
request POST "/v1/inbox/$ADDED/match" "$A_TOKEN" "$(jq -nc --arg i "$CLUB" --arg d "$(date -u +%F)" '{itemId:$i,dueDate:$d}')"; expect 200 "the added coffee pays Coffee club"
echo "$BODY" | jq -e --arg i "$CLUB" '.source=="added" and .status=="confirmed" and .match.itemId==$i' >/dev/null || fail "added row matched: $BODY"
request POST "/v1/inbox/$ADDED/unmatch" "$A_TOKEN"; expect 200 "unlink it"
echo "$BODY" | jq -e '.source=="added" and .status=="confirmed" and .match==null' >/dev/null || fail "unlinked added row stays added: $BODY"
request GET "/v1/recurring/$CLUB/occurrences/$(date -u +%F)/candidates" "$A_TOKEN"; expect 200 "bill-side candidates"
echo "$BODY" | jq -e --arg t "$ADDED" 'any(.[]; .id==$t)' >/dev/null || fail "the unlinked added row is a bill-side candidate: $BODY"
request POST "/v1/recurring/$CLUB/occurrences/$(date -u +%F)/link" "$A_TOKEN" "$(jq -nc --arg t "$ADDED" '{transactionId:$t}')"; expect 200 "link it from the bill's side"
echo "$BODY" | jq -e '.paid.source=="bank" and .paid.amountCents==425' >/dev/null || fail "occurrence paid by the added row: $BODY"
request GET "/v1/recurring/$CLUB/payments" "$A_TOKEN"; expect 200 "Coffee club payments show the added row"
echo "$BODY" | jq -e --arg t "$ADDED" --arg d "$(date -u +%F)" 'any(.[]; .dueDate==$d and .transaction.id==$t)' >/dev/null || fail "payment links the added row: $BODY"
request DELETE "/v1/transactions/$ADDED" "$B_TOKEN"; expect 204 "Sam deletes it"
request GET "/v1/recurring/$CLUB/occurrences/$(date -u +%F)/candidates" "$A_TOKEN"; expect 200 "the deleted row is gone from the candidates"
echo "$BODY" | jq -e --arg t "$ADDED" 'all(.[]; .id!=$t)' >/dev/null || fail "deleted added row still a candidate"
request GET "/v1/recurring/$CLUB/payments" "$A_TOKEN"; expect 200 "Coffee club payments"
echo "$BODY" | jq -e 'all(.[]; .paid==null)' >/dev/null || fail "deleting the added row unpays the bill: $BODY"
request DELETE "/v1/recurring/$CLUB" "$A_TOKEN"; expect 204 "archive Coffee club"
request DELETE "/v1/transactions/$ADDED" "$B_TOKEN"; expect 404 "delete again → 404"

# Start over: wipes the household's money data for both of them; the couple stays.
request POST /v1/couple/reset "$A_TOKEN" '{"confirm":"yes"}'; expect 400 "reset without the word RESET → 400"
echo "$BODY" | jq -e '.errors | has("confirm")' >/dev/null || fail "reset error names confirm"
request POST /v1/couple/reset "$A_TOKEN" '{"confirm":"RESET"}'; expect 200 "Alex starts over (RESET)"
echo "$BODY" | jq -e '.paydayWeekday==5 and .autoMatchLevel=="all"' >/dev/null || fail "settings back to defaults: $BODY"
request GET /v1/accounts "$B_TOKEN"; expect 200 "Sam's view after the reset"
echo "$BODY" | jq -e 'length==0' >/dev/null || fail "no accounts left for either member"
request GET /v1/recurring "$B_TOKEN"; echo "$BODY" | jq -e 'length==0' >/dev/null || fail "no bills left: $(echo "$BODY" | head -c 300)"
request GET /v1/categories "$A_TOKEN"; echo "$BODY" | jq -e 'length>0' >/dev/null || fail "default categories seeded again"
request GET /v1/activity "$B_TOKEN"; echo "$BODY" | jq -e '[.events[].kind]==["household.reset"]' >/dev/null || fail "the feed holds only the reset: $(echo "$BODY" | head -c 300)"
pass "reset: accounts, bills and activity gone for both, categories re-seeded, still a couple"

request POST /v1/couple/leave "$B_TOKEN"; expect 200 "Sam leaves"
[[ "$(echo "$BODY" | jq -r .household)" == "null" ]] || fail "after leave household is null"
request GET /v1/couple "$A_TOKEN"; expect 200 "household kept for Alex"
[[ "$(echo "$BODY" | jq -r '.members | length')" == "1" ]] || fail "one member left"

echo "All smoke checks passed."
