import { env } from "cloudflare:test";
import { requestHash } from "../src/domain/ids.js";
import { validateJobCreation } from "../src/domain/validation.js";
import { admitJob } from "../src/data/mutations.js";
import type { Actor, MutationSuccess } from "../src/data/types.js";

export const OWNER: Actor = { type: "owner", id: "github:12345" };
export const WORKER: Actor = { type: "worker", id: "worker:alpha" };
export const SYSTEM: Actor = { type: "system", id: "reconciler" };

export const AUTHORIZED_JOB_TYPES = ["research.investigation.v1"] as const;

export function creationBody(overrides: Record<string, unknown> = {}) {
  return {
    contractVersion: "1.0",
    jobType: "research.investigation.v1",
    request: { question: "Compare SQL Server and PostgreSQL", evidencePolicy: "hybrid-governed" },
    executionProfile: "standard",
    priority: "normal",
    visibility: "private",
    ...overrides,
  };
}

/** Admit a job through the real validation + admission adapter. */
export async function admit(opts: {
  owner?: Actor;
  body?: Record<string, unknown>;
  key?: string;
  now?: number;
} = {}): Promise<MutationSuccess> {
  const owner = opts.owner ?? OWNER;
  const body = opts.body ?? creationBody();
  const key = opts.key ?? crypto.randomUUID();
  const now = opts.now ?? Date.now();
  const creation = validateJobCreation(body);
  const hash = await requestHash(body);
  return admitJob({ db: env.DB, now, owner, idempotencyKey: key, requestHash: hash, creation });
}

export async function getJob(jobId: string) {
  return env.DB.prepare("SELECT * FROM jobs WHERE id = ?").bind(jobId).first<Record<string, unknown>>();
}

export async function getEvents(jobId: string) {
  const { results } = await env.DB
    .prepare("SELECT * FROM job_events WHERE job_id = ? ORDER BY sequence")
    .bind(jobId)
    .all<Record<string, unknown>>();
  return results;
}

export async function countReceipts(): Promise<number> {
  const row = await env.DB.prepare("SELECT COUNT(*) AS n FROM mutation_receipts").first<{ n: number }>();
  return row?.n ?? 0;
}

export function hashOf(body: unknown): Promise<string> {
  return requestHash(body);
}

export interface LeaseHandle {
  jobId: string;
  workerId: string;
  leaseId: string;
  generation: number;
  stateVersion: number;
}

/** Claim the single queued job as WORKER and return its lease handle. */
export async function claimOne(now = Date.now(), worker: Actor = WORKER): Promise<LeaseHandle> {
  const { claimNext } = await import("../src/data/mutations.js");
  const res = await claimNext({
    db: env.DB,
    now,
    worker,
    idempotencyKey: crypto.randomUUID(),
    requestHash: await requestHash({ op: "claim" }),
    leaseSeconds: 180,
    authorizedJobTypes: AUTHORIZED_JOB_TYPES,
  });
  if (!res.claimed) throw new Error("expected a claim");
  const r = res.response as unknown as {
    jobId: string;
    leaseId: string;
    executionGeneration: number;
    stateVersion: number;
    workerId: string;
  };
  return {
    jobId: r.jobId,
    workerId: r.workerId,
    leaseId: r.leaseId,
    generation: r.executionGeneration,
    stateVersion: r.stateVersion,
  };
}

/** Admit → claim → start, returning a running lease handle. */
export async function claimAndStart(now = Date.now()): Promise<LeaseHandle> {
  const { transition } = await import("../src/data/mutations.js");
  await admit({ key: crypto.randomUUID(), now });
  const h = await claimOne(now);
  const res = await transition({
    db: env.DB,
    now,
    actor: WORKER,
    operation: "worker.start",
    idempotencyKey: crypto.randomUUID(),
    requestHash: await requestHash({ op: "start", jobId: h.jobId }),
    jobId: h.jobId,
    op: "start",
    eventType: "started",
    lease: { workerId: h.workerId, leaseId: h.leaseId, generation: h.generation },
  });
  return { ...h, stateVersion: res.stateVersion };
}
