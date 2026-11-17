import { ApiError } from "../domain/errors.js";
import { newId } from "../domain/ids.js";
import { isTerminal, type Priority, type RecoveryMode } from "../domain/states.js";
import { resolveReceipt } from "./receipts.js";
import { transition } from "./mutations.js";
import { CONTRACT_VERSION, type Actor, type JobRow, type MutationSuccess } from "./types.js";

async function loadJob(db: D1Database, jobId: string): Promise<JobRow | null> {
  return db.prepare("SELECT * FROM jobs WHERE id = ?").bind(jobId).first<JobRow>();
}

/**
 * Owner flag mutation on a Queued job (hold / release / priority change).
 * Not a state transition — updates an orthogonal field with an audit event + receipt.
 */
async function flagMutation(args: {
  db: D1Database;
  now: number;
  owner: Actor;
  idempotencyKey: string;
  requestHash: string;
  jobId: string;
  operation: string;
  eventType: string;
  setColumn: string;
  setValue: string | number;
  details: Record<string, unknown>;
}): Promise<MutationSuccess> {
  const { db, now, owner, idempotencyKey, requestHash, jobId, operation, eventType } = args;

  const replay = await resolveReceipt(db, owner, operation, idempotencyKey, requestHash);
  if (replay) return replay as MutationSuccess;

  const job = await loadJob(db, jobId);
  if (!job) throw new ApiError("NOT_FOUND", "Job not found.");
  if (job.owner_id !== owner.id) throw new ApiError("UNAUTHORIZED", "Not the owner of this job.");
  if (job.state !== "Queued") {
    throw new ApiError("STATE_CONFLICT", `Only Queued jobs can be modified; job is ${job.state}.`);
  }

  const mutationId = newId();
  const newVersion = job.state_version + 1;
  const response = {
    contractVersion: CONTRACT_VERSION,
    operationId: newId(),
    jobId,
    stateVersion: newVersion,
    executionGeneration: job.execution_generation,
    acceptedAt: new Date(now).toISOString(),
    outcome: "accepted" as const,
    state: job.state,
  };

  const update = db
    .prepare(
      `UPDATE jobs SET ${args.setColumn} = ?, state_version = state_version + 1,
              last_mutation_id = ?, updated_at = ?
        WHERE id = ? AND state = 'Queued' AND state_version = ? AND owner_id = ?`,
    )
    .bind(args.setValue, mutationId, now, jobId, job.state_version, owner.id);

  const event = db
    .prepare(
      `INSERT INTO job_events
         (event_id, job_id, sequence, from_state, to_state, event_type, actor_type, actor_id,
          execution_generation, mutation_id, details_json, created_at)
       SELECT ?, j.id,
              COALESCE((SELECT MAX(sequence) FROM job_events WHERE job_id = j.id), 0) + 1,
              'Queued', 'Queued', ?, 'owner', ?, j.execution_generation, ?, ?, ?
         FROM jobs j WHERE j.id = ? AND j.last_mutation_id = ?`,
    )
    .bind(newId(), eventType, owner.id, mutationId, JSON.stringify(args.details), now, jobId, mutationId);

  const receipt = db
    .prepare(
      `INSERT INTO mutation_receipts
         (receipt_id, actor_id, operation, idempotency_key, request_hash, mutation_id, job_id, response_json, accepted_at)
       SELECT ?,?,?,?,?,?,?,?,?
        WHERE EXISTS (SELECT 1 FROM jobs WHERE id = ? AND last_mutation_id = ?)`,
    )
    .bind(newId(), owner.id, operation, idempotencyKey, requestHash, mutationId, jobId,
      JSON.stringify(response), now, jobId, mutationId);

  await db.batch([update, event, receipt]);

  const committed = await resolveReceipt(db, owner, operation, idempotencyKey, requestHash);
  if (committed) return committed as MutationSuccess;
  throw new ApiError("STATE_CONFLICT", "The job state changed; the flag was not applied.");
}

export function setHold(a: Omit<Parameters<typeof flagMutation>[0],
  "operation" | "eventType" | "setColumn" | "setValue" | "details"> & { hold: boolean }): Promise<MutationSuccess> {
  return flagMutation({
    ...a,
    operation: "owner.hold",
    eventType: a.hold ? "hold_requested" : "hold_released",
    setColumn: "hold_requested",
    setValue: a.hold ? 1 : 0,
    details: { hold: a.hold },
  });
}

export function setPriority(a: Omit<Parameters<typeof flagMutation>[0],
  "operation" | "eventType" | "setColumn" | "setValue" | "details"> & { priority: Priority }): Promise<MutationSuccess> {
  return flagMutation({
    ...a,
    operation: "owner.priority",
    eventType: "priority_changed",
    setColumn: "priority",
    setValue: a.priority,
    details: { priority: a.priority },
  });
}

/**
 * Owner cancellation. A Queued job is cancelled immediately. An active
 * (Claimed/Running) job records cancel_requested=1; the worker later acknowledges.
 * Pressing Cancel never reports completion.
 */
export async function requestCancel(args: {
  db: D1Database;
  now: number;
  owner: Actor;
  idempotencyKey: string;
  requestHash: string;
  jobId: string;
}): Promise<MutationSuccess> {
  const { db, now, owner, idempotencyKey, requestHash, jobId } = args;
  const job = await loadJob(db, jobId);
  if (!job) throw new ApiError("NOT_FOUND", "Job not found.");
  if (job.owner_id !== owner.id) throw new ApiError("UNAUTHORIZED", "Not the owner of this job.");

  if (job.state === "Queued") {
    return transition({
      db, now, actor: owner, operation: "owner.cancel", idempotencyKey, requestHash,
      jobId, op: "cancel_queued", eventType: "cancelled_by_owner", ownerId: owner.id,
      details: { reason: "owner_cancel" },
    });
  }
  if (job.state === "Claimed" || job.state === "Running") {
    return flagMutation({
      db, now, owner, idempotencyKey, requestHash, jobId,
      operation: "owner.cancel",
      eventType: "cancel_requested",
      setColumn: "cancel_requested",
      setValue: 1,
      details: { reason: "owner_cancel" },
    }).catch((e) => {
      // flagMutation requires Queued; active jobs set the flag via a dedicated path.
      if (e instanceof ApiError && e.code === "STATE_CONFLICT") {
        return setCancelFlagActive({ db, now, owner, idempotencyKey, requestHash, jobId });
      }
      throw e;
    });
  }
  if (isTerminal(job.state)) {
    throw new ApiError("STATE_CONFLICT", `Job already terminal (${job.state}).`);
  }
  throw new ApiError("STATE_CONFLICT", `Cannot cancel in state ${job.state}.`);
}

/** Sets cancel_requested on an active job without changing its primary state. */
async function setCancelFlagActive(args: {
  db: D1Database;
  now: number;
  owner: Actor;
  idempotencyKey: string;
  requestHash: string;
  jobId: string;
}): Promise<MutationSuccess> {
  const { db, now, owner, idempotencyKey, requestHash, jobId } = args;
  const operation = "owner.cancel";

  const replay = await resolveReceipt(db, owner, operation, idempotencyKey, requestHash);
  if (replay) return replay as MutationSuccess;

  const job = await loadJob(db, jobId);
  if (!job) throw new ApiError("NOT_FOUND", "Job not found.");
  const mutationId = newId();
  const newVersion = job.state_version + 1;
  const response: MutationSuccess = {
    contractVersion: CONTRACT_VERSION,
    operationId: newId(),
    jobId,
    stateVersion: newVersion,
    executionGeneration: job.execution_generation,
    acceptedAt: new Date(now).toISOString(),
    outcome: "accepted",
    state: job.state,
    cancelRequested: true,
  };

  const update = db
    .prepare(
      `UPDATE jobs SET cancel_requested = 1, state_version = state_version + 1,
              last_mutation_id = ?, updated_at = ?
        WHERE id = ? AND state IN ('Claimed','Running') AND state_version = ? AND owner_id = ?`,
    )
    .bind(mutationId, now, jobId, job.state_version, owner.id);
  const event = db
    .prepare(
      `INSERT INTO job_events
         (event_id, job_id, sequence, from_state, to_state, event_type, actor_type, actor_id,
          execution_generation, mutation_id, details_json, created_at)
       SELECT ?, j.id,
              COALESCE((SELECT MAX(sequence) FROM job_events WHERE job_id = j.id), 0) + 1,
              j.state, j.state, 'cancel_requested', 'owner', ?, j.execution_generation, ?, ?, ?
         FROM jobs j WHERE j.id = ? AND j.last_mutation_id = ?`,
    )
    .bind(newId(), owner.id, mutationId, JSON.stringify({ reason: "owner_cancel" }), now, jobId, mutationId);
  const receipt = db
    .prepare(
      `INSERT INTO mutation_receipts
         (receipt_id, actor_id, operation, idempotency_key, request_hash, mutation_id, job_id, response_json, accepted_at)
       SELECT ?,?,?,?,?,?,?,?,?
        WHERE EXISTS (SELECT 1 FROM jobs WHERE id = ? AND last_mutation_id = ?)`,
    )
    .bind(newId(), owner.id, operation, idempotencyKey, requestHash, mutationId, jobId,
      JSON.stringify(response), now, jobId, mutationId);

  await db.batch([update, event, receipt]);
  const committed = await resolveReceipt(db, owner, operation, idempotencyKey, requestHash);
  if (committed) return committed as MutationSuccess;
  throw new ApiError("STATE_CONFLICT", "The job changed state; cancel flag not applied.");
}

/**
 * Owner-authorized recovery decision (resume / restart / terminate).
 * Recovery never resets consumed budget. Resume requires a locally-validated
 * checkpoint digest (re-validated by the worker before execution).
 */
export async function authorizeRecovery(args: {
  db: D1Database;
  now: number;
  owner: Actor;
  idempotencyKey: string;
  requestHash: string;
  jobId: string;
  expectedStateVersion: number;
  mode: RecoveryMode;
  checkpointDigest?: string;
}): Promise<MutationSuccess> {
  const { db, now, owner, idempotencyKey, requestHash, jobId, mode, checkpointDigest } = args;
  const job = await loadJob(db, jobId);
  if (!job) throw new ApiError("NOT_FOUND", "Job not found.");
  if (job.owner_id !== owner.id) throw new ApiError("UNAUTHORIZED", "Not the owner of this job.");
  if (job.state !== "RecoveryRequired") {
    throw new ApiError("STATE_CONFLICT", `Recovery only valid from RecoveryRequired; job is ${job.state}.`);
  }
  if (job.state_version !== args.expectedStateVersion) {
    throw new ApiError("VERSION_CONFLICT", "Stale state version for recovery decision.");
  }
  if (mode === "resume" && !checkpointDigest) {
    throw new ApiError("INVALID_SCHEMA", "Resume requires a validated checkpoint digest.");
  }

  if (mode === "resume" || mode === "restart") {
    // Requeue with a NEW execution generation (fencing). Budget/history preserved.
    return transition({
      db, now, actor: owner, operation: "owner.recovery", idempotencyKey, requestHash,
      jobId, op: "recovery_requeue", eventType: `recovery_${mode}`, ownerId: owner.id,
      bumpGeneration: true, clearLease: true,
      extraSet: {
        recovery_mode: mode,
        approved_checkpoint_hash: mode === "resume" ? (checkpointDigest ?? null) : null,
      },
      details: { mode, checkpointDigest: checkpointDigest ?? null },
    });
  }

  // terminate → Failed (preserve records).
  return transition({
    db, now, actor: owner, operation: "owner.recovery", idempotencyKey, requestHash,
    jobId, op: "recovery_fail", eventType: "recovery_terminated", ownerId: owner.id,
    clearLease: true, extraSet: { recovery_mode: "terminate" },
    details: { mode: "terminate" },
  });
}
