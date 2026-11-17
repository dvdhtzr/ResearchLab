# AGENTS.md — Working rules for implementing agents

This repository is built against the approved **Research Mission Control — Consolidated Architecture
and Implementation Specification v1.0**. Read `docs/decisions/implementation-status.md` first.

## Hard boundaries (never cross without separate owner authorization)

- **No paid services.** Azure PAYG, Cloudflare paid plans/storage/compute, paid inference APIs,
  purchased domains — all prohibited. Fail closed on quota exhaustion.
- **No public deployment** (M6). No `wrangler deploy` to a real account.
- **No merging** of the implementation PR; no force-push; no branch deletion.
- **All AI inference is local via Ollama.** No cloud inference fallback.
- **The model proposes; the application authorizes.** Authoritative transitions, budgets, journals,
  and checkpoints live in the application domain, not in the agent framework.

## Authority split

- **Cloudflare Worker API (`apps/cloud-api`)** is authoritative for: job admission & queue order,
  state transitions, execution leases & fencing generations, owner sessions, worker identities,
  recovery decisions, publication approval, public projections.
- **.NET Worker (`src/`)** is authoritative for: actual execution, local model calls, tool
  authorization, durable journals, evidence snapshots, checkpoints, report generation, evaluation.
- **React frontend (`apps/web`)** never independently determines authoritative job state.

## Security invariants

INV-01..INV-10 (see `docs/architecture/invariants.md`). Never weaken one, never disable a failing
acceptance test, never claim a milestone GREEN without verification. A RED gate stops advancement;
a missing required live-model test makes a milestone BLOCKED, not GREEN.

## Milestone protocol

For each milestone: read the contract → write tests → implement smallest functional solution →
run targeted + all prior checks → repair → inspect diff → commit verified increment → record exact
results → advance only if GREEN.
