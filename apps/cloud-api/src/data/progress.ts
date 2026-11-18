import { ApiError } from "../domain/errors.js";
import { newId } from "../domain/ids.js";
import type { Actor, JobRow } from "./types.js";

// Bounds — a progress post is best-effort diagnostics, never allowed to be unbounded.
const MAX_ENTRIES_PER_CALL = 50;
const MAX_MESSAGE_CHARS = 2000;
const MAX_DETAIL_CHARS = 8 * 1024;
const MAX_STAGE_CHARS = 40;

export interface ProgressEntryInput {
  stage: string;
  message: string;
  level: "info" | "detail";
  detail: unknown;
}

export interface ProgressView {
  sequence: number;
  executionGeneration: number;
  stage: string;
  level: string;
  message: string;
  detail: unknown;
  at: string;
}

/** Parse + bound the worker-supplied progress entries. Rejects a malformed payload. */
export function parseProgressEntries(raw: unknown): ProgressEntryInput[] {
  if (!Array.isArray(raw)) {
    throw new ApiError("INVALID_SCHEMA", "entries must be an array.");
  }
  if (raw.length > MAX_ENTRIES_PER_CALL) {
    throw new ApiError("INVALID_SCHEMA", `At most ${MAX_ENTRIES_PER_CALL} progress entries per call.`);
  }
  return raw.map((item) => {
    const e = (item ?? {}) as Record<string, unknown>;
    const stage = typeof e.stage === "string" ? e.stage.slice(0, MAX_STAGE_CHARS) : "";
    if (stage.length === 0) throw new ApiError("INVALID_SCHEMA", "Each progress entry needs a non-empty stage.");
    const message = (typeof e.message === "string" ? e.message : "").slice(0, MAX_MESSAGE_CHARS);
    const level = e.level === "detail" ? "detail" : "info";
    let detail: unknown = e.detail ?? {};
    // Bound the serialized detail; drop it rather than reject the whole post if oversized.
    if (JSON.stringify(detail).length > MAX_DETAIL_CHARS) detail = { truncated: true };
    return { stage, message, level, detail };
  });
}

/**
 * Append verbose progress for a job. Lease-fenced: only the job's current lease holder (the
 * worker executing this generation) may append, so a stale/evicted worker cannot write.
 * Best-effort from the worker's side — it ignores failures — so this never disrupts a run.
 */
export async function appendProgress(args: {
  db: D1Database;
  now: number;
  worker: Actor;
  jobId: string;
  leaseId: string;
  generation: number;
  entries: ProgressEntryInput[];
}): Promise<{ appended: number }> {
  const { db, now, worker, jobId, leaseId, generation, entries } = args;

  const job = await db
    .prepare("SELECT id, worker_id, lease_id, execution_generation FROM jobs WHERE id = ?")
    .bind(jobId)
    .first<Pick<JobRow, "id" | "worker_id" | "lease_id" | "execution_generation">>();
  if (!job) throw new ApiError("NOT_FOUND", "Job not found.");
  if (job.worker_id !== worker.id || job.lease_id !== leaseId || job.execution_generation !== generation) {
    throw new ApiError("STATE_CONFLICT", "Stale lease; progress not recorded.");
  }
  if (entries.length === 0) return { appended: 0 };

  const base = await db
    .prepare("SELECT COALESCE(MAX(sequence), 0) AS m FROM job_progress WHERE job_id = ?")
    .bind(jobId)
    .first<{ m: number }>();
  const start = base?.m ?? 0;

  const stmts = entries.map((e, i) =>
    db
      .prepare(
        `INSERT INTO job_progress
           (progress_id, job_id, sequence, execution_generation, stage, level, message, detail_json, created_at)
         VALUES (?,?,?,?,?,?,?,?,?)`,
      )
      .bind(newId(), jobId, start + i + 1, generation, e.stage, e.level, e.message, JSON.stringify(e.detail), now),
  );
  await db.batch(stmts);
  return { appended: stmts.length };
}

/** Owner-scoped read of a job's full progress stream (ordered). null if not the owner's job. */
export async function getOwnerJobProgress(
  db: D1Database,
  ownerId: string,
  jobId: string,
): Promise<ProgressView[] | null> {
  const owns = await db.prepare("SELECT 1 FROM jobs WHERE id = ? AND owner_id = ?").bind(jobId, ownerId).first();
  if (!owns) return null;
  const { results } = await db
    .prepare(
      "SELECT sequence, execution_generation, stage, level, message, detail_json, created_at FROM job_progress WHERE job_id = ? ORDER BY sequence",
    )
    .bind(jobId)
    .all<{
      sequence: number;
      execution_generation: number;
      stage: string;
      level: string;
      message: string;
      detail_json: string;
      created_at: number;
    }>();
  return results.map((r) => ({
    sequence: r.sequence,
    executionGeneration: r.execution_generation,
    stage: r.stage,
    level: r.level,
    message: r.message,
    detail: safeParse(r.detail_json),
    at: new Date(r.created_at).toISOString(),
  }));
}

function safeParse(json: string): unknown {
  try {
    return JSON.parse(json);
  } catch {
    return {};
  }
}
