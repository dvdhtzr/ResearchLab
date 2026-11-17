# Implementation status (honest, living record)

**Updated:** 2026-11-17 · **Branch:** `feat/research-mission-control-m0-m5`

This is the authoritative statement of what actually works. It never reports untested work as done.

## Environment this was built in

| Tool | Status |
|------|--------|
| OS | Linux (cloud container) — **not** the Windows target for the .NET worker |
| Node.js | v22.23.4 ✓ |
| npm | 10.9.9 ✓ |
| .NET SDK | **absent** (`dotnet` not installed) |
| Ollama | **absent** |
| PowerShell (`pwsh`) | **absent** (scripts authored, not executed here) |

**Consequence:** M0–M1 (TypeScript / Cloudflare / React, model-free) are fully buildable and testable
here. M2–M5 depend on the .NET 10 Worker Service and a local Ollama model, which this container
cannot provide. Those milestones are therefore **BLOCKED** for verification in this environment and
must not be reported GREEN. Their code/scaffolding may be authored, but a milestone is GREEN only when
its acceptance tests actually pass, and BLOCKED when a required live-model/runtime test cannot run.

## Milestone status

| Milestone | Status | Notes |
|-----------|--------|-------|
| **M0 — Foundation & contracts** | **GREEN** | 34/34 tests pass on local D1 (Miniflare), incl. 8-way concurrency + DB-failure injection. Covers M0-01…M0-12. |
| **M1 — First end-to-end job** | in progress | Local API router, minimal React dashboard, dev auth, mock worker, full lifecycle. |
| **M2 — Durable recovery** | BLOCKED | Requires the .NET worker (SQLite journals, checkpoints, outbox). No `dotnet` here. Cloud-side recovery authorization + reconciliation already built & tested in M0. |
| **M3 — Real Ollama execution** | BLOCKED | Requires .NET + a local Ollama model. Neither present. |
| **M4 — Research Laboratory** | BLOCKED | Depends on M3. |
| **M5 — Evaluation & Mission Control** | partial | Public/owner UI + publication/digest flow are model-free and partly buildable; the evaluation lanes depend on M3/M4. |
| **M6 — Public deployment** | NOT AUTHORIZED | Requires separate owner authorization. Never executed. |

## M0 verification evidence

- `cd apps/cloud-api && npm test` → **8 files, 34 tests, all passing**.
- `npx tsc --noEmit` → clean.
- Test ↔ acceptance mapping: see `docs/contracts/m0-acceptance.md`.

## What is explicitly NOT done / assumed

- No Cloudflare account, GitHub OAuth app, or worker credentials exist; production is blocked by design.
- No Ollama models downloaded (prohibited to auto-download).
- The .NET projects, if/when added, are authored but unverified until a Windows + .NET 10 + Ollama
  environment runs them.
