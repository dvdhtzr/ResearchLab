#!/usr/bin/env bash
# smoke.sh — complete end-to-end local test (model-free).
# Starts wrangler dev, submits a job, drives it to completion with the mock worker,
# asserts the full lifecycle, and tears everything down. Exit nonzero on any failure.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PORT="${RMC_PORT:-8787}"
BASE="http://127.0.0.1:${PORT}"
API="${BASE}/api/v1"
SECRET="dev-worker-secret"
LOG="$(mktemp)"
WPID=""

cleanup() {
  [ -n "$WPID" ] && kill "$WPID" 2>/dev/null || true
  pkill -f "wrangler dev --port ${PORT}" 2>/dev/null || true
}
trap cleanup EXIT

fail() { echo "SMOKE FAILED: $*" >&2; exit 1; }

echo "=== applying migrations ==="
( cd "$ROOT/apps/cloud-api" && npx wrangler d1 migrations apply rmc-local --local >/dev/null 2>&1 )

echo "=== ensuring worker secret ==="
[ -f "$ROOT/apps/cloud-api/.dev.vars" ] || echo "RMC_WORKER_SHARED_SECRET=${SECRET}" > "$ROOT/apps/cloud-api/.dev.vars"

echo "=== starting wrangler dev ==="
( cd "$ROOT/apps/cloud-api" && npx wrangler dev --port "${PORT}" --local --ip 127.0.0.1 >"$LOG" 2>&1 ) &
WPID=$!
for i in $(seq 1 40); do curl -sf "${API}/public/jobs" >/dev/null 2>&1 && break; sleep 1; done
curl -sf "${API}/public/jobs" >/dev/null 2>&1 || fail "API did not become ready"

echo "=== submitting job ==="
JID=$(curl -s -X POST "${API}/owner/jobs" -H "content-type: application/json" \
  -H "Idempotency-Key: smoke-$(date +%s%N)" \
  -d '{"contractVersion":"1.0","jobType":"research.investigation.v1","request":{"question":"smoke test","evidencePolicy":"hybrid-governed"},"executionProfile":"quick","priority":"high","visibility":"private"}' \
  | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>process.stdout.write(JSON.parse(s).jobId))')
[ -n "$JID" ] || fail "no jobId returned"
echo "jobId=$JID"

echo "=== running mock worker (one cycle) ==="
( cd "$ROOT/apps/worker-mock" && RMC_API_BASE="$BASE" RMC_WORKER_ID="worker:alpha" RMC_WORKER_SECRET="$SECRET" RMC_POLL_MS=1000 \
  timeout 8 node --experimental-strip-types src/main.ts >/dev/null 2>&1 ) || true

echo "=== asserting final state ==="
STATE=$(curl -s "${API}/owner/jobs/${JID}" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>process.stdout.write(JSON.parse(s).state))')
TIMELINE=$(curl -s "${API}/owner/jobs/${JID}/events" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>process.stdout.write(JSON.parse(s).items.map(e=>e.eventType).join(",")))')
echo "state=$STATE timeline=$TIMELINE"
[ "$STATE" = "Completed" ] || fail "expected Completed, got $STATE"
[ "$TIMELINE" = "admitted,claimed,started,completed" ] || fail "unexpected timeline: $TIMELINE"

echo "SMOKE OK"
