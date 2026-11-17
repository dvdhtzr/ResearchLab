import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { reconcileExpiredLeases, transition } from "../src/data/mutations.js";
import { requestHash } from "../src/domain/ids.js";
import { claimAndStart, getEvents, SYSTEM, WORKER } from "./helpers.js";

const LEASE_MS = 180_000;

describe("M0-07 expired lease requires recovery", () => {
  it("a worker commit after lease expiry is rejected as STALE_LEASE", async () => {
    const start = Date.now();
    const h = await claimAndStart(start);
    const afterExpiry = start + LEASE_MS + 1;

    await expect(
      transition({
        db: env.DB,
        now: afterExpiry,
        actor: WORKER,
        operation: "worker.complete",
        idempotencyKey: crypto.randomUUID(),
        requestHash: await requestHash({ op: "complete" }),
        jobId: h.jobId,
        op: "complete",
        eventType: "completed",
        lease: { workerId: h.workerId, leaseId: h.leaseId, generation: h.generation },
      }),
    ).rejects.toMatchObject({ code: "STALE_LEASE" });
  });

  it("reconciliation moves an expired active job to RecoveryRequired with an audit event", async () => {
    const start = Date.now();
    const h = await claimAndStart(start);
    const afterExpiry = start + LEASE_MS + 1;

    // Before reconcile, the job is still Running (a read never mutated it).
    const before = await env.DB.prepare("SELECT state FROM jobs WHERE id = ?").bind(h.jobId).first();
    expect(before?.state).toBe("Running");

    const result = await reconcileExpiredLeases({ db: env.DB, now: afterExpiry, actor: SYSTEM });
    expect(result.reconciled).toContain(h.jobId);

    const after = await env.DB.prepare("SELECT state FROM jobs WHERE id = ?").bind(h.jobId).first();
    expect(after?.state).toBe("RecoveryRequired");

    const events = await getEvents(h.jobId);
    expect(events.some((e) => e.event_type === "lease_expired")).toBe(true);
  });

  it("reconciliation does NOT touch a job whose lease is still valid", async () => {
    const start = Date.now();
    const h = await claimAndStart(start);
    const result = await reconcileExpiredLeases({ db: env.DB, now: start + 1000, actor: SYSTEM });
    expect(result.reconciled).not.toContain(h.jobId);
    const row = await env.DB.prepare("SELECT state FROM jobs WHERE id = ?").bind(h.jobId).first();
    expect(row?.state).toBe("Running");
  });
});
