#!/usr/bin/env pwsh
# check-ollama.ps1 — run ONLY the explicitly-enabled real-model integration tests (M3+).
# These require a local Ollama with the approved model installed. If the prerequisites
# are absent, this reports BLOCKED and exits nonzero — it never fabricates a pass.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not (Get-Command ollama -ErrorAction SilentlyContinue)) {
  Write-Host "BLOCKED: ollama is not installed. M3+ live-model tests cannot run." -ForegroundColor Yellow
  exit 2
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  Write-Host "BLOCKED: dotnet is not installed. The .NET worker required for live-model tests is unavailable." -ForegroundColor Yellow
  exit 2
}

# The M3 test project is gated behind the RMC_OLLAMA_TESTS flag so it never runs by accident.
$env:RMC_OLLAMA_TESTS = "1"
Write-Host "Running real-model integration tests (RMC_OLLAMA_TESTS=1)…" -ForegroundColor Cyan
Push-Location "$root"
dotnet test --filter "Category=OllamaIntegration"
$code = $LASTEXITCODE
Pop-Location
exit $code
