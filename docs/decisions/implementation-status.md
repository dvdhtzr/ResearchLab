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
| **M3 — Real Ollama execution** | **GREEN** | 26 model-free `Infrastructure.Tests` (gateway failure taxonomy, digest/registry checks, independent JSON validation, loopback-only security guard) + 3 live `Integration.Tests` against `qwen3.5:9b` (digest resolution, missing-model rejection, structured JSON response) via `scripts/check-ollama.ps1`. Ollama reached over REST (HttpClient), loopback-only, no SDK dependency. |
| **M4 — Research Laboratory** | **GREEN** | 43 model-free `Infrastructure.Tests` (evidence governance, tool authorization, role validators, orchestrator with scripted client, JSON extraction) + live `Integration.Tests` research run: real Wikipedia evidence → `qwen3.5:9b` Planner→Researcher→Skeptic→Report, every finding validated against captured passages. Reasoning-model (`<think>`) output handled. |
| **M5 — Evaluation & Mission Control** | **GREEN** | Cloud publication/digest pipeline (46/46 cloud tests) + public showcase/report view (9/9 web tests), both verified in the container; three-lane evaluation (7/7 `Evaluation.Tests` on the owner's machine). Privacy, exact-digest approval, no-reuse, withdrawal, and evaluation-independent-of-execution all enforced. |
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

## M3 verification evidence

- `dotnet test Infrastructure.Tests` → **26/26** (adds 10 gateway + 4 loopback-guard to the M2 12).
- `scripts/check-ollama.ps1` → **3/3** live against `qwen3.5:9b` on the owner's machine.

## M4 verification evidence

- `dotnet test Infrastructure.Tests` → **43/43** (adds evidence governance, orchestrator, JSON extraction).
- `scripts/check-ollama.ps1` → **4/4** live, incl. the end-to-end research workflow producing an
  evidence-grounded report from a real Wikipedia article via `qwen3.5:9b`.

## M5 verification evidence

- `cd apps/cloud-api && npm test` → **46/46** (adds 6 publication-pipeline tests).
- `cd apps/web && npm test` → **9/9** (adds 5 public showcase / report-view tests).
- `dotnet test Evaluation.Tests` → **7/7** on the owner's machine (three lanes).

## All milestones GREEN (M0–M5)

The complete authorized scope is implemented and verified. M6 (public deployment) remains NOT
AUTHORIZED and was never executed. Verified test totals: cloud-api 46, web 9, .NET Infrastructure.Tests
43, .NET Evaluation.Tests 7, .NET Integration.Tests (live, gated) 4.

## What is explicitly NOT done / assumed

- No Cloudflare account, GitHub OAuth app, or worker credentials exist; production is blocked by design.
- `qwen3.5:9b` is already installed on the owner's machine; no models are auto-downloaded.
- Package versions use CPM floating (`10.0.*` / `*`) for first build; to be pinned to the exact
  resolved versions (spec §5) once captured from `dotnet list package`.
