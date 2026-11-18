import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import {
  isTransitionAllowed,
  TRANSITION_RULES,
  targetState,
} from "../src/domain/transitions.js";
import { JOB_STATES, isTerminal } from "../src/domain/states.js";
import { transition } from "../src/data/mutations.js";
import { requestHash } from "../src/domain/ids.js";
import { admit, claimAndStart, WORKER } from "./helpers.js";

describe("transition table (pure domain)", () => {
  it("only lists the 11 approved transitions", () => {
    expect(TRANSITION_RULES.length).toBe(11);
  });
  it("forbids transitions out of terminal states", () => {
    for (const s of JOB_STATES.filter(isTerminal)) {
      for (const rule of TRANSITION_RULES) {
        expect(rule.from.includes(s)).toBe(false);
      }
    }
  });
  it("Queued can only go to Claimed or Cancelled", () => {
    expect(isTransitionAllowed("claim", "Queued")).toBe(true);
    expect(isTransitionAllowed("cancel_queued", "Queued")).toBe(true);
    expect(isTransitionAllowed("complete", "Queued")).toBe(false);
    expect(isTransitionAllowed("start", "Queued")).toBe(false);
  });
  it("Running completes to Completed", () => {
    expect(targetState("complete")).toBe("Completed");
  });
});

describe("M0-08 terminal states are immutable", () => {
  it("a completed job rejects any further transition with STATE_CONFLICT", async () => {
    const h = await claimAndStart();
    await transition({
      db: env.DB,
      now: Date.now(),
      actor: WORKER,
      operation: "worker.complete",
      idempotencyKey: crypto.randomUUID(),
      requestHash: await requestHash({ op: "complete", jobId: h.jobId }),
      jobId: h.jobId,
      op: "complete",
      eventType: "completed",
      lease: { workerId: h.workerId, leaseId: h.leaseId, generation: h.generation },
      clearLease: true,
      extraSet: { research_disposition: "supported" },
    });

    const before = await env.DB.prepare("SELECT state FROM jobs WHERE id = ?").bind(h.jobId).first();
    expect(before?.state).toBe("Completed");

    // Attempt to fail an already-completed job.
    await expect(
      transition({
        db: env.DB,
        now: Date.now(),
        actor: WORKER,
        operation: "worker.fail",
        idempotencyKey: crypto.randomUUID(),
        requestHash: await requestHash({ op: "fail", jobId: h.jobId }),
        jobId: h.jobId,
        op: "exec_fail",
        eventType: "failed",
        lease: { workerId: h.workerId, leaseId: h.leaseId, generation: h.generation },
      }),
    ).rejects.toMatchObject({ code: "STATE_CONFLICT" });

    const after = await env.DB.prepare("SELECT state FROM jobs WHERE id = ?").bind(h.jobId).first();
    expect(after?.state).toBe("Completed");
  });

  it("cannot start a job that was never claimed", async () => {
    const job = await admit({ key: "sm-never-claimed" });
    await expect(
      transition({
        db: env.DB,
        now: Date.now(),
        actor: WORKER,
        operation: "worker.start",
        idempotencyKey: crypto.randomUUID(),
        requestHash: await requestHash({ op: "start" }),
        jobId: job.jobId,
        op: "start",
        eventType: "started",
        lease: { workerId: "worker:alpha", leaseId: "nonexistent", generation: 1 },
      }),
    ).rejects.toMatchObject({ code: "STATE_CONFLICT" });
  });
});
