#!/usr/bin/env pwsh
# setup.ps1 — verify tools, restore dependencies, initialize local databases.
# Fails clearly when prerequisites are missing. Never downloads Ollama models.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Require($cmd, $hint) {
  if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
    Write-Host "MISSING prerequisite: $cmd — $hint" -ForegroundColor Red
    exit 1
  }
  Write-Host "ok: $cmd" -ForegroundColor Green
}

Write-Host "=== prerequisites ===" -ForegroundColor Cyan
Require node "install Node.js 20+ (https://nodejs.org)"
Require npm  "bundled with Node.js"

# .NET and Ollama are only needed for M3+ (the real worker). Warn, don't fail.
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  Write-Host "warn: dotnet not found — M3+ (.NET worker) will be unavailable" -ForegroundColor Yellow
}
if (-not (Get-Command ollama -ErrorAction SilentlyContinue)) {
  Write-Host "warn: ollama not found — M3+ (local inference) will be unavailable" -ForegroundColor Yellow
}

Write-Host "`n=== restore dependencies ===" -ForegroundColor Cyan
foreach ($app in @("apps/cloud-api", "apps/web", "apps/worker-mock")) {
  Write-Host "npm install: $app"
  Push-Location "$root/$app"; npm install; if ($LASTEXITCODE -ne 0) { Pop-Location; exit 1 }; Pop-Location
}

Write-Host "`n=== initialize local D1 ===" -ForegroundColor Cyan
Push-Location "$root/apps/cloud-api"
npx wrangler d1 migrations apply rmc-local --local
Pop-Location

if (-not (Test-Path "$root/apps/cloud-api/.dev.vars")) {
  Write-Host "note: create apps/cloud-api/.dev.vars from .dev.vars.example for worker auth" -ForegroundColor Yellow
}
Write-Host "`nsetup.ps1: complete" -ForegroundColor Green
