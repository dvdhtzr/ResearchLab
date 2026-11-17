#!/usr/bin/env pwsh
# stop.ps1 — stop ONLY project-owned processes recorded by start.ps1.
# Never touches unrelated services.
$root = Split-Path -Parent $PSScriptRoot
$runDir = Join-Path $root ".run"
if (-not (Test-Path $runDir)) { Write-Host "nothing to stop"; exit 0 }

Get-ChildItem $runDir -Filter *.pid | ForEach-Object {
  $pid = Get-Content $_.FullName | Select-Object -First 1
  try {
    Stop-Process -Id $pid -ErrorAction Stop
    Write-Host "stopped $($_.BaseName) (pid $pid)" -ForegroundColor Green
  } catch {
    Write-Host "already stopped: $($_.BaseName) (pid $pid)" -ForegroundColor Yellow
  }
  Remove-Item $_.FullName -Force
}
