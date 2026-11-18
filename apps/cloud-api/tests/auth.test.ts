import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { transition } from "../src/data/mutations.js";
import { requestCancel } from "../src/data/control.js";
import { requestHash } from "../src/domain/ids.js";
import { admit, claimAndStart, WORKER } from "./helpers.js";

describe("M0-09 unauthorized mutations rejected", () => {
  it("a different worker cannot commit another worker's job", async () => {
    const h = await claimAndStart();
    const otherWorker = { type: "worker" as const, id: "worker:intruder" };
    await expect(
      transition({
        db: env.DB,
        now: Date.now(),
        actor: otherWorker,
        operation: "worker.complete",
        idempotencyKey: crypto.randomUUID(),
        requestHash: await requestHash({ op: "complete" }),
        jobId: h.jobId,
        op: "complete",
        eventType: "completed",
        lease: { workerId: otherWorker.id, leaseId: h.leaseId, generation: h.generation },
      }),
    ).rejects.toMatchObject({ code: "UNAUTHORIZED" });

    const row = await env.DB.prepare("SELECT state FROM jobs WHERE id = ?").bind(h.jobId).first();
    expect(row?.state).toBe("Running");
  });

  it("a non-owner cannot cancel a job", async () => {
    const job = await admit({ key: "auth-cancel" });
    const intruder = { type: "owner" as const, id: "github:99999" };
    await expect(
      requestCancel({
        db: env.DB,
        now: Date.now(),
        owner: intruder,
        idempotencyKey: crypto.randomUUID(),
        requestHash: await requestHash({ op: "cancel" }),
        jobId: job.jobId,
      }),
    ).rejects.toMatchObject({ code: "UNAUTHORIZED" });

    const row = await env.DB.prepare("SELECT state FROM jobs WHERE id = ?").bind(job.jobId).first();
    expect(row?.state).toBe("Queued");
  });

  it("the correct worker with the wrong lease id is rejected as STALE_LEASE", async () => {
    const h = await claimAndStart();
    await expect(
      transition({
        db: env.DB,
        now: Date.now(),
        actor: WORKER,
        operation: "worker.complete",
        idempotencyKey: crypto.randomUUID(),
        requestHash: await requestHash({ op: "complete" }),
        jobId: h.jobId,
        op: "complete",
        eventType: "completed",
        lease: { workerId: h.workerId, leaseId: "wrong-lease-id", generation: h.generation },
      }),
    ).rejects.toMatchObject({ code: "STALE_LEASE" });
  });
});
