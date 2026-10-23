#!/usr/bin/env bash
# End-to-end smoke test against a running API (default http://localhost:8080).
# Walks: register → couple code → second user joins → account → recurring → calendar → paid → summary,
# then v2: settings → pay period → expenses (deduction) → budgets → to-pay → goals → activity → push →
# passkeys config → month report → (optionally) autopay by the scheduler,
# then v3: inbox/CSV validation → (with SMOKE_SIMPLEFIN_PORT) fake SimpleFIN Bridge: claim → map → inbox → confirm/ignore/
# restore → CSV import → bulk → sync/cooldown → scheduler auto-match → undo → unlink.
# Requires: curl, jq. Usage: scripts/smoke.sh [base-url]
#   SMOKE_SCHEDULER_WAIT=<seconds>  also create an autopay bill due today and wait for the scheduler
#                                   to mark it (set to > Scheduler__IntervalSeconds); default 0 = skip.
#   SMOKE_SIMPLEFIN_PORT=<port>     start scripts/fake-simplefin.py and run the bank-sync flow (API needs Security__DataKey
#                                   and SimpleFin__BaseUrl=http://127.0.0.1:<port>; add Sync__IntervalHours=0 +
#                                   Scheduler__IntervalSeconds=5 + SMOKE_SCHEDULER_WAIT for auto-match).
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
[[ "$(echo "$BODY" | jq -r .weekStart)" == "1" ]] || fail "weekStart saved"
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

  request POST "/v1/banks/$LINK/sync" "$A_TOKEN"; expect 200 "manual sync (allowed right after linking)"
  echo "$BODY" | jq -e --arg l "$LINK" '.linkId==$l and .newTransactions==0 and .autoMatched==0 and (.errors|length==0)' >/dev/null || fail "second sync dedupes everything: $BODY"
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

request POST /v1/auth/password "$A_TOKEN" "$(jq -nc --arg p "$PASSWORD" '{currentPassword:$p,newPassword:"a brand new passphrase"}')"; expect 204 "change password"
request POST /v1/auth/refresh '' "$(jq -nc --arg r "$A_REFRESH" '{refreshToken:$r}')"; expect 401 "all refresh tokens revoked after password change"

request POST /v1/couple/leave "$B_TOKEN"; expect 200 "Sam leaves"
[[ "$(echo "$BODY" | jq -r .household)" == "null" ]] || fail "after leave household is null"
request GET /v1/couple "$A_TOKEN"; expect 200 "household kept for Alex"
[[ "$(echo "$BODY" | jq -r '.members | length')" == "1" ]] || fail "one member left"

echo "All smoke checks passed."
