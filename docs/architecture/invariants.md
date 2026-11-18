# Atomic mutation invariants (INV-01 … INV-10)

These are enforced by the Cloudflare Worker + D1 layer and verified by the M0 test suite
(`apps/cloud-api/tests/`). None may be weakened.

| ID | Invariant | Enforcement | Test |
|----|-----------|-------------|------|
| INV-01 | Acknowledged jobs are durably committed | Admission returns success only after the receipt is confirmed via a read-back (`admitJob`) | `admission.test.ts` |
| INV-02 | At most one current execution lease owns a job | Conditional claim with lease/generation preconditions; worker ops require matching `worker_id`+`lease_id`+`generation`+unexpired `lease_expires_at` | `claim-concurrency.test.ts`, `auth.test.ts` |
| INV-03 | At most one initial-worker job active | Claim SQL has `NOT EXISTS (active unexpired lease)` guard | `claim-concurrency.test.ts` |
| INV-04 | One accepted transition ⇒ exactly one durable audit event | Event insert guarded on `last_mutation_id`, committed in the same `db.batch()` as the state update; `UNIQUE(job_id, sequence)` + `UNIQUE(mutation_id)` | `atomic.test.ts` |
| INV-05 | Accepted mutations have durable idempotency receipts | Receipt insert guarded + `UNIQUE(actor_id, operation, idempotency_key)`; success returned only after receipt confirmed | `admission.test.ts`, `atomic.test.ts` |
| INV-06 | Stale/expired generations cannot commit | Conditional `WHERE execution_generation = ?` + pre-validation; expired lease treated as invalid for all state-changing ops even before reconciliation | `atomic.test.ts`, `lease.test.ts` |
| INV-07 | Terminal states cannot be reopened | Transition table excludes terminal `from`-states; pre-validation + conditional `WHERE state = ?` | `state-machine.test.ts` |
| INV-08 | Resource reservations survive crashes & recovery | Budget snapshot persisted at admission; recovery never resets `budget_json` | `recovery.test.ts` |
| INV-09 | Unapproved content cannot become publicly readable | Publication requires a `publication_approvals` row matching the exact `content_digest`; public GETs read only approved rows | (M5) schema + `0003_publication.sql` |
| INV-10 | DB failures cannot produce false success | Guarded `db.batch()`; a thrown batch propagates as `CLOUD_PERSISTENCE_FAILURE`; no success path without a confirmed receipt | `atomic.test.ts` (failure injection) |

## The atomic mutation strategy

Every state-changing operation follows this pattern (`src/data/mutations.ts`):

1. **Receipt lookup** — if a receipt exists for `(actor, operation, idempotency-key)`: replay on hash
   match, else `IDEMPOTENCY_CONFLICT`.
2. **Conditional UPDATE** stamping a fresh `last_mutation_id`, with all preconditions in the `WHERE`
   (state, state_version, and — for worker ops — worker/generation/lease/expiry).
3. **Guarded event + receipt inserts** keyed off `last_mutation_id = <this mutation>` so they fire
   **only if** the update actually matched. All three statements run in one `db.batch()` (atomic).
4. **Confirm** by reading the receipt back; success is returned only then.
5. **Classify** a zero-row update by re-reading the job (stale generation / stale lease / terminal /
   version conflict) — a zero-row update is never silently treated as success.

This is never a read-then-unconditional-update. The read only populates `from_state` and precise
error messages; the authoritative guard is always the conditional SQL.
