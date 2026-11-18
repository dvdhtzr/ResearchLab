#!/usr/bin/env pwsh
# start-local.ps1 — launch the full local stack in three terminal windows:
#   1) cloud API   (Cloudflare Worker + local D1)   http://localhost:8787
#   2) web console (React / Vite)                   http://localhost:5173/owner
#   3) .NET research worker (local Ollama)          polls the cloud API
#
# Usage (from the repo root or from scripts\):
#   .\scripts\start-local.ps1
#   .\scripts\start-local.ps1 -Model qwen3:30b
#
# Prereqs: Node + npm, .NET 10 SDK, Ollama running with the model installed
# (ollama list), and `npm install` already run in apps/cloud-api and apps/web.
# If running the script is blocked by execution policy:
#   powershell -ExecutionPolicy Bypass -File .\scripts\start-local.ps1

param([string]$Model)

$ErrorActionPreference = "Stop"

# Resolve the repo root whether this script sits in the repo root or in scripts\.
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (Test-Path (Join-Path $here 'apps/cloud-api')) {
    $repo = $here
} else {
    $repo = Split-Path -Parent $here
}
if (-not (Test-Path (Join-Path $repo 'apps/cloud-api'))) {
    throw "Could not locate the repo root (no apps/cloud-api found). Put this script in the repo root or in scripts\."
}

# Prefer PowerShell 7 (pwsh) if present, else fall back to Windows PowerShell.
$pwshCmd = Get-Command pwsh -ErrorAction SilentlyContinue
if ($pwshCmd) { $shell = $pwshCmd.Source } else { $shell = (Get-Command powershell).Source }

function Start-Window {
    param([string]$Title, [string]$WorkDir, [string]$Command)
    $inner = "`$host.UI.RawUI.WindowTitle = '$Title'; Set-Location -LiteralPath '$WorkDir'; $Command"
    Start-Process -FilePath $shell -ArgumentList @('-NoExit', '-NoProfile', '-Command', $inner) | Out-Null
}

Write-Host "Repo root: $repo" -ForegroundColor DarkGray

# 1) cloud API — apply migrations (idempotent) then serve on :8787 with local D1.
Start-Window -Title 'RMC cloud-api :8787' -WorkDir (Join-Path $repo 'apps/cloud-api') `
    -Command 'npx wrangler d1 migrations apply rmc-local --local; npx wrangler dev --port 8787 --local'

# 2) web console — Vite dev server on :5173 (owner console at /owner).
Start-Window -Title 'RMC web :5173' -WorkDir (Join-Path $repo 'apps/web') `
    -Command 'npm run dev'

# 3) .NET worker — wait a few seconds so the cloud API is reachable before it polls.
if ($Model) { $modelArg = " -Model $Model" } else { $modelArg = "" }
Start-Window -Title 'RMC worker' -WorkDir $repo `
    -Command "Start-Sleep -Seconds 5; .\scripts\run-worker.ps1$modelArg"

$modelLabel = if ($Model) { $Model } else { 'qwen3.5:9b (default)' }
Write-Host ""
Write-Host "Launched 3 windows:" -ForegroundColor Green
Write-Host "  1) cloud-api -> http://localhost:8787"
Write-Host "  2) web       -> http://localhost:5173/owner"
Write-Host "  3) worker    -> polls the cloud API   (model: $modelLabel)"
Write-Host ""
Write-Host "Make sure Ollama is running with the model installed (ollama list)." -ForegroundColor Yellow
Write-Host "Then submit a question at http://localhost:5173/owner and watch it get claimed + researched."
