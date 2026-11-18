import { ApiError } from "../domain/errors.js";
import { requestHash } from "../domain/ids.js";
import { validateJobCreation } from "../domain/validation.js";
import { REGISTERED_JOB_TYPES, type Priority, type RecoveryMode } from "../domain/states.js";
import {
  admitJob,
  claimNext,
  heartbeat,
  reconcileExpiredLeases,
  transition,
} from "../data/mutations.js";
import { authorizeRecovery, requestCancel, setHold, setPriority } from "../data/control.js";
import {
  approveDigest,
  getPublishedReport,
  listPublications,
  publishContent,
  registerManifest,
  revokeApproval,
  withdrawReport,
} from "../data/publication.js";
import {
  getOwnerJob,
  getOwnerJobEvents,
  getPublicJob,
  getPublicJobEvents,
  listOwnerJobs,
  listPublicJobs,
} from "../data/jobs-repo.js";
import { deleteOwnerJob } from "../data/delete.js";
import { appendProgress, getOwnerJobProgress, parseProgressEntries } from "../data/progress.js";
import { CONTRACT_VERSION, type Actor } from "../data/types.js";
import type { Env } from "../index.js";
import {
  assertProductionAuthSafe,
  resolveOwner,
  resolveWorker,
} from "./auth.js";
import { errorResponse, json, readJsonBody, requireIdempotencyKey } from "./http.js";

const PREFIX = "/api/v1";

interface Lease {
  leaseId: string;
  generation: number;
}

function leaseFromBody(actor: Actor, body: Record<string, unknown>): { workerId: string; leaseId: string; generation: number } {
  const leaseId = body.leaseId;
  const generation = body.generation;
  if (typeof leaseId !== "string" || typeof generation !== "number") {
    throw new ApiError("INVALID_SCHEMA", "leaseId and generation are required.");
  }
  return { workerId: actor.id, leaseId, generation };
}

export async function handle(request: Request, env: Env): Promise<Response> {
  try {
    assertProductionAuthSafe(env);
    const url = new URL(request.url);
    if (!url.pathname.startsWith(PREFIX)) {
      throw new ApiError("NOT_FOUND", "Unknown route.");
    }
    const path = url.pathname.slice(PREFIX.length);
    const method = request.method.toUpperCase();
    const seg = path.split("/").filter(Boolean); // e.g. ["owner","jobs","{id}","control"]
    const db = env.DB;
    const now = Date.now();
    const leaseSeconds = Number(env.RMC_LEASE_SECONDS ?? "180");

    // ------------------------------------------------------------- public
    if (seg[0] === "public") {
      if (method !== "GET") throw new ApiError("NOT_FOUND", "Unknown route.");
      if (seg[1] === "jobs" && seg.length === 2) {
        const page = Number(url.searchParams.get("page") ?? "1");
        return json({ items: await listPublicJobs(db, page), page });
      }
      if (seg[1] === "jobs" && seg.length === 3) {
        const j = await getPublicJob(db, seg[2]!);
        if (!j) throw new ApiError("NOT_FOUND", "Not found.");
        return json(j);
      }
      if (seg[1] === "jobs" && seg.length === 4 && seg[3] === "events") {
        const ev = await getPublicJobEvents(db, seg[2]!);
        if (ev === null) throw new ApiError("NOT_FOUND", "Not found.");
        return json({ items: ev });
      }
      if (seg[1] === "reports" && seg.length === 3) {
        const report = await getPublishedReport(db, seg[2]!);
        if (!report) throw new ApiError("NOT_FOUND", "Not found.");
        return json(report);
      }
      throw new ApiError("NOT_FOUND", "Unknown route.");
    }

    // ------------------------------------------------------------- owner
    if (seg[0] === "owner") {
      const owner = resolveOwner(request, env);

      if (seg[1] === "session" && method === "GET") {
        return json({ contractVersion: CONTRACT_VERSION, ownerId: owner.id, authenticated: true });
      }
      if (seg[1] === "models" && method === "GET") {
        return json({ contractVersion: CONTRACT_VERSION, models: [{ id: "qwen3.5:9b", approved: true, installed: null }] });
      }
      if (seg[1] === "jobs" && seg.length === 2 && method === "GET") {
        return json({ items: await listOwnerJobs(db, owner.id) });
      }
      if (seg[1] === "jobs" && seg.length === 2 && method === "POST") {
        const key = requireIdempotencyKey(request);
        const body = (await readJsonBody(request)) as Record<string, unknown>;
        const creation = validateJobCreation(body);
        const hash = await requestHash({ path, body });
        const res = await admitJob({ db, now, owner, idempotencyKey: key, requestHash: hash, creation });
        return json(res, 201);
      }
      if (seg[1] === "jobs" && seg.length === 3 && method === "GET") {
        const j = await getOwnerJob(db, owner.id, seg[2]!);
        if (!j) throw new ApiError("NOT_FOUND", "Not found.");
        return json(j);
      }
      if (seg[1] === "jobs" && seg.length === 3 && method === "DELETE") {
        return json(await deleteOwnerJob({ db, owner, jobId: seg[2]! }));
      }
      if (seg[1] === "jobs" && seg.length === 4 && seg[3] === "events" && method === "GET") {
        const ev = await getOwnerJobEvents(db, owner.id, seg[2]!);
        if (ev === null) throw new ApiError("NOT_FOUND", "Not found.");
        return json({ items: ev });
      }
      if (seg[1] === "jobs" && seg.length === 4 && seg[3] === "progress" && method === "GET") {
        const pv = await getOwnerJobProgress(db, owner.id, seg[2]!);
        if (pv === null) throw new ApiError("NOT_FOUND", "Not found.");
        return json({ items: pv });
      }
      if (seg[1] === "jobs" && seg.length === 4 && seg[3] === "control" && method === "POST") {
        const key = requireIdempotencyKey(request);
        const jobId = seg[2]!;
        const body = (await readJsonBody(request)) as Record<string, unknown>;
        const hash = await requestHash({ path, body });
        const base = { db, now, owner, idempotencyKey: key, requestHash: hash, jobId };
        switch (body.action) {
          case "hold":
            return json(await setHold({ ...base, hold: true }));
          case "release":
            return json(await setHold({ ...base, hold: false }));
          case "set_priority":
            return json(await setPriority({ ...base, priority: requireEnum(body.priority, ["low", "normal", "high"]) as Priority }));
          case "request_cancel":
            return json(await requestCancel(base));
          default:
            throw new ApiError("INVALID_SCHEMA", "Unknown control action.");
        }
      }
      if (seg[1] === "jobs" && seg.length === 4 && seg[3] === "recovery" && method === "POST") {
        const key = requireIdempotencyKey(request);
        const jobId = seg[2]!;
        const body = (await readJsonBody(request)) as Record<string, unknown>;
        const hash = await requestHash({ path, body });
        const mode = requireEnum(body.mode, ["resume", "restart", "terminate"]) as RecoveryMode;
        if (typeof body.expectedStateVersion !== "number") {
          throw new ApiError("INVALID_SCHEMA", "expectedStateVersion is required.");
        }
        return json(
          await authorizeRecovery({
            db, now, owner, idempotencyKey: key, requestHash: hash, jobId,
            expectedStateVersion: body.expectedStateVersion,
            mode,
            checkpointDigest: typeof body.checkpointDigest === "string" ? body.checkpointDigest : undefined,
          }),
        );
      }
      if (seg[1] === "publications" && seg.length === 2 && method === "GET") {
        return json({ items: await listPublications(db, owner.id) });
      }
      if (seg[1] === "jobs" && seg.length === 4 && seg[3] === "publication" && method === "POST") {
        requireIdempotencyKey(request);
        const jobId = seg[2]!;
        const body = (await readJsonBody(request)) as Record<string, unknown>;
        switch (body.action) {
          case "approve_digest":
            return json(await approveDigest({
              db, now, owner, jobId,
              artifactId: reqStr(body, "artifactId"),
              contentDigest: reqStr(body, "contentDigest"),
            }));
          case "revoke_approval":
            return json(await revokeApproval({
              db, now, owner, jobId,
              artifactId: reqStr(body, "artifactId"),
              contentDigest: reqStr(body, "contentDigest"),
            }));
          case "withdraw":
            return json(await withdrawReport({ db, now, owner, jobId, reportId: reqStr(body, "reportId") }));
          default:
            throw new ApiError("INVALID_SCHEMA", "Unknown publication action.");
        }
      }
      throw new ApiError("NOT_FOUND", "Unknown route.");
    }

    // ------------------------------------------------------------- worker
    if (seg[0] === "worker") {
      const worker = resolveWorker(request, env);

      if (seg[1] === "claims" && method === "POST") {
        const key = requireIdempotencyKey(request);
        const hash = await requestHash({ path, worker: worker.id });
        const res = await claimNext({
          db, now, worker, idempotencyKey: key, requestHash: hash,
          leaseSeconds, authorizedJobTypes: REGISTERED_JOB_TYPES,
        });
        return json(res.response);
      }
      if (seg[1] === "reconcile" && method === "POST") {
        return json(await reconcileExpiredLeases({ db, now, actor: worker }));
      }
      if (seg[1] === "artifacts" && seg[2] === "manifests" && method === "POST") {
        requireIdempotencyKey(request);
        const body = (await readJsonBody(request)) as Record<string, unknown>;
        return json(await registerManifest({
          db, now,
          jobId: reqStr(body, "jobId"),
          artifactVersion: typeof body.artifactVersion === "number" ? body.artifactVersion : 1,
          schemaVersion: typeof body.schemaVersion === "string" ? body.schemaVersion : "public-report.v1",
          contentDigest: reqStr(body, "contentDigest"),
          contentLength: typeof body.contentLength === "number" ? body.contentLength : 0,
          summary: body.summary,
        }), 201);
      }
      if (seg[1] === "artifacts" && seg[2] === "content" && method === "POST") {
        requireIdempotencyKey(request);
        // The body is the EXACT approved bytes; read raw text and hash those exact bytes.
        const jobId = url.searchParams.get("jobId") ?? "";
        if (!jobId) throw new ApiError("INVALID_SCHEMA", "jobId query parameter is required.");
        const rawBody = await request.text();
        return json(await publishContent({ db, now, jobId, rawBody }), 201);
      }
      if (seg[1] === "jobs" && seg.length === 4 && seg[3] === "progress" && method === "POST") {
        // Best-effort verbose progress (lease-fenced). Not a state transition, no idempotency key.
        const jobId = seg[2]!;
        const body = (await readJsonBody(request)) as Record<string, unknown>;
        const lease = leaseFromBody(worker, body);
        return json(await appendProgress({
          db, now, worker, jobId, leaseId: lease.leaseId, generation: lease.generation,
          entries: parseProgressEntries(body.entries),
        }));
      }
      if (seg[1] === "jobs" && seg.length === 4 && method === "POST") {
        const jobId = seg[2]!;
        const action = seg[3]!;
        const key = requireIdempotencyKey(request);
        const body = (await readJsonBody(request)) as Record<string, unknown>;
        const hash = await requestHash({ path, body });
        const lease = leaseFromBody(worker, body);

        switch (action) {
          case "start":
            return json(await transition({
              db, now, actor: worker, operation: "worker.start", idempotencyKey: key, requestHash: hash,
              jobId, op: "start", eventType: "started", lease,
              extraSet: { current_stage: "plan" },
            }));
          case "heartbeat":
            return json(await heartbeat({ db, now, worker, idempotencyKey: key, requestHash: hash, jobId, lease, leaseSeconds }));
          case "complete": {
            // Optional owner-visible research report (bounded). Stored in the completed
            // event's details — NOT in public projections (those are server-templated).
            const report = body.report;
            if (report !== undefined && (typeof report !== "object" || report === null || JSON.stringify(report).length > 64 * 1024)) {
              throw new ApiError("INVALID_SCHEMA", "report must be an object under 64 KiB.");
            }
            return json(await transition({
              db, now, actor: worker, operation: "worker.complete", idempotencyKey: key, requestHash: hash,
              jobId, op: "complete", eventType: "completed", lease, clearLease: true,
              extraSet: {
                research_disposition: typeof body.disposition === "string" ? body.disposition : "not_determined",
                current_stage: "done",
              },
              details: { disposition: body.disposition ?? "not_determined", report: report ?? null },
            }));
          }
          case "fail":
            return json(await transition({
              db, now, actor: worker, operation: "worker.fail", idempotencyKey: key, requestHash: hash,
              jobId, op: "exec_fail", eventType: "failed", lease, clearLease: true,
              details: { reason: typeof body.reason === "string" ? body.reason : "unspecified" },
            }));
          case "cancelled":
            return json(await transition({
              db, now, actor: worker, operation: "worker.cancelled", idempotencyKey: key, requestHash: hash,
              jobId, op: "cancel_acked", eventType: "cancelled_by_owner", lease, clearLease: true,
              details: { reason: "worker_acked_cancel" },
            }));
          default:
            throw new ApiError("NOT_FOUND", "Unknown worker action.");
        }
      }
      throw new ApiError("NOT_FOUND", "Unknown route.");
    }

    throw new ApiError("NOT_FOUND", "Unknown route.");
  } catch (e) {
    return errorResponse(e);
  }
}

function requireEnum<T extends string>(v: unknown, allowed: readonly T[]): T {
  if (typeof v !== "string" || !(allowed as readonly string[]).includes(v)) {
    throw new ApiError("INVALID_SCHEMA", `Expected one of: ${allowed.join(", ")}.`);
  }
  return v as T;
}

function reqStr(body: Record<string, unknown>, key: string): string {
  const v = body[key];
  if (typeof v !== "string" || v.length === 0) {
    throw new ApiError("INVALID_SCHEMA", `${key} is required.`);
  }
  return v;
}

// Keep the Lease interface referenced for callers/towers of the worker protocol.
export type { Lease };
