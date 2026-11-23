#!/usr/bin/env bash
# End-to-end test against a running stack (docker compose up): two customers, deposits,
# an idempotent transfer, a flagged transfer approved by the back office, and a balanced ledger.
set -euo pipefail
API="${API:-http://localhost:5300}"
pass() { echo "  ✓ $1"; }
fail() { echo "  ✗ $1"; exit 1; }
for _ in $(seq 1 60); do curl -fs "$API/health" >/dev/null 2>&1 && break; sleep 2; done
curl -fs "$API/health" >/dev/null || fail "API never became healthy"

call() { # METHOD PATH [BODY] [EXTRA HEADER]
  curl -s -X "$1" "$API$2" -H 'Content-Type: application/json' ${TOKEN:+-H "Authorization: Bearer $TOKEN"} ${4:+-H "$4"} ${3:+-d "$3"}
}
register() { TOKEN= call POST /api/auth/register "{\"email\":\"$1-$RANDOM@e2e.dev\",\"fullName\":\"$1\",\"password\":\"Passw0rd!\"}" | jq -r .token; }

DEMO=$(TOKEN= call POST /api/auth/login '{"email":"demo@ledgerpay.dev","password":"Demo#2025"}' | jq -r .token)
[ "$(TOKEN=$DEMO call GET /api/accounts | jq 'length')" = 3 ] && pass "demo customer seeded with 3 wallets" || fail "demo seed"

ALI=$(register Ali); SARA=$(register Sara)
[ "$ALI" != null ] && [ "$SARA" != null ] && pass "registered two customers" || fail "register"

A=$(TOKEN=$ALI call POST /api/accounts '{"name":"Main","currency":"PKR"}' | jq -r .result.id)
S_JSON=$(TOKEN=$SARA call POST /api/accounts '{"name":"Main","currency":"PKR"}')
S=$(echo "$S_JSON" | jq -r .result.id); S_NUM=$(echo "$S_JSON" | jq -r .result.number)
POS=$(TOKEN=$ALI call POST "/api/accounts/$A/deposit" '{"amount":50000,"reference":"salary"}' | jq -r .position)
[ "$(TOKEN=$ALI call GET "/api/accounts/$A?after=$POS" | jq '.balance == 50000')" = true ] && pass "deposit projected (read-your-writes)" || fail "deposit"

KEY="e2e-$RANDOM"
T1=$(TOKEN=$ALI call POST /api/transfers "{\"fromAccountId\":\"$A\",\"toAccountNumber\":\"$S_NUM\",\"amount\":1250.75,\"memo\":\"rent\"}" "Idempotency-Key: $KEY")
[ "$(echo "$T1" | jq -r .result.status)" = Completed ] && pass "transfer completed" || fail "transfer: $T1"
T1b=$(TOKEN=$ALI call POST /api/transfers "{\"fromAccountId\":\"$A\",\"toAccountNumber\":\"$S_NUM\",\"amount\":1250.75,\"memo\":\"rent\"}" "Idempotency-Key: $KEY")
[ "$(echo "$T1b" | jq -r .result.replayed)" = true ] && pass "retry with the same Idempotency-Key replayed, not re-sent" || fail "idempotency"
POS=$(echo "$T1" | jq -r .position)
[ "$(TOKEN=$SARA call GET "/api/accounts/$S?after=$POS" | jq '.balance == 1250.75')" = true ] && pass "payee received exactly once" || fail "payee balance"

BIG=$(TOKEN=$ALI call POST /api/transfers "{\"fromAccountId\":\"$A\",\"toAccountNumber\":\"$S_NUM\",\"amount\":20000}" "Idempotency-Key: big-$RANDOM")
[ "$(echo "$BIG" | jq -r .result.status)" = PendingReview ] && pass "large transfer held for review" || fail "review: $BIG"

ADMIN=$(TOKEN= call POST /api/auth/login '{"email":"admin@ledgerpay.dev","password":"Admin#2025"}' | jq -r .token)
BIG_ID=$(echo "$BIG" | jq -r .result.id)
APPROVED=$(TOKEN=$ADMIN call POST "/api/admin/transfers/$BIG_ID/approve")
[ "$(echo "$APPROVED" | jq -r .result.status)" = Completed ] && pass "reviewer approved it" || fail "approve: $APPROVED"
POS=$(echo "$APPROVED" | jq -r .position)
[ "$(TOKEN=$ALI call GET "/api/accounts/$A?after=$POS" | jq '.balance == 28749.25')" = true ] && pass "sender balance 28,749.25" || fail "sender balance"

[ "$(TOKEN=$ADMIN call GET "/api/admin/trial-balance?after=$POS" | jq -r .balanced)" = true ] && pass "trial balance: ledger nets to zero" || fail "trial balance"
[ "$(TOKEN=$ADMIN call POST /api/admin/projections/rebuild | jq '.replayed > 0')" = true ] && pass "projections rebuilt from the event log" || fail "rebuild"
[ "$(TOKEN=$ALI call GET "/api/accounts/$A" | jq '.balance == 28749.25')" = true ] && pass "same balance after rebuild" || fail "rebuild balance"
echo "All end-to-end checks passed."
