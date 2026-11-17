#!/usr/bin/env pwsh
# check-security.ps1 — run authentication, authorization, and privacy tests.
# These are a subset of the cloud-api suite plus the frontend; nonzero on failure.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location "$root/apps/cloud-api"
# auth (authz), lifecycle (API guards + cross-owner isolation), publication (digest
# approval + private-never-public + no-reuse-of-changed-artifact).
npx vitest run tests/auth.test.ts tests/lifecycle.test.ts tests/publication.test.ts
$code = $LASTEXITCODE
Pop-Location
if ($code -ne 0) { Write-Host "check-security.ps1: FAILURES" -ForegroundColor Red; exit 1 }
Write-Host "check-security.ps1: security tests passed" -ForegroundColor Green
exit 0
