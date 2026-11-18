# ResearchMissionControl — Handoff

A living, self-contained brief for continuing this project in a new session. Pair it with
`docs/decisions/implementation-status.md` (the authoritative, test-by-test record).

---

## 1. What this is

A **local-first AI job-orchestration platform with a public research showcase**, built against the
approved _Research Mission Control — Consolidated Architecture & Implementation Specification v1.0_.
A person submits a research question in a web console; a Windows worker claims it, researches it with a
**local Ollama model over governed Wikipedia evidence** (Planner → Researcher → Skeptic → Report
Builder, every finding validated against captured evidence), and the authoritative cloud tracks state.
The **model proposes; the application authorizes.** All inference is local. No paid services.

## 2. Where things are

- **Repo:** `github.com/dvdhtzr/ResearchLab` · **Branch:** `feat/research-mission-control-m0-m5`
- **PR:** #1 (open, **do not merge**) — the review surface for all of this work.
- **Two layers / two environments:**
  - **TypeScript** (`apps/cloud-api` = Cloudflare Worker + D1; `apps/web` = React; `apps/worker-mock`)
    — built and verified in a Linux cloud container (Node only; local Miniflare D1).
  - **.NET 10** (`src/…` solution; the real Worker Service, inference gateway, SQLite durability,
    research orchestrator, evaluation) — built and verified on the **owner's Windows machine**
    (.NET 10.0.401, Ollama 0.40.16 with `qwen3.5:9b` and larger models installed).

## 3. Current status — all authorized milestones GREEN, plus enhancements

| Milestone (M0–M5) | Status |
|---|---|
| M0 Foundation & contracts, M1 end-to-end job, M2 durable recovery, M3 real Ollama, M4 Research Lab, M5 Evaluation & publication/showcase | **GREEN** |
| M6 public deployment | **NOT AUTHORIZED** (never executed) |

**Enhancements built after M5 (all working):**
- The **real .NET worker poll-loop is wired to the orchestrator** — a question submitted in the UI is
  claimed, researched by the local model, and completed back to the cloud.
- **Research reports are visible** in the owner console (summary + evidence-grounded findings), not just
  a disposition.
- **Model-assisted Planner (§7.1 — DONE, GREEN):** the local model proposes targeted search queries and
  splits "X vs Y" comparisons into one query per entity; the application authorizes them
  (`Domain/ResearchPlan.cs` → `PlanValidator`) and **fails safe** to the deterministic Planner. Evidence
  acquisition is now **balanced** across queries (round-robin fetch + passage interleaving), so
  comparisons ground on every entity, not just the first article. Comparison questions ground better.
- **Multi-source evidence (§7.2 — DONE, GREEN):** Wikidata (CC0 facts) and arXiv (CC0 scholarly
  abstracts) added as approved free sources — no key — behind a **pluggable merge**
  (`CompositeSourceSearch` round-robin + fail-soft; `CompositeSourceAdapter` host-routed + fail-closed).
  Wikipedia prose + Wikidata structured facts + arXiv abstracts; rights metadata recorded per source.
  Approved-source GETs retry transient rate-limits (429/5xx) with backoff honouring `Retry-After`
  (`HttpResilience`, arXiv at a 3 s base for its throttle); a failed source degrades to honest
  `inconclusive`, never a crash. More sources drop in via the same `ISourceSearch`/`ISourceAdapter`
  pattern.
- **Smarter deterministic Planner** (searches salient terms, not the whole question) — retained as the
  authorized fallback for the model-assisted Planner.
- **Model switching** via `RMC_MODEL` / `run-worker.ps1 -Model` (registry auto-approves the named model).
- Robustness: dev worker-auth fallback (no `.dev.vars` needed locally), HTTP connection recycling,
  reasoning-model (`<think>`) JSON handling.

**Verified test totals:** cloud-api **54**, web **9**, .NET `Infrastructure.Tests` **91**,
`Evaluation.Tests` **7**, `Integration.Tests` (live, gated by `RMC_OLLAMA_TESTS`) **7**.

## 4. Authority split (do not blur)

- **Cloud Worker API + D1 (`apps/cloud-api`)** is authoritative for: job admission & queue order, state
  transitions, execution leases & fencing generations, owner sessions, worker identities, recovery
  decisions, publication approval, public projections.
- **.NET Worker (`src/`)** is authoritative for: actual execution, local model calls, tool
  authorization, durable journals, evidence snapshots, checkpoints, report generation, evaluation.
- **React (`apps/web`)** never independently determines authoritative job state.

## 5. How to run it locally

Full walkthrough: `README.md` → "Running it locally". The essentials and the gotchas we hit:

1. **cloud API** (terminal 1): `cd apps/cloud-api`, then **apply migrations once**
   (`npx wrangler d1 migrations apply rmc-local --local`), then `npx wrangler dev --port 8787 --local`.
   Run it **from `apps/cloud-api`** (that's where `wrangler.jsonc` lives). No `.dev.vars` needed — local
   dev falls back to the worker secret `dev-worker-secret`.
2. **web** (terminal 2): `cd apps/web`, `npm run dev` → http://localhost:5173 (owner console at `/owner`).
3. **worker** (terminal 3): `.\scripts\run-worker.ps1` (model-free lifecycle demo: use the mock worker
   from README section A instead). Switch models: `.\scripts\run-worker.ps1 -Model qwen3:30b`.
4. Submit a question at `/owner`; the report appears in the job detail. Entity questions ("What is X?")
   work best; comparison/recommendation questions are hit-or-miss (see §7).

## 6. How to work on this project (operating model)

This matters: **a cloud session cannot run `dotnet` or `ollama`.** The workflow that has worked:

- **TypeScript/React** changes are built **and verified** in the session (Node is available): `npm test`,
  `npx tsc --noEmit`, `scripts/smoke.sh`.
- **.NET / live-model** changes are authored in the session but **verified by the human on Windows**.
  The loop: make the change → push → hand the human one `dotnet test …` / `scripts\check-ollama.ps1` /
  `run-worker.ps1` command → they paste the result → fix RED or record GREEN. Keep each step small; a
  first-pass C# compile error is normal and cheap to fix this way.
- **Milestone protocol:** read the contract → write tests → smallest implementation → run targeted +
  prior checks → repair → inspect diff → **commit a verified increment** → record results → advance only
  if GREEN. Never claim GREEN without the human's passing result for .NET/live work.
- **Commit cadence:** descriptive local commits per verified increment; push to the feature branch; keep
  the PR description current. Attribution trailers are already in use — match them.

## 7. Known limitations & next-step backlog

Honest state: it's a **governed research demonstrator**, not a polished analyst. It grounds what it can
and reports `inconclusive` honestly when it can't.

Highest-leverage next steps (roughly ordered):
1. ~~**Model-assisted planning** — let the model propose 1–3 targeted search queries, and split "X vs Y"
   comparison questions into separate lookups per entity.~~ **DONE (GREEN, 2026-11-18)** — model-assisted
   Planner with deterministic fallback + balanced acquisition; see
   `implementation-status.md` → "Post-M5 enhancement — model-assisted Planner". Evidence is still
   Wikipedia-only; broadening sources is item 2.
2. **Multi-source synthesis** — **DONE (GREEN, 2026-11-18):** Wikidata + arXiv added behind a pluggable
   merge (round-robin across sources, host-routed fetch, transient-429 retry); see
   `implementation-status.md` → "Post-M5 enhancement — multi-source evidence". **Remaining:**
   rank/dedupe captured passages by relevance to the question before they reach the prompt, so the
   strongest evidence leads regardless of source. Keep the loopback/no-paid-services boundary.
3. **Wire the publication flow into the owner UI** — the digest-approval → publish → public-showcase
   pipeline exists and is tested cloud-side, but the worker doesn't auto-generate a publication candidate
   and the owner UI has no "review & approve digest" screen yet.
4. **Revision rounds** — the host decision + bounded rounds exist, but the orchestrator currently does a
   single pass; actually loop Researcher↔Skeptic when challenges remain and budget allows.
5. **Semantic evaluation lane in the live path** — Lane 2 is implemented and unit-tested with a scripted
   client; it isn't run against real jobs yet.
6. **Local report viewer** (loopback-only) and **real GitHub OAuth** are specified but stubbed (local dev
   uses a fake owner identity).
7. **M6 deployment** — NOT authorized; do not deploy, do not provision paid anything.

## 8. Hard boundaries (never cross without explicit owner authorization)

- No paid services (Azure PAYG, Cloudflare paid, paid inference). Fail **closed** on quota exhaustion.
- No public deployment / no `wrangler deploy`. No merging the PR. No force-push / branch deletion.
- All inference local via Ollama; no cloud-model fallback. Ollama stays loopback-only.
- Nothing becomes public without exact-digest owner approval. Private job details never leak to public
  projections. Never weaken an INV-01…INV-10 invariant or disable a failing acceptance test.
