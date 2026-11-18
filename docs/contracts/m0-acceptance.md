# M0 acceptance mapping

Each required M0 acceptance test maps to a concrete automated test. Run with
`cd apps/cloud-api && npm test`.

| Acceptance | Requirement | Test file › case |
|------------|-------------|------------------|
| **M0-01** | Invalid schemas rejected | `admission.test.ts › M0-01 …` (unknown props, bad version, bad enums, question bounds, visibility self-grant) |
| **M0-02** | Duplicate job admission creates one job | `admission.test.ts › M0-02 …` |
| **M0-03** | Conflicting idempotency payload rejected | `admission.test.ts › M0-03 …` |
| **M0-04** | Concurrent claims produce one winner | `claim-concurrency.test.ts › M0-04 …` (8-way `Promise.all`) |
| **M0-05** | State and audit event commit atomically | `atomic.test.ts › M0-05 …` (one event + one receipt together; rejected op writes neither) |
| **M0-06** | Stale generation rejected | `atomic.test.ts › M0-06 …` |
| **M0-07** | Expired lease requires recovery | `lease.test.ts › M0-07 …` (stale-lease commit rejected; reconcile → RecoveryRequired; valid lease untouched) |
| **M0-08** | Terminal states immutable | `state-machine.test.ts › M0-08 …` |
| **M0-09** | Unauthorized mutations rejected | `auth.test.ts › M0-09 …` (wrong worker, non-owner cancel, wrong lease id) |
| **M0-10** | Database failure cannot create false success | `atomic.test.ts › M0-10 …` (injected `batch()` failure; no state/event/receipt change) |
| **M0-11** | Recovery preserves budget and history | `recovery.test.ts › M0-11 …` (restart bumps generation, budget unchanged, events append-only) |
| **M0-12** | Tests work against local D1 without remote cloud | Entire suite runs on Miniflare-backed D1 via `@cloudflare/vitest-pool-workers`; `harness.test.ts` asserts tables present |

**Concurrency & failure injection** (required beyond happy-path): M0-04 runs 8 concurrent claims;
M0-10 injects a database failure; M0-02/M0-03 exercise duplicate submission and payload conflict.
