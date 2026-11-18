# Implementation status (honest, living record)

**Updated:** 2026-11-18 · **Branch:** `feat/research-mission-control-m0-m5`

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
AUTHORIZED and was never executed. Verified test totals: cloud-api 54, web 9, .NET Infrastructure.Tests
83, .NET Evaluation.Tests 7, .NET Integration.Tests (live, gated) 6.

## Post-M5 enhancement — model-assisted Planner (HANDOFF §7.1) · GREEN

**What:** The Planner no longer reduces a question to a single deterministic keyword query. The local
model now **proposes** search queries (splitting "X vs Y" comparisons into one query per entity); the
application **authorizes** them (`Domain/ResearchPlan.cs` → `PlanValidator`: trims, collapses
whitespace, de-duplicates case-insensitively, caps count=3/length=80, and degrades an empty or
unusable proposal to the deterministic `QueryPlanner`). The orchestrator gained a budgeted `planner`
inference stage that **fails safe** to the deterministic query, plus **balanced evidence acquisition**
— round-robin document fetch across the planned queries and passage interleaving across documents — so
a comparison grounds on every entity instead of only the first article's passages. Evidence governance
and the `ResearchOrchestrator.RunAsync` signature are unchanged (`ResearchRunner`/`WorkerLoop`
untouched). "The model proposes; the application authorizes" is preserved.

**Verified (owner's Windows box, .NET 10.0.12):**
- `dotnet test .\tests\Infrastructure.Tests` → **63/63** (adds `PlanValidatorTests` ×8 and 2 new
  orchestrator cases: planner-failure fallback, and a comparison case asserting the researcher prompt
  carries evidence from BOTH entities; the 2 prior orchestrator scenarios updated to run the planner
  stage first). Model-free; also build-verified clean.
- `scripts\check-ollama.ps1` (live, `RMC_OLLAMA_TESTS=1`, `qwen3.5:9b`) → **GREEN**, incl. the new
  `M4ResearchLiveTests.ComparisonQuestion_GroundsEvidenceFromBothEntities` (a real
  "Compare PostgreSQL and MySQL" run grounded on ≥2 entity snapshots and completed), with the prior
  live tests still passing.

**Still Wikipedia-only.** Broadening to additional free/public sources is HANDOFF §7.2 (next).

## Post-M5 enhancement — multi-source evidence (HANDOFF §7.2) · GREEN

**What:** Evidence is no longer Wikipedia-only. **Wikidata** (CC0, no key, host-locked
`www.wikidata.org`) is added as a second approved free source behind a **pluggable composition**, so
the Planner's queries fan out across sources and entities/comparisons ground on structured facts as
well as prose.
- `WikidataSourceAdapter` — `wbsearchentities` (search) + `wbgetentities` (fetch) rendering
  label/description/aliases/claims into readable "property: value" facts, resolving property/entity IDs
  to labels in one bounded batch. All parsing/rendering is pure `public static` (network-free tests).
- `CompositeSourceSearch` (round-robin merge across sources, deduped, bounded; fail-soft if one source
  is down) + `CompositeSourceAdapter` (routes fetch by host; **fail-closed** on an unapproved host, on
  top of each adapter's own host lock). `ResearchRunner` wires Wikipedia + Wikidata behind both.
- **Rights metadata** is now persisted per source (`SourceDocument.Rights`): Wikipedia CC BY-SA 4.0,
  Wikidata CC0 1.0 — strengthens the §23 rights-metadata field, weakens nothing.
- **Transient-failure resilience** (`HttpResilience`): approved-source GETs retry 429/408/5xx and
  transport errors with exponential backoff honouring `Retry-After`; non-transient statuses fail fast;
  the orchestrator's search loop degrades a failed source search to an honest `inconclusive` instead of
  crashing the job. (Added after a live run hit a Wikipedia 429 under burst load.)

Governance unchanged otherwise: `EvidenceAcquisition`, finding validation, and
`ResearchOrchestrator.RunAsync` signature are untouched; "the model proposes; the application
authorizes" holds — this only governs which approved sources are consulted.

**Verified (owner's Windows box, .NET 10.0.12):**
- `dotnet test .\tests\Infrastructure.Tests` → **83/83** (adds `CompositeSourceTests` ×6,
  `WikidataSourceAdapterTests` ×9, `HttpResilienceTests` ×4, and 1 orchestrator one-source-failure
  case). Model-free; build-verified clean.
- `scripts\check-ollama.ps1` (live, `qwen3.5:9b`) → **6/6 GREEN**, incl. the new
  `M4ResearchLiveTests.MultiSource_GroundsOnWikipediaAndWikidata` (a real run grounding on ≥2 snapshots
  — one Wikipedia, one Wikidata — for a single entity), with all prior live tests passing.

**Next (HANDOFF §7.2 continuation):** arXiv is a strong second additional source and drops in via the
same `ISourceSearch`/`ISourceAdapter` + composite pattern (note its 1 req/3 s throttle).

## What is explicitly NOT done / assumed

- No Cloudflare account, GitHub OAuth app, or worker credentials exist; production is blocked by design.
- `qwen3.5:9b` is already installed on the owner's machine; no models are auto-downloaded.
- Package versions use CPM floating (`10.0.*` / `*`) for first build; to be pinned to the exact
  resolved versions (spec §5) once captured from `dotnet list package`.
