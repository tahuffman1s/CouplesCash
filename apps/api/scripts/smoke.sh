#!/usr/bin/env bash
# End-to-end smoke test against a running API (default http://localhost:8080).
# Walks: register → couple code → second user joins → account → recurring → calendar → paid → summary,
# then v2: settings → pay period → expenses (deduction) → budgets → to-pay → goals → activity → push →
# passkeys config → month report → (optionally) autopay by the scheduler.
# Requires: curl, jq. Usage: scripts/smoke.sh [base-url]
#   SMOKE_SCHEDULER_WAIT=<seconds>  also create an autopay bill due today and wait for the scheduler
#                                   to mark it (set to > Scheduler__IntervalSeconds); default 0 = skip.
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
trap 'rm -f "$TMP_BODY"' EXIT
STATUS=""
BODY=""

# request METHOD PATH [TOKEN] [JSON] → sets $STATUS and $BODY
request() {
  local method="$1" path="$2" token="${3:-}" json="${4:-}"
  local -a args=(-s -o "$TMP_BODY" -w '%{http_code}' -X "$method" "$BASE$path" -H 'Content-Type: application/json')
  [[ -n "$token" ]] && args+=(-H "Authorization: Bearer $token")
  [[ -n "$json" ]] && args+=(--data "$json")
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

request POST /v1/auth/password "$A_TOKEN" "$(jq -nc --arg p "$PASSWORD" '{currentPassword:$p,newPassword:"a brand new passphrase"}')"; expect 204 "change password"
request POST /v1/auth/refresh '' "$(jq -nc --arg r "$A_REFRESH" '{refreshToken:$r}')"; expect 401 "all refresh tokens revoked after password change"

request POST /v1/couple/leave "$B_TOKEN"; expect 200 "Sam leaves"
[[ "$(echo "$BODY" | jq -r .household)" == "null" ]] || fail "after leave household is null"
request GET /v1/couple "$A_TOKEN"; expect 200 "household kept for Alex"
[[ "$(echo "$BODY" | jq -r '.members | length')" == "1" ]] || fail "one member left"

echo "All smoke checks passed."
