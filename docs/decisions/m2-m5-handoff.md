# M2–M5 handoff (BLOCKED in this environment)

These milestones require a **Windows machine with the .NET 10 SDK and a local Ollama** running the
approved model. This build container (Linux, no `dotnet`, no `ollama`) cannot compile, run, or verify
them, so they are **BLOCKED — not GREEN**. The cloud side that these milestones depend on (atomic
mutations, lease/fencing, reconciliation, recovery authorization, publication approval, public
projections) is already built and tested in M0/M1.

What has been authored as **reviewable design artifacts** (clearly unverified):
- `Directory.Build.props`, `Directory.Packages.props`, `ResearchMissionControl.slnx`, and
  `src/`/`tests/` project skeletons. **Package versions are provisional**; confirm against official
  sources and pin after the first successful restore (spec §5).
- `src/ResearchMissionControl.Infrastructure/Migrations/0001_worker_core.sql` — the local SQLite
  schema (§12).
- `src/ResearchMissionControl.Domain/JobStateMachine.cs` — mirror of the authoritative state machine.
- `contracts/schemas/research/*.schema.json` — role output schemas (§22) for findings and challenges.

## First steps on Windows

```powershell
dotnet --version              # expect 10.x
ollama list                   # confirm the approved model is installed
dotnet restore                # pin versions in Directory.Packages.props afterwards
dotnet build
```

## M2 — Durable recovery (acceptance = fault-injection suite GREEN)

Implement in `Infrastructure` + `Worker`:
- SQLite journals/checkpoints/reservations/outbox per `0001_worker_core.sql`, WAL mode, FKs.
- Operation journal: reserve an attempt **before** every model/tool call; never reconstruct a missing
  interrupted result.
- Durable outbox: persist outbound messages **before** transmission; on reconnect, determine cloud
  authority, replay idempotent messages, retrieve committed receipts for ambiguous acks, refuse
  stale-generation commits.
- Checkpoints recoverable **only** after the SQLite tx commits; restore validator rejects corrupted/
  incompatible/incomplete checkpoints.
- Owner-controlled recovery only (no auto resume/restart). Recovery never resets consumed budget.

Required tests (`Integration.Tests`): process termination, interrupted acknowledgment, expired lease,
duplicate result, stale generation, damaged checkpoint, budget preservation, restart & resume
authorization. **No stale write or fabricated completion.**

## M3 — Real Ollama execution (acceptance = model-free AND live-model tests GREEN)

Inference gateway steps (§24): confirm authority → model installed & approved → expected digest →
budget → **reserve durably** → invoke Ollama → validate returned JSON (never trust structured-output
mode blindly) → record usage → commit or explicit failure → advance only after a consistent
checkpoint/journaled transition. Represent failure categories distinctly; an unknown completion is
never a success. No model downloads during execution; no cloud fallback.

Tests (gated behind `RMC_OLLAMA_TESTS`, run via `scripts/check-ollama.ps1`): structured response,
installed-model digest resolution, missing model, invalid output, cancellation, timeout, worker
restart, no paid fallback. If the model is unavailable, report **M3 blocked**, never a fake pass.

## M4 — Research Laboratory (acceptance = model-free + real-model research smoke GREEN)

Planner → acquire authorized evidence → Researcher → Skeptic → deterministic host decision
(bounded revision rounds or conclude) → Report Builder. Governed tools only
(`source.search.v1`, `source.fetch.v1`, `evidence.find.v1`, `evidence.read.v1`): the host validates
and authorizes every proposed operation; no unrestricted function calling. Evidence becomes immutable
snapshots; fetched documents are untrusted data, never instructions. A cited source is not assumed
correct. Report Builder introduces no claims absent from committed findings.

Tests: all citations reference real captured evidence; unsupported claims rejected or marked
unresolved; Skeptic references genuine findings; unauthorized tool proposals do not execute; rounds
honor budgets; invalid reports cannot complete; interrupted research is reviewable; ≥1 genuine
local-model workflow completes with auditable evidence.

## M5 — Evaluation & publication (partly model-free)

Three evaluation lanes (technical deterministic / semantic local-model-assisted / frozen laboratory
fixtures, labeled provisional until independently reviewed). Publication pipeline (§27): the cloud
verifies SHA-256 over the **exact submitted bytes** before publishing; changed artifacts cannot reuse
a prior approval; unapproved content never becomes public; public events are server-templated, never
raw LLM output. Local report viewer bound to loopback only. Size guardrails per §27.

The cloud-side digest/approval/publication schema already exists (`migrations/0003_publication.sql`,
owner/worker publication endpoints in the OpenAPI contract); M5 wires the worker upload + evaluation
lanes + the public report UI to it.
