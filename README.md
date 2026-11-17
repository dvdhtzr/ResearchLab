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

## Quick start (local, model-free)

The cloud-API and its contract/state-machine test suite run with Node only — no Cloudflare account,
no GitHub OAuth, no Ollama:

```bash
cd apps/cloud-api
npm install
npm test          # M0 contract + state-machine + concurrency/failure tests
```

See [`docs/operations/local-dev.md`](docs/operations/local-dev.md) for the full local workflow and
[`scripts/`](scripts/) for the PowerShell entry points (`setup`, `start`, `check`, `smoke`,
`check-ollama`, `check-security`, `stop`).

## Safety invariants (never weakened)

- No paid services (Azure PAYG, Cloudflare paid, paid inference). Fail **closed** on quota exhaustion.
- Ollama is never exposed to the public Internet.
- Unapproved content cannot become publicly readable.
- A job completing technically does **not** establish research accuracy.
- Development authentication is impossible on production hosts.

## License

No license is declared yet; the owner should add one before any public distribution.
