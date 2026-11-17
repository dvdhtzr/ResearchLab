#!/usr/bin/env pwsh
# smoke.ps1 — complete end-to-end local test (model-free).
# Starts wrangler dev, submits a job, drives it to completion with the mock worker,
# asserts the full lifecycle, and tears down. Nonzero exit on any failure.
# (Verified equivalent: scripts/smoke.sh.)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$port = if ($env:RMC_PORT) { $env:RMC_PORT } else { "8787" }
$base = "http://127.0.0.1:$port"
$api  = "$base/api/v1"
$secret = "dev-worker-secret"
$wrangler = $null

function Fail($m) { Write-Host "SMOKE FAILED: $m" -ForegroundColor Red; Cleanup; exit 1 }
function Cleanup { if ($wrangler) { Stop-Process -Id $wrangler.Id -ErrorAction SilentlyContinue } }

try {
  Push-Location "$root/apps/cloud-api"
  npx wrangler d1 migrations apply rmc-local --local | Out-Null
  if (-not (Test-Path ".dev.vars")) { "RMC_WORKER_SHARED_SECRET=$secret" | Out-File ".dev.vars" }

  $wrangler = Start-Process npx -ArgumentList "wrangler dev --port $port --local --ip 127.0.0.1" -PassThru -NoNewWindow
  Pop-Location

  $ready = $false
  for ($i = 0; $i -lt 40; $i++) {
    try { Invoke-RestMethod "$api/public/jobs" | Out-Null; $ready = $true; break } catch { Start-Sleep 1 }
  }
  if (-not $ready) { Fail "API did not become ready" }

  $headers = @{ "content-type" = "application/json"; "Idempotency-Key" = "smoke-$(Get-Random)" }
  $body = '{"contractVersion":"1.0","jobType":"research.investigation.v1","request":{"question":"smoke test","evidencePolicy":"hybrid-governed"},"executionProfile":"quick","priority":"high","visibility":"private"}'
  $created = Invoke-RestMethod "$api/owner/jobs" -Method Post -Headers $headers -Body $body
  $jid = $created.jobId
  if (-not $jid) { Fail "no jobId returned" }
  Write-Host "jobId=$jid"

  Push-Location "$root/apps/worker-mock"
  $env:RMC_API_BASE = $base; $env:RMC_WORKER_ID = "worker:alpha"; $env:RMC_WORKER_SECRET = $secret; $env:RMC_POLL_MS = "1000"
  $w = Start-Process node -ArgumentList "--experimental-strip-types src/main.ts" -PassThru -NoNewWindow
  Start-Sleep 8
  Stop-Process -Id $w.Id -ErrorAction SilentlyContinue
  Pop-Location

  $job = Invoke-RestMethod "$api/owner/jobs/$jid"
  $events = (Invoke-RestMethod "$api/owner/jobs/$jid/events").items | ForEach-Object { $_.eventType }
  $timeline = ($events -join ",")
  Write-Host "state=$($job.state) timeline=$timeline"
  if ($job.state -ne "Completed") { Fail "expected Completed, got $($job.state)" }
  if ($timeline -ne "admitted,claimed,started,completed") { Fail "unexpected timeline: $timeline" }

  Write-Host "SMOKE OK" -ForegroundColor Green
} finally {
  Cleanup
}
