#!/usr/bin/env pwsh
# run-worker.ps1 — run the real .NET research worker against a locally-running cloud API
# and Ollama. It polls for claimed jobs and runs the governed orchestrator for each.
#
#   .\scripts\run-worker.ps1                    # uses qwen3.5:9b (or $env:RMC_MODEL)
#   .\scripts\run-worker.ps1 -Model qwen3:30b   # use a bigger installed model
#
# Prerequisites (separate terminals):
#   1. cloud API:  cd apps/cloud-api; npx wrangler dev --port 8787 --local
#   2. Ollama running with the chosen model installed (ollama list)
#   3. outbound HTTPS to en.wikipedia.org
# Then submit a job at http://localhost:5173/owner and watch it get claimed + researched.
param([string]$Model)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if ($Model) { $env:RMC_MODEL = $Model }
if (-not $env:RMC_API_BASE) { $env:RMC_API_BASE = "http://localhost:8787" }
if (-not $env:RMC_WORKER_ID) { $env:RMC_WORKER_ID = "worker:alpha" }
if (-not $env:RMC_WORKER_SECRET) { $env:RMC_WORKER_SECRET = "dev-worker-secret" }
if (-not $env:RMC_MODEL) { $env:RMC_MODEL = "qwen3.5:9b" }

Write-Host "Starting .NET research worker -> $($env:RMC_API_BASE) (Ctrl+C to stop)" -ForegroundColor Cyan
dotnet run --project "$root/src/ResearchMissionControl.Worker/ResearchMissionControl.Worker.csproj" -c Debug
