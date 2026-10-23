#!/usr/bin/env bash
# End-to-end smoke test against a running API (default http://localhost:8080).
# Walks: register → couple code → second user joins → account → recurring → calendar → paid → summary.
# Requires: curl, jq. Usage: scripts/smoke.sh [base-url]
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

request POST /v1/auth/password "$A_TOKEN" "$(jq -nc --arg p "$PASSWORD" '{currentPassword:$p,newPassword:"a brand new passphrase"}')"; expect 204 "change password"
request POST /v1/auth/refresh '' "$(jq -nc --arg r "$A_REFRESH" '{refreshToken:$r}')"; expect 401 "all refresh tokens revoked after password change"

request POST /v1/couple/leave "$B_TOKEN"; expect 200 "Sam leaves"
[[ "$(echo "$BODY" | jq -r .household)" == "null" ]] || fail "after leave household is null"
request GET /v1/couple "$A_TOKEN"; expect 200 "household kept for Alex"
[[ "$(echo "$BODY" | jq -r '.members | length')" == "1" ]] || fail "one member left"

echo "All smoke checks passed."
