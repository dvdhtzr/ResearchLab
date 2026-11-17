# ResearchMissionControl

A local-first AI job orchestration platform with a public research showcase.

> **Implementation status:** Autonomous implementation of milestones **M0–M5** per the approved
> _Research Mission Control — Consolidated Architecture and Implementation Specification v1.0_.
> See [`docs/decisions/implementation-status.md`](docs/decisions/implementation-status.md) for the
> authoritative, honest milestone status and environment blockers.

## What this is

- A **Cloudflare Worker API** (TypeScript) + **D1** — authoritative for job admission, state,
  execution leases/fencing, owner sessions, recovery decisions, and publication approval.
- A **React Mission Control** frontend (TypeScript, Vite) — public showcase + owner administration.
- A **Windows .NET 10 Worker Service** — authoritative for actual research execution, local Ollama
  inference, durable journals, checkpoints, evidence capture, and evaluation.

The **model proposes; the application authorizes.** Cloud job authority and local research authority
do not overlap. All AI inference is local via Ollama. No paid services. No public deployment without
separate owner authorization.

## Repository layout

```
apps/web/          React Mission Control (public + owner)
apps/cloud-api/     Cloudflare Worker API + D1 migrations
contracts/          OpenAPI 3.1 + JSON Schema (canonical contracts)
src/                .NET 10 solution (Domain / Application / Infrastructure / Worker / Research / Evaluation / LocalReview)
tests/              .NET test projects
evaluation/         Frozen fixtures, benchmarks, protocols
scripts/            PowerShell operations scripts
docs/               Architecture, contracts, security, operations, decisions
```

## Running it locally

There are two layers. The **cloud API + frontend** (TypeScript) run anywhere Node runs and need no
Cloudflare account, no GitHub OAuth, and no model. The **real research worker** (.NET 10 + Ollama)
runs on Windows. You can explore the whole system with just the first layer; add the second to do
genuine local-model research.

### Prerequisites

- **Always:** [Node.js](https://nodejs.org) 20+.
- **For real research only:** Windows, the [.NET 10 SDK](https://dotnet.microsoft.com/download), and
  [Ollama](https://ollama.com) with the approved model pulled: `ollama pull qwen3.5:9b`.

### A. See the Mission Control job lifecycle (any OS, model-free)

This runs the authoritative cloud API on a local D1 database, the React UI, and a **mock worker** that
drives a job through its states — no model involved. Three terminals from the repo root:

```bash
cd apps/cloud-api && npm install && npx wrangler d1 migrations apply rmc-local --local && npx wrangler dev --port 8787 --local
```

```bash
cd apps/web && npm install && npm run dev
```

```bash
cd apps/worker-mock && npm install && RMC_API_BASE=http://127.0.0.1:8787 RMC_WORKER_SECRET=dev-worker-secret RMC_WORKER_ID=worker:alpha node --experimental-strip-types src/main.ts
```

> The cloud API reads the worker credential from `apps/cloud-api/.dev.vars`. Create it once:
> `echo "RMC_WORKER_SHARED_SECRET=dev-worker-secret" > apps/cloud-api/.dev.vars` (it is gitignored).

Then open **http://localhost:5173** (owner console at **/owner**), submit a research question, and watch
the mock worker carry it **Queued → Claimed → Running → Completed** on the live timeline. The public
showcase is at **/** and a published report renders at **/reports/:id**.

On Windows you can do all of the above with one command — `scripts\start.ps1` — and stop it with
`scripts\stop.ps1`.

### B. Run genuine local-model research (Windows + Ollama)

Build the .NET solution and run the governed research workflow against your local model. From the repo
root in PowerShell:

```powershell
dotnet build .\ResearchMissionControl.slnx -c Debug
.\scripts\check-ollama.ps1
```

`check-ollama.ps1` runs the gated live integration suite: it fetches a real Wikipedia article as
immutable evidence, then drives **Planner → Researcher → Skeptic → Report Builder** on `qwen3.5:9b`,
validating every finding against the captured evidence. It needs Ollama running and outbound HTTPS to
`en.wikipedia.org`. (The research pipeline currently runs as this harness; wiring the .NET worker's
cloud poll-loop to the orchestrator is the next integration step beyond the M0–M5 scope.)

### Verify everything

```bash
# TypeScript (any OS)
cd apps/cloud-api && npm test      # 46 — contracts, state machine, lease/fencing, publication
cd apps/web && npm test            # 9  — owner console + public showcase/report view
bash scripts/smoke.sh              # end-to-end cloud job lifecycle, exits 0
```

```powershell
# .NET (Windows); scripts\check.ps1 runs the model-free TypeScript suites too
dotnet test .\tests\Infrastructure.Tests\Infrastructure.Tests.csproj   # 43 — durability, gateway, governance, orchestrator
dotnet test .\tests\Evaluation.Tests\Evaluation.Tests.csproj           # 7  — three evaluation lanes
.\scripts\check-ollama.ps1                                             # 4  — live model + Wikipedia
```

See [`docs/operations/local-dev.md`](docs/operations/local-dev.md) for more, and [`scripts/`](scripts/)
for the PowerShell entry points (`setup`, `start`, `check`, `smoke`, `check-ollama`, `check-security`,
`stop`).

## Safety invariants (never weakened)

- No paid services (Azure PAYG, Cloudflare paid, paid inference). Fail **closed** on quota exhaustion.
- Ollama is never exposed to the public Internet.
- Unapproved content cannot become publicly readable.
- A job completing technically does **not** establish research accuracy.
- Development authentication is impossible on production hosts.

## License

No license is declared yet; the owner should add one before any public distribution.
