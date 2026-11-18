#!/usr/bin/env pwsh
# check-ollama.ps1 — run ONLY the explicitly-enabled real-model integration tests (M3+).
# Requires a local Ollama with the approved model (qwen3.5:9b) installed. If prerequisites
# are absent it reports BLOCKED and exits nonzero — it never fabricates a pass.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  Write-Host "BLOCKED: dotnet is not installed. M3+ live-model tests cannot run." -ForegroundColor Yellow
  exit 2
}
if (-not (Get-Command ollama -ErrorAction SilentlyContinue)) {
  Write-Host "BLOCKED: ollama is not installed. The local model required for live tests is unavailable." -ForegroundColor Yellow
  exit 2
}

# The live tests no-op unless this gate is set, so they never run by accident.
$env:RMC_OLLAMA_TESTS = "1"
Write-Host "Running live Ollama integration tests (requires qwen3.5:9b installed)..." -ForegroundColor Cyan
Push-Location $root
dotnet test .\tests\Integration.Tests\Integration.Tests.csproj -c Debug
$code = $LASTEXITCODE
Pop-Location

$env:RMC_OLLAMA_TESTS = $null
exit $code
