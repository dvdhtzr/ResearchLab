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
in the cloud container. M2–M5 depend on the .NET 10 Worker Service and a local Ollama model, which the
container cannot provide — they are verified on the **owner's Windows machine** instead.

**Owner verification machine:** Windows · .NET SDK **10.0.401** · Ollama **0.40.16** with `qwen3.5:9b`
(and others) installed. A milestone is GREEN only when its acceptance tests actually pass there.

## Milestone status

| Milestone | Status | Notes |
|-----------|--------|-------|
| **M0 — Foundation & contracts** | **GREEN** | 34/34 tests pass on local D1 (Miniflare), incl. 8-way concurrency + DB-failure injection. Covers M0-01…M0-12. |
| **M1 — First end-to-end job** | **GREEN** | Full HTTP API router + dev auth, minimal React Mission Control, standalone mock worker. 40/40 cloud-api tests (incl. `lifecycle.test.ts` driving Submit→Queued→Claimed→Running→Completed→Display over the real Worker) + 4/4 web RTL tests. Verified live: `wrangler dev` + mock-worker CLI + `scripts/smoke.sh` (exit 0, timeline `admitted,claimed,started,completed`). |
| **M2 — Durable recovery** | **GREEN** | 12/12 `Infrastructure.Tests` pass on .NET 10.0.12 (owner's Windows box): crash survival, single-shot complete, idempotent outbox, checkpoint integrity/schema validation, budget preservation, stale-generation fencing, owner-controlled recovery. Solution builds clean. |
| **M3 — Real Ollama execution** | in progress | Inference gateway + model registry + structured-output validation. Model-free phase first (fake chat client), then a gated live test against `qwen3.5:9b`. |
| **M4 — Research Laboratory** | not started | Depends on M3. |
| **M5 — Evaluation & Mission Control** | partial | Public/owner UI + publication/digest flow are model-free and done; the evaluation lanes depend on M3/M4. |
| **M6 — Public deployment** | NOT AUTHORIZED | Requires separate owner authorization. Never executed. |

## M0 verification evidence

- `cd apps/cloud-api && npm test` → **34 M0 tests passing** (8 files).
- `npx tsc --noEmit` → clean.
- Test ↔ acceptance mapping: see `docs/contracts/m0-acceptance.md`.

## M1 verification evidence

- `cd apps/cloud-api && npm test` → **40 tests passing** (adds `lifecycle.test.ts`: 6 cases).
- `cd apps/web && npm test` → **4 RTL tests passing**; `npx vite build` → clean production bundle.
- `bash scripts/smoke.sh` → **exit 0**, `state=Completed`, timeline `admitted,claimed,started,completed`
  (real `wrangler dev` + local D1 + mock-worker CLI).
- Live-verified endpoints: owner admit/get/events/control, worker claim/start/complete, 401 on
  missing worker credential, 404 cross-owner isolation.

## M2 verification evidence

- `dotnet build .\ResearchMissionControl.slnx -c Debug` → **Build succeeded** (whole solution).
- `dotnet test .\tests\Infrastructure.Tests` → **12/12 passed** on .NET 10.0.12 (owner's Windows box).
- Covers: process termination, single-shot complete, duplicate result (idempotent outbox), interrupted
  acknowledgment, damaged/incompatible checkpoint rejection, budget preservation across recovery
  generations, stale-generation fencing, restart & resume authorization, terminate.

## M3–M5 remaining

M3 (real Ollama execution) is being implemented in two phases: a model-free gateway/registry/validation
phase, then a live test gated behind `RMC_OLLAMA_TESTS` against `qwen3.5:9b`. M4 (Research Laboratory)
and the M5 evaluation lanes depend on M3. See `docs/decisions/m2-m5-handoff.md`.

## What is explicitly NOT done / assumed

- No Cloudflare account, GitHub OAuth app, or worker credentials exist; production is blocked by design.
- `qwen3.5:9b` is already installed on the owner's machine; no models are auto-downloaded.
- Package versions use CPM floating (`10.0.*` / `*`) for first build; to be pinned to the exact
  resolved versions (spec §5) once captured from `dotnet list package`.
