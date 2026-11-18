import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { transition } from "../src/data/mutations.js";
import { requestHash } from "../src/domain/ids.js";
import { admit, claimAndStart, getEvents, WORKER } from "./helpers.js";

describe("M0-05 state transition and audit event commit atomically", () => {
  it("a successful transition writes exactly one event and one receipt together", async () => {
    const h = await claimAndStart();
    const eventsBefore = await getEvents(h.jobId);

    await transition({
      db: env.DB,
      now: Date.now(),
      actor: WORKER,
      operation: "worker.complete",
      idempotencyKey: crypto.randomUUID(),
      requestHash: await requestHash({ op: "complete" }),
      jobId: h.jobId,
      op: "complete",
      eventType: "completed",
      lease: { workerId: h.workerId, leaseId: h.leaseId, generation: h.generation },
      clearLease: true,
      extraSet: { research_disposition: "supported" },
    });

    const eventsAfter = await getEvents(h.jobId);
    expect(eventsAfter.length).toBe(eventsBefore.length + 1);
    const last = eventsAfter[eventsAfter.length - 1]!;
    expect(last.to_state).toBe("Completed");

    // Exactly one receipt for this mutation, referencing the same mutation_id as the event.
    const receipt = await env.DB
      .prepare("SELECT mutation_id FROM mutation_receipts WHERE operation = 'worker.complete' AND job_id = ?")
      .bind(h.jobId)
      .first<{ mutation_id: string }>();
    expect(receipt?.mutation_id).toBe(last.mutation_id);
  });

  it("a rejected transition writes neither event nor receipt", async () => {
    const job = await admit({ key: "atomic-reject" });
    const eventsBefore = await getEvents(job.jobId);
    const receiptsBefore = await env.DB.prepare("SELECT COUNT(*) AS n FROM mutation_receipts").first<{ n: number }>();

    // complete is illegal from Queued
    await expect(
      transition({
        db: env.DB,
        now: Date.now(),
        actor: WORKER,
        operation: "worker.complete",
        idempotencyKey: crypto.randomUUID(),
        requestHash: await requestHash({ op: "complete" }),
        jobId: job.jobId,
        op: "complete",
        eventType: "completed",
        lease: { workerId: "worker:alpha", leaseId: "x", generation: 1 },
      }),
    ).rejects.toThrow();

    const eventsAfter = await getEvents(job.jobId);
    const receiptsAfter = await env.DB.prepare("SELECT COUNT(*) AS n FROM mutation_receipts").first<{ n: number }>();
    expect(eventsAfter.length).toBe(eventsBefore.length);
    expect(receiptsAfter?.n).toBe(receiptsBefore?.n);
  });
});

describe("M0-06 stale generation rejected", () => {
  it("a worker holding a superseded generation cannot commit", async () => {
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
        // Correct worker + lease, but a stale (wrong) generation.
        lease: { workerId: h.workerId, leaseId: h.leaseId, generation: h.generation + 7 },
      }),
    ).rejects.toMatchObject({ code: "STALE_EXECUTION_GENERATION" });

    const state = await env.DB.prepare("SELECT state FROM jobs WHERE id = ?").bind(h.jobId).first();
    expect(state?.state).toBe("Running");
  });
});

describe("M0-10 database failure cannot create false success", () => {
  it("an injected batch failure throws and leaves no state change, event, or receipt", async () => {
    const h = await claimAndStart();
    const stateBefore = await env.DB.prepare("SELECT state, state_version FROM jobs WHERE id = ?").bind(h.jobId).first<{ state: string; state_version: number }>();
    const eventsBefore = await getEvents(h.jobId);
    const receiptsBefore = await env.DB.prepare("SELECT COUNT(*) AS n FROM mutation_receipts").first<{ n: number }>();

    // Proxy that fails only on batch() — prepare/first still work.
    const failingDb = new Proxy(env.DB, {
      get(target, prop, receiver) {
        if (prop === "batch") {
          return () => Promise.reject(new Error("injected D1 failure"));
        }
        const v = Reflect.get(target, prop, receiver);
        return typeof v === "function" ? v.bind(target) : v;
      },
    }) as D1Database;

    await expect(
      transition({
        db: failingDb,
        now: Date.now(),
        actor: WORKER,
        operation: "worker.complete",
        idempotencyKey: crypto.randomUUID(),
        requestHash: await requestHash({ op: "complete" }),
        jobId: h.jobId,
        op: "complete",
        eventType: "completed",
        lease: { workerId: h.workerId, leaseId: h.leaseId, generation: h.generation },
      }),
    ).rejects.toMatchObject({ code: "CLOUD_PERSISTENCE_FAILURE" });

    const stateAfter = await env.DB.prepare("SELECT state, state_version FROM jobs WHERE id = ?").bind(h.jobId).first<{ state: string; state_version: number }>();
    const eventsAfter = await getEvents(h.jobId);
    const receiptsAfter = await env.DB.prepare("SELECT COUNT(*) AS n FROM mutation_receipts").first<{ n: number }>();

    expect(stateAfter).toEqual(stateBefore);
    expect(eventsAfter.length).toBe(eventsBefore.length);
    expect(receiptsAfter?.n).toBe(receiptsBefore?.n);
  });
});
