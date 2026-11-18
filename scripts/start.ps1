#!/usr/bin/env pwsh
# start.ps1 — start the local API, frontend, and mock worker as background jobs.
# PIDs are written to .run/ so stop.ps1 only stops project-owned processes.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$runDir = Join-Path $root ".run"
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

function StartProc($name, $workdir, $file, $argline) {
  $p = Start-Process -FilePath $file -ArgumentList $argline -WorkingDirectory $workdir -PassThru -NoNewWindow
  $p.Id | Out-File -FilePath (Join-Path $runDir "$name.pid")
  Write-Host "started $name (pid $($p.Id))" -ForegroundColor Green
}

StartProc "cloud-api" "$root/apps/cloud-api" "npx" "wrangler dev --port 8787 --local"
Start-Sleep -Seconds 3
StartProc "web"        "$root/apps/web"       "npx" "vite --port 5173"
StartProc "worker"     "$root/apps/worker-mock" "node" "--experimental-strip-types src/main.ts"

Write-Host "`nAPI:      http://localhost:8787/api/v1" -ForegroundColor Cyan
Write-Host "Frontend: http://localhost:5173" -ForegroundColor Cyan
Write-Host "Stop with scripts/stop.ps1"
