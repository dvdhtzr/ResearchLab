import { ApiError } from "../domain/errors.js";
import { newId } from "../domain/ids.js";
import { budgetForProfile } from "../domain/budget.js";
import { isTerminal, type JobState } from "../domain/states.js";
import {
  ruleFor,
  targetState,
  type TransitionOp,
} from "../domain/transitions.js";
import type { ValidatedJobCreation } from "../domain/validation.js";
import { resolveReceipt } from "./receipts.js";
import { CONTRACT_VERSION, type Actor, type JobRow, type MutationSuccess } from "./types.js";

// ---------------------------------------------------------------------------
// Shared helpers
// ---------------------------------------------------------------------------

type Sql = D1PreparedStatement;

function successResponse(
  jobId: string,
  stateVersion: number,
  executionGeneration: number,
  now: number,
  extra: Record<string, unknown> = {},
): MutationSuccess {
  return {
    contractVersion: CONTRACT_VERSION,
    operationId: newId(),
    jobId,
    stateVersion,
    executionGeneration,
    acceptedAt: new Date(now).toISOString(),
    outcome: "accepted",
    ...extra,
  };
}

/** Guarded event insert — fires only if the job now carries this mutationId. */
function eventStmt(
  db: D1Database,
  p: {
    jobId: string;
    fromState: JobState;
    toState: JobState;
    eventType: string;
    actor: Actor;
    mutationId: string;
    details: Record<string, unknown>;
    now: number;
  },
): Sql {
  return db
    .prepare(
      `INSERT INTO job_events
         (event_id, job_id, sequence, from_state, to_state, event_type,
          actor_type, actor_id, execution_generation, mutation_id, details_json, created_at)
       SELECT ?, j.id,
              COALESCE((SELECT MAX(sequence) FROM job_events WHERE job_id = j.id), 0) + 1,
              ?, ?, ?, ?, ?, j.execution_generation, ?, ?, ?
         FROM jobs j
        WHERE j.id = ? AND j.last_mutation_id = ?`,
    )
    .bind(
      newId(),
      p.fromState,
      p.toState,
      p.eventType,
      p.actor.type,
      p.actor.id,
      p.mutationId,
      JSON.stringify(p.details),
      p.now,
      p.jobId,
      p.mutationId,
    );
}

/** Guarded receipt insert — fires only if the job now carries this mutationId. */
function receiptStmt(
  db: D1Database,
  p: {
    actor: Actor;
    operation: string;
    idempotencyKey: string;
    requestHash: string;
    mutationId: string;
    jobId: string;
    response: unknown;
    now: number;
    guardOnJob?: boolean; // default true
  },
): Sql {
  const guard = p.guardOnJob === false;
  if (guard) {
    // Unconditional (used by admission, where the INSERT jobs is the guard).
    return db
      .prepare(
        `INSERT INTO mutation_receipts
           (receipt_id, actor_id, operation, idempotency_key, request_hash, mutation_id, job_id, response_json, accepted_at)
         VALUES (?,?,?,?,?,?,?,?,?)`,
      )
      .bind(
        newId(),
        p.actor.id,
        p.operation,
        p.idempotencyKey,
        p.requestHash,
        p.mutationId,
        p.jobId,
        JSON.stringify(p.response),
        p.now,
      );
  }
  return db
    .prepare(
      `INSERT INTO mutation_receipts
         (receipt_id, actor_id, operation, idempotency_key, request_hash, mutation_id, job_id, response_json, accepted_at)
       SELECT ?,?,?,?,?,?,?,?,?
        WHERE EXISTS (SELECT 1 FROM jobs WHERE id = ? AND last_mutation_id = ?)`,
    )
    .bind(
      newId(),
      p.actor.id,
      p.operation,
      p.idempotencyKey,
      p.requestHash,
      p.mutationId,
      p.jobId,
      JSON.stringify(p.response),
      p.now,
      p.jobId,
      p.mutationId,
    );
}

async function loadJob(db: D1Database, jobId: string): Promise<JobRow | null> {
  return db.prepare("SELECT * FROM jobs WHERE id = ?").bind(jobId).first<JobRow>();
}

// ---------------------------------------------------------------------------
// Job admission (POST /owner/jobs)
// ---------------------------------------------------------------------------

export interface AdmitArgs {
  db: D1Database;
  now: number;
  owner: Actor; // actor.type === 'owner'
  idempotencyKey: string;
  requestHash: string;
  creation: ValidatedJobCreation;
}

export async function admitJob(args: AdmitArgs): Promise<MutationSuccess> {
  const { db, now, owner, idempotencyKey, requestHash, creation } = args;
  const operation = "owner.admit";

  const replay = await resolveReceipt(db, owner, operation, idempotencyKey, requestHash);
  if (replay) return replay as MutationSuccess;

  const jobId = newId();
  const mutationId = newId();
  const budget = budgetForProfile(creation.executionProfile);
  const response = successResponse(jobId, 0, 0, now, { state: "Queued" });

  const insertJob = db
    .prepare(
      `INSERT INTO jobs
        (id, contract_version, job_type, owner_id, request_json, execution_profile,
         budget_json, priority, state, state_version, execution_generation,
         hold_requested, cancel_requested, evaluation_status, publication_status,
         visibility, admission_idempotency_key, admission_request_hash,
         last_mutation_id, created_at, updated_at)
       VALUES (?,?,?,?,?,?,?,?, 'Queued', 0, 0, 0, 0, 'none', 'private', ?, ?, ?, ?, ?, ?)`,
    )
    .bind(
      jobId,
      CONTRACT_VERSION,
      creation.jobType,
      owner.id,
      JSON.stringify(creation.request),
      creation.executionProfile,
      JSON.stringify(budget),
      creation.priority,
      creation.visibility,
      idempotencyKey,
      requestHash,
      mutationId,
      now,
      now,
    );

  const event = eventStmt(db, {
    jobId,
    fromState: "Queued", // admission has no prior state; record Queued->Queued as 'admitted'
    toState: "Queued",
    eventType: "admitted",
    actor: owner,
    mutationId,
    details: { executionProfile: creation.executionProfile, priority: creation.priority },
    now,
  });

  const receipt = receiptStmt(db, {
    actor: owner,
    operation,
    idempotencyKey,
    requestHash,
    mutationId,
    jobId,
    response,
    now,
    guardOnJob: false,
  });

  try {
    await db.batch([insertJob, event, receipt]);
  } catch (e) {
    // Unique (owner, admission_idempotency_key) OR (actor, operation, key) collision
    // from a concurrent duplicate: the other request won. Replay its receipt.
    const existing = await resolveReceipt(db, owner, operation, idempotencyKey, requestHash);
    if (existing) return existing as MutationSuccess;
    // Different request content reusing the same admission key on an existing job.
    const dup = await db
      .prepare("SELECT 1 FROM jobs WHERE owner_id = ? AND admission_idempotency_key = ?")
      .bind(owner.id, idempotencyKey)
      .first();
    if (dup) {
      throw new ApiError(
        "IDEMPOTENCY_CONFLICT",
        "This idempotency key already admitted a different job.",
      );
    }
    throw mapDbError(e);
  }

  const committed = await resolveReceipt(db, owner, operation, idempotencyKey, requestHash);
  if (!committed) {
    throw new ApiError("CLOUD_PERSISTENCE_FAILURE", "Admission did not durably commit.");
  }
  return committed as MutationSuccess;
}

// ---------------------------------------------------------------------------
// Generic single-job transition
// ---------------------------------------------------------------------------

export interface TransitionArgs {
  db: D1Database;
  now: number;
  actor: Actor;
  operation: string;
  idempotencyKey: string;
  requestHash: string;
  jobId: string;
  op: TransitionOp;
  eventType: string;
  details?: Record<string, unknown>;
  /** Worker lease ownership requirement (worker-driven transitions). */
  lease?: { workerId: string; leaseId: string; generation: number };
  /** Owner ownership requirement (owner-driven transitions). */
  ownerId?: string;
  /** Extra column assignments committed with the transition. */
  extraSet?: Record<string, string | number | null>;
  /** When true, the new state clears lease/worker fields (terminal / requeue). */
  clearLease?: boolean;
  /** Increment the execution generation (recovery requeue starts a new fence). */
  bumpGeneration?: boolean;
}

export async function transition(args: TransitionArgs): Promise<MutationSuccess> {
  const {
    db,
    now,
    actor,
    operation,
    idempotencyKey,
    requestHash,
    jobId,
    op,
    eventType,
  } = args;

  const replay = await resolveReceipt(db, actor, operation, idempotencyKey, requestHash);
  if (replay) return replay as MutationSuccess;

  const job = await loadJob(db, jobId);
  if (!job) throw new ApiError("NOT_FOUND", "Job not found.");

  // Precise pre-validation (authoritative guard is still the conditional SQL below).
  validateTransitionPreconditions(job, args, now);

  const rule = ruleFor(op);
  const toState = targetState(op);
  const mutationId = newId();
  const newVersion = job.state_version + 1;
  const newGeneration = args.bumpGeneration
    ? job.execution_generation + 1
    : job.execution_generation;

  // Build SET clause.
  const sets: string[] = ["state = ?", "state_version = ?", "last_mutation_id = ?", "updated_at = ?"];
  const setBinds: (string | number | null)[] = [toState, newVersion, mutationId, now];
  if (args.bumpGeneration) {
    sets.push("execution_generation = ?");
    setBinds.push(newGeneration);
  }
  if (args.clearLease) {
    sets.push("worker_id = NULL", "lease_id = NULL", "lease_expires_at = NULL");
  }
  for (const [col, val] of Object.entries(args.extraSet ?? {})) {
    sets.push(`${col} = ?`);
    setBinds.push(val);
  }

  // Build WHERE preconditions (state, version, and ownership/lease/generation).
  const where: string[] = ["id = ?", "state = ?", "state_version = ?"];
  const whereBinds: (string | number)[] = [jobId, job.state, job.state_version];
  if (args.lease) {
    where.push("worker_id = ?", "lease_id = ?", "execution_generation = ?", "lease_expires_at > ?");
    whereBinds.push(args.lease.workerId, args.lease.leaseId, args.lease.generation, now);
  }
  if (args.ownerId) {
    where.push("owner_id = ?");
    whereBinds.push(args.ownerId);
  }

  const updateSql = `UPDATE jobs SET ${sets.join(", ")} WHERE ${where.join(" AND ")}`;
  const update = db.prepare(updateSql).bind(...setBinds, ...whereBinds);

  const response = successResponse(jobId, newVersion, newGeneration, now, {
    state: toState,
    ...(args.lease ? {} : {}),
  });

  const event = eventStmt(db, {
    jobId,
    fromState: job.state,
    toState,
    eventType,
    actor,
    mutationId,
    details: args.details ?? {},
    now,
  });
  const receipt = receiptStmt(db, {
    actor,
    operation,
    idempotencyKey,
    requestHash,
    mutationId,
    jobId,
    response,
    now,
  });

  try {
    await db.batch([update, event, receipt]);
  } catch (e) {
    const existing = await resolveReceipt(db, actor, operation, idempotencyKey, requestHash);
    if (existing) return existing as MutationSuccess;
    throw mapDbError(e);
  }

  const committed = await resolveReceipt(db, actor, operation, idempotencyKey, requestHash);
  if (committed) return committed as MutationSuccess;

  // Zero-row update, no receipt: a concurrent writer changed the row. Classify.
  throw await classifyTransitionConflict(db, jobId, job.state, rule.from, args, now);
}

function validateTransitionPreconditions(job: JobRow, args: TransitionArgs, now: number): void {
  const rule = ruleFor(args.op);

  if (isTerminal(job.state)) {
    throw new ApiError("STATE_CONFLICT", `Job is terminal (${job.state}) and cannot be modified.`);
  }
  if (!rule.from.includes(job.state)) {
    throw new ApiError(
      "STATE_CONFLICT",
      `Operation '${args.op}' is not allowed from state ${job.state}.`,
    );
  }
  if (args.ownerId && job.owner_id !== args.ownerId) {
    throw new ApiError("UNAUTHORIZED", "Not the owner of this job.");
  }
  if (args.lease) {
    // Precondition order per spec §9: worker identity → generation → lease identity → expiry.
    if (job.worker_id !== args.lease.workerId) {
      throw new ApiError("UNAUTHORIZED", "This worker is not assigned to the job.");
    }
    if (job.execution_generation !== args.lease.generation) {
      throw new ApiError(
        "STALE_EXECUTION_GENERATION",
        "The execution no longer owns this job (generation superseded).",
      );
    }
    if (job.lease_id !== args.lease.leaseId) {
      throw new ApiError("STALE_LEASE", "The execution lease has been superseded.");
    }
    if (job.lease_expires_at === null || job.lease_expires_at <= now) {
      throw new ApiError("STALE_LEASE", "The execution lease has expired.");
    }
  }
}

async function classifyTransitionConflict(
  db: D1Database,
  jobId: string,
  originalState: JobState,
  allowedFrom: readonly JobState[],
  args: TransitionArgs,
  now: number,
): Promise<ApiError> {
  const job = await loadJob(db, jobId);
  if (!job) return new ApiError("NOT_FOUND", "Job not found.");
  if (isTerminal(job.state)) {
    return new ApiError("STATE_CONFLICT", `Job is terminal (${job.state}).`);
  }
  if (args.lease) {
    if (job.worker_id !== args.lease.workerId) {
      return new ApiError("UNAUTHORIZED", "Worker is not assigned to the job.");
    }
    if (job.execution_generation !== args.lease.generation) {
      return new ApiError("STALE_EXECUTION_GENERATION", "Execution generation superseded.");
    }
    if (job.lease_id !== args.lease.leaseId) {
      return new ApiError("STALE_LEASE", "The execution lease has been superseded.");
    }
    if (job.lease_expires_at === null || job.lease_expires_at <= now) {
      return new ApiError("STALE_LEASE", "The execution lease has expired.");
    }
  }
  if (!allowedFrom.includes(job.state)) {
    return new ApiError("STATE_CONFLICT", `State changed to ${job.state}.`);
  }
  return new ApiError("VERSION_CONFLICT", "The job was modified concurrently; retry with fresh state.");
}

// ---------------------------------------------------------------------------
// Claim (POST /worker/claims) — atomic single-active selection
// ---------------------------------------------------------------------------

export interface ClaimArgs {
  db: D1Database;
  now: number;
  worker: Actor; // actor.type === 'worker'
  idempotencyKey: string;
  requestHash: string;
  leaseSeconds: number;
  authorizedJobTypes: readonly string[];
}

export interface ClaimResult {
  claimed: boolean;
  response: MutationSuccess | { contractVersion: string; outcome: "no_work" };
}

export async function claimNext(args: ClaimArgs): Promise<ClaimResult> {
  const { db, now, worker, idempotencyKey, requestHash, leaseSeconds, authorizedJobTypes } = args;
  const operation = "worker.claim";

  const replay = await resolveReceipt(db, worker, operation, idempotencyKey, requestHash);
  if (replay) {
    const r = replay as MutationSuccess & { outcome: string };
    return { claimed: r.outcome === "accepted", response: r as never };
  }

  const leaseId = newId();
  const mutationId = newId();
  const leaseExpiresAt = now + leaseSeconds * 1000;
  const typeList = authorizedJobTypes.map(() => "?").join(",");

  // Atomic: claim the single top-priority queued job, but only if NO other job
  // currently holds an unexpired active lease (single-worker INV-03).
  const update = db
    .prepare(
      `UPDATE jobs SET
          state = 'Claimed',
          worker_id = ?,
          lease_id = ?,
          lease_expires_at = ?,
          execution_generation = execution_generation + 1,
          state_version = state_version + 1,
          last_mutation_id = ?,
          updated_at = ?
        WHERE id = (
          SELECT id FROM jobs
           WHERE state = 'Queued'
             AND hold_requested = 0
             AND cancel_requested = 0
             AND job_type IN (${typeList})
           ORDER BY (CASE priority WHEN 'high' THEN 0 WHEN 'normal' THEN 1 ELSE 2 END),
                    created_at ASC, id ASC
           LIMIT 1
        )
        AND NOT EXISTS (
          SELECT 1 FROM jobs j2
           WHERE j2.state IN ('Claimed','Running')
             AND j2.lease_expires_at > ?
        )`,
    )
    .bind(worker.id, leaseId, leaseExpiresAt, mutationId, now, ...authorizedJobTypes, now);

  // Event + receipt are guarded on last_mutation_id; they fire only if a row was claimed.
  const event = db
    .prepare(
      `INSERT INTO job_events
         (event_id, job_id, sequence, from_state, to_state, event_type,
          actor_type, actor_id, execution_generation, mutation_id, details_json, created_at)
       SELECT ?, j.id,
              COALESCE((SELECT MAX(sequence) FROM job_events WHERE job_id = j.id), 0) + 1,
              'Queued', 'Claimed', 'claimed', ?, ?, j.execution_generation, ?, ?, ?
         FROM jobs j
        WHERE j.last_mutation_id = ?`,
    )
    .bind(newId(), worker.type, worker.id, mutationId, JSON.stringify({ leaseSeconds }), now, mutationId);

  const receipt = db
    .prepare(
      `INSERT INTO mutation_receipts
         (receipt_id, actor_id, operation, idempotency_key, request_hash, mutation_id, job_id, response_json, accepted_at)
       SELECT ?, ?, ?, ?, ?, ?, (SELECT id FROM jobs WHERE last_mutation_id = ?),
              (SELECT '{"placeholder":true}'), ?
        WHERE EXISTS (SELECT 1 FROM jobs WHERE last_mutation_id = ?)`,
    )
    .bind(newId(), worker.id, operation, idempotencyKey, requestHash, mutationId, mutationId, now, mutationId);

  try {
    await db.batch([update, event, receipt]);
  } catch (e) {
    const existing = await resolveReceipt(db, worker, operation, idempotencyKey, requestHash);
    if (existing) {
      const r = existing as MutationSuccess & { outcome: string };
      return { claimed: r.outcome === "accepted", response: r as never };
    }
    throw mapDbError(e);
  }

  // Did we claim? Find the job carrying our mutationId.
  const claimedJob = await db
    .prepare("SELECT * FROM jobs WHERE last_mutation_id = ?")
    .bind(mutationId)
    .first<JobRow>();

  if (claimedJob) {
    const response = successResponse(
      claimedJob.id,
      claimedJob.state_version,
      claimedJob.execution_generation,
      now,
      {
        state: "Claimed",
        leaseId,
        leaseExpiresAt: new Date(leaseExpiresAt).toISOString(),
        workerId: worker.id,
      },
    );
    // Persist the real response into the receipt (it was inserted with a placeholder).
    await db
      .prepare("UPDATE mutation_receipts SET response_json = ? WHERE mutation_id = ?")
      .bind(JSON.stringify(response), mutationId)
      .run();
    return { claimed: true, response };
  }

  // No work (or another active lease blocks us). Record an idempotent no-work receipt.
  const noWork = { contractVersion: CONTRACT_VERSION, outcome: "no_work" as const };
  try {
    await db
      .prepare(
        `INSERT INTO mutation_receipts
           (receipt_id, actor_id, operation, idempotency_key, request_hash, mutation_id, job_id, response_json, accepted_at)
         VALUES (?,?,?,?,?,?,NULL,?,?)`,
      )
      .bind(newId(), worker.id, operation, idempotencyKey, requestHash, mutationId, JSON.stringify(noWork), now)
      .run();
  } catch {
    const existing = await resolveReceipt(db, worker, operation, idempotencyKey, requestHash);
    if (existing) {
      const r = existing as MutationSuccess & { outcome: string };
      return { claimed: r.outcome === "accepted", response: r as never };
    }
  }
  return { claimed: false, response: noWork };
}

// ---------------------------------------------------------------------------
// Heartbeat (POST /worker/jobs/{id}/heartbeat) — extend the lease
// ---------------------------------------------------------------------------

export interface HeartbeatArgs {
  db: D1Database;
  now: number;
  worker: Actor;
  idempotencyKey: string;
  requestHash: string;
  jobId: string;
  lease: { workerId: string; leaseId: string; generation: number };
  leaseSeconds: number;
}

export async function heartbeat(args: HeartbeatArgs): Promise<MutationSuccess> {
  const { db, now, worker, idempotencyKey, requestHash, jobId, lease, leaseSeconds } = args;
  const operation = "worker.heartbeat";

  const replay = await resolveReceipt(db, worker, operation, idempotencyKey, requestHash);
  if (replay) return replay as MutationSuccess;

  const job = await loadJob(db, jobId);
  if (!job) throw new ApiError("NOT_FOUND", "Job not found.");
  if (job.state !== "Claimed" && job.state !== "Running") {
    throw new ApiError("STATE_CONFLICT", `Heartbeat not valid in state ${job.state}.`);
  }
  if (job.worker_id !== lease.workerId || job.lease_id !== lease.leaseId) {
    throw new ApiError("UNAUTHORIZED", "Worker does not own this lease.");
  }
  if (job.execution_generation !== lease.generation) {
    throw new ApiError("STALE_EXECUTION_GENERATION", "Execution generation superseded.");
  }
  if (job.lease_expires_at === null || job.lease_expires_at <= now) {
    throw new ApiError("STALE_LEASE", "Lease already expired; recovery required.");
  }

  const mutationId = newId();
  const newExpiry = now + leaseSeconds * 1000;
  const newVersion = job.state_version + 1;
  const response = successResponse(jobId, newVersion, job.execution_generation, now, {
    state: job.state,
    leaseExpiresAt: new Date(newExpiry).toISOString(),
  });

  const update = db
    .prepare(
      `UPDATE jobs SET lease_expires_at = ?, state_version = state_version + 1,
              last_mutation_id = ?, updated_at = ?
        WHERE id = ? AND worker_id = ? AND lease_id = ? AND execution_generation = ?
          AND lease_expires_at > ? AND state IN ('Claimed','Running')`,
    )
    .bind(newExpiry, mutationId, now, jobId, lease.workerId, lease.leaseId, lease.generation, now);

  const receipt = receiptStmt(db, {
    actor: worker,
    operation,
    idempotencyKey,
    requestHash,
    mutationId,
    jobId,
    response,
    now,
  });

  try {
    await db.batch([update, receipt]);
  } catch (e) {
    const existing = await resolveReceipt(db, worker, operation, idempotencyKey, requestHash);
    if (existing) return existing as MutationSuccess;
    throw mapDbError(e);
  }

  const committed = await resolveReceipt(db, worker, operation, idempotencyKey, requestHash);
  if (committed) return committed as MutationSuccess;
  throw new ApiError("STALE_LEASE", "Heartbeat rejected; lease no longer valid.");
}

// ---------------------------------------------------------------------------
// Reconcile expired leases (POST /worker/reconcile) — explicit, authenticated.
// Moves active jobs whose lease expired to RecoveryRequired. GETs never do this.
// ---------------------------------------------------------------------------

export interface ReconcileArgs {
  db: D1Database;
  now: number;
  actor: Actor;
}

export async function reconcileExpiredLeases(
  args: ReconcileArgs,
): Promise<{ contractVersion: string; reconciled: string[] }> {
  const { db, now, actor } = args;

  const expired = await db
    .prepare(
      `SELECT id, state, state_version, execution_generation
         FROM jobs
        WHERE state IN ('Claimed','Running')
          AND lease_expires_at IS NOT NULL
          AND lease_expires_at <= ?`,
    )
    .bind(now)
    .all<Pick<JobRow, "id" | "state" | "state_version" | "execution_generation">>();

  const reconciled: string[] = [];
  for (const row of expired.results) {
    const mutationId = newId();
    const update = db
      .prepare(
        `UPDATE jobs SET state = 'RecoveryRequired', recovery_mode = NULL,
                state_version = state_version + 1, last_mutation_id = ?, updated_at = ?
          WHERE id = ? AND state = ? AND state_version = ? AND lease_expires_at <= ?`,
      )
      .bind(mutationId, now, row.id, row.state, row.state_version, now);
    const event = eventStmt(db, {
      jobId: row.id,
      fromState: row.state,
      toState: "RecoveryRequired",
      eventType: "lease_expired",
      actor,
      mutationId,
      details: { reason: "lease_expired" },
      now,
    });
    await db.batch([update, event]);
    const moved = await db
      .prepare("SELECT 1 FROM jobs WHERE id = ? AND last_mutation_id = ?")
      .bind(row.id, mutationId)
      .first();
    if (moved) reconciled.push(row.id);
  }
  return { contractVersion: CONTRACT_VERSION, reconciled };
}

// ---------------------------------------------------------------------------
// Error mapping — never leak raw SQL text.
// ---------------------------------------------------------------------------

function mapDbError(e: unknown): ApiError {
  const msg = e instanceof Error ? e.message : String(e);
  if (/UNIQUE constraint/i.test(msg)) {
    return new ApiError("STATE_CONFLICT", "A conflicting record already exists.");
  }
  return new ApiError("CLOUD_PERSISTENCE_FAILURE", "A database operation failed.");
}
