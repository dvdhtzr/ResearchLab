import type { JobRow } from "./types.js";

export interface OwnerJobView {
  jobId: string;
  jobType: string;
  state: string;
  stateVersion: number;
  executionGeneration: number;
  priority: string;
  executionProfile: string;
  visibility: string;
  holdRequested: boolean;
  cancelRequested: boolean;
  researchDisposition: string | null;
  publicationStatus: string;
  currentStage: string | null;
  leaseExpiresAt: string | null;
  createdAt: string;
  updatedAt: string;
}

function toOwnerView(r: JobRow): OwnerJobView {
  return {
    jobId: r.id,
    jobType: r.job_type,
    state: r.state,
    stateVersion: r.state_version,
    executionGeneration: r.execution_generation,
    priority: r.priority,
    executionProfile: r.execution_profile,
    visibility: r.visibility,
    holdRequested: !!r.hold_requested,
    cancelRequested: !!r.cancel_requested,
    researchDisposition: r.research_disposition,
    publicationStatus: r.publication_status,
    currentStage: r.current_stage,
    leaseExpiresAt: r.lease_expires_at ? new Date(r.lease_expires_at).toISOString() : null,
    createdAt: new Date(r.created_at).toISOString(),
    updatedAt: new Date(r.updated_at).toISOString(),
  };
}

export async function listOwnerJobs(db: D1Database, ownerId: string): Promise<OwnerJobView[]> {
  const { results } = await db
    .prepare("SELECT * FROM jobs WHERE owner_id = ? ORDER BY created_at DESC LIMIT 100")
    .bind(ownerId)
    .all<JobRow>();
  return results.map(toOwnerView);
}

export async function getOwnerJob(
  db: D1Database,
  ownerId: string,
  jobId: string,
): Promise<OwnerJobView | null> {
  const row = await db
    .prepare("SELECT * FROM jobs WHERE id = ? AND owner_id = ?")
    .bind(jobId, ownerId)
    .first<JobRow>();
  return row ? toOwnerView(row) : null;
}

export interface JobEventView {
  sequence: number;
  fromState: string | null;
  toState: string;
  eventType: string;
  actorType: string;
  executionGeneration: number;
  at: string;
  details: unknown;
}

export async function getOwnerJobEvents(
  db: D1Database,
  ownerId: string,
  jobId: string,
): Promise<JobEventView[] | null> {
  const owns = await db.prepare("SELECT 1 FROM jobs WHERE id = ? AND owner_id = ?").bind(jobId, ownerId).first();
  if (!owns) return null;
  const { results } = await db
    .prepare("SELECT * FROM job_events WHERE job_id = ? ORDER BY sequence")
    .bind(jobId)
    .all<Record<string, unknown>>();
  return results.map((e) => ({
    sequence: e.sequence as number,
    fromState: (e.from_state as string) ?? null,
    toState: e.to_state as string,
    eventType: e.event_type as string,
    actorType: e.actor_type as string,
    executionGeneration: e.execution_generation as number,
    at: new Date(e.created_at as number).toISOString(),
    details: JSON.parse(e.details_json as string),
  }));
}

// -------------------------------------------------------------------- public
// Strictly public projections. No worker ids, lease details, prompts, budgets,
// private titles, or raw agent/LLM output. Public events use server templates.

const PUBLIC_EVENT_TEMPLATES: Record<string, string> = {
  admitted: "Investigation accepted into the queue.",
  claimed: "A worker began the investigation.",
  started: "Execution started.",
  completed: "Investigation completed.",
  failed: "Investigation ended with a failure.",
  cancelled_by_owner: "Investigation was cancelled.",
  lease_expired: "Execution was interrupted; awaiting recovery.",
  recovery_restart: "Execution was restarted.",
  recovery_resume: "Execution resumed from a checkpoint.",
  recovery_terminated: "Investigation was terminated during recovery.",
};

function isPublic(r: Pick<JobRow, "visibility">): boolean {
  return r.visibility === "public_demo";
}

export interface PublicJobView {
  jobId: string;
  state: string;
  stage: string | null;
  disposition: string | null;
  updatedAt: string;
}

export async function listPublicJobs(db: D1Database, page: number, pageSize = 25): Promise<PublicJobView[]> {
  const offset = (Math.max(1, page) - 1) * pageSize;
  const { results } = await db
    .prepare(
      "SELECT id, state, current_stage, research_disposition, updated_at FROM jobs WHERE visibility = 'public_demo' ORDER BY updated_at DESC LIMIT ? OFFSET ?",
    )
    .bind(pageSize, offset)
    .all<Pick<JobRow, "id" | "state" | "current_stage" | "research_disposition" | "updated_at">>();
  return results.map((r) => ({
    jobId: r.id,
    state: r.state,
    stage: r.current_stage,
    disposition: r.research_disposition,
    updatedAt: new Date(r.updated_at).toISOString(),
  }));
}

export async function getPublicJob(db: D1Database, jobId: string): Promise<PublicJobView | null> {
  const r = await db
    .prepare("SELECT id, state, current_stage, research_disposition, updated_at, visibility FROM jobs WHERE id = ?")
    .bind(jobId)
    .first<Pick<JobRow, "id" | "state" | "current_stage" | "research_disposition" | "updated_at" | "visibility">>();
  if (!r || !isPublic(r)) return null;
  return {
    jobId: r.id,
    state: r.state,
    stage: r.current_stage,
    disposition: r.research_disposition,
    updatedAt: new Date(r.updated_at).toISOString(),
  };
}

export interface PublicEventView {
  sequence: number;
  templateId: string;
  message: string;
  at: string;
}

export async function getPublicJobEvents(
  db: D1Database,
  jobId: string,
  limit = 50,
): Promise<PublicEventView[] | null> {
  const r = await db.prepare("SELECT visibility FROM jobs WHERE id = ?").bind(jobId).first<Pick<JobRow, "visibility">>();
  if (!r || !isPublic(r)) return null;
  const { results } = await db
    .prepare("SELECT sequence, event_type, created_at FROM job_events WHERE job_id = ? ORDER BY sequence LIMIT ?")
    .bind(jobId, Math.min(limit, 50))
    .all<{ sequence: number; event_type: string; created_at: number }>();
  return results
    .filter((e) => e.event_type in PUBLIC_EVENT_TEMPLATES)
    .map((e) => ({
      sequence: e.sequence,
      templateId: e.event_type,
      message: PUBLIC_EVENT_TEMPLATES[e.event_type]!,
      at: new Date(e.created_at).toISOString(),
    }));
}
