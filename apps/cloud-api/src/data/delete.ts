import { ApiError } from "../domain/errors.js";
import type { Actor, JobRow } from "./types.js";

export interface DeleteJobResult {
  deleted: true;
  jobId: string;
}

/**
 * Owner-authorized hard delete of a research job, in ANY state (the owner's explicit
 * choice). Cascades every record tied to the job — publication artifacts, approvals and
 * published reports (so the public showcase never dangles), the audit events, the verbose
 * progress stream, and idempotency receipts — then the job row itself, in one atomic batch.
 * Still strictly owner-scoped: an owner can only delete their own job.
 */
export async function deleteOwnerJob(args: {
  db: D1Database;
  owner: Actor;
  jobId: string;
}): Promise<DeleteJobResult> {
  const { db, owner, jobId } = args;

  const job = await db
    .prepare("SELECT id, owner_id FROM jobs WHERE id = ?")
    .bind(jobId)
    .first<Pick<JobRow, "id" | "owner_id">>();
  if (!job) throw new ApiError("NOT_FOUND", "Job not found.");
  if (job.owner_id !== owner.id) throw new ApiError("UNAUTHORIZED", "Not the owner of this job.");

  // Children before parents (foreign-key safe), all in one atomic batch.
  await db.batch([
    db.prepare("DELETE FROM published_reports WHERE job_id = ?").bind(jobId),
    db
      .prepare(
        "DELETE FROM publication_approvals WHERE artifact_id IN (SELECT artifact_id FROM artifact_manifests WHERE job_id = ?)",
      )
      .bind(jobId),
    db.prepare("DELETE FROM artifact_manifests WHERE job_id = ?").bind(jobId),
    db.prepare("DELETE FROM job_progress WHERE job_id = ?").bind(jobId),
    db.prepare("DELETE FROM job_events WHERE job_id = ?").bind(jobId),
    db.prepare("DELETE FROM mutation_receipts WHERE job_id = ?").bind(jobId),
    db.prepare("DELETE FROM jobs WHERE id = ? AND owner_id = ?").bind(jobId, owner.id),
  ]);

  return { deleted: true, jobId };
}
