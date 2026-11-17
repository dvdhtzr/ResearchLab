#!/usr/bin/env pwsh
# check.ps1 — run all MODEL-FREE builds and tests. No Ollama, no cloud account.
# Returns a nonzero exit code on any failure.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$failed = $false

function Step($name, $scriptblock) {
  Write-Host "`n=== $name ===" -ForegroundColor Cyan
  try { & $scriptblock; if ($LASTEXITCODE -ne 0) { throw "exit $LASTEXITCODE" } }
  catch { Write-Host "FAILED: $name — $_" -ForegroundColor Red; $script:failed = $true }
}

Step "cloud-api: typecheck" { Push-Location "$root/apps/cloud-api"; npx tsc --noEmit; Pop-Location }
Step "cloud-api: tests"     { Push-Location "$root/apps/cloud-api"; npx vitest run; Pop-Location }
Step "web: typecheck"       { Push-Location "$root/apps/web"; npx tsc --noEmit; Pop-Location }
Step "web: tests"           { Push-Location "$root/apps/web"; npx vitest run; Pop-Location }
Step "worker-mock: typecheck" { Push-Location "$root/apps/worker-mock"; npx tsc --noEmit; Pop-Location }

if ($failed) { Write-Host "`ncheck.ps1: FAILURES" -ForegroundColor Red; exit 1 }
Write-Host "`ncheck.ps1: all model-free checks passed" -ForegroundColor Green
exit 0
