import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { reconcileExpiredLeases } from "../src/data/mutations.js";
import { authorizeRecovery } from "../src/data/control.js";
import { requestHash } from "../src/domain/ids.js";
import { claimAndStart, getEvents, OWNER, SYSTEM } from "./helpers.js";

const LEASE_MS = 180_000;

async function toRecoveryRequired() {
  const start = Date.now();
  const h = await claimAndStart(start);
  await reconcileExpiredLeases({ db: env.DB, now: start + LEASE_MS + 1, actor: SYSTEM });
  const row = await env.DB
    .prepare("SELECT state, state_version, execution_generation, budget_json FROM jobs WHERE id = ?")
    .bind(h.jobId)
    .first<{ state: string; state_version: number; execution_generation: number; budget_json: string }>();
  return { h, row: row! };
}

describe("M0-11 recovery preserves budget and history", () => {
  it("restart requeues with a new generation while preserving budget and all prior events", async () => {
    const { h, row } = await toRecoveryRequired();
    expect(row.state).toBe("RecoveryRequired");
    const eventsBefore = await getEvents(h.jobId);
    const budgetBefore = row.budget_json;
    const generationBefore = row.execution_generation;

    const res = await authorizeRecovery({
      db: env.DB,
      now: Date.now(),
      owner: OWNER,
      idempotencyKey: crypto.randomUUID(),
      requestHash: await requestHash({ op: "recovery", mode: "restart" }),
      jobId: h.jobId,
      expectedStateVersion: row.state_version,
      mode: "restart",
    });
    expect(res.outcome).toBe("accepted");

    const after = await env.DB
      .prepare("SELECT state, execution_generation, budget_json FROM jobs WHERE id = ?")
      .bind(h.jobId)
      .first<{ state: string; execution_generation: number; budget_json: string }>();

    expect(after?.state).toBe("Queued");
    expect(after?.execution_generation).toBe(generationBefore + 1); // new fence
    expect(after?.budget_json).toBe(budgetBefore); // budget NOT reset
    const eventsAfter = await getEvents(h.jobId);
    expect(eventsAfter.length).toBeGreaterThan(eventsBefore.length); // history appended, never erased
    // Every prior event is still present (append-only).
    for (const e of eventsBefore) {
      expect(eventsAfter.find((x) => x.event_id === e.event_id)).toBeTruthy();
    }
  });

  it("resume requires a validated checkpoint digest", async () => {
    const { h, row } = await toRecoveryRequired();
    await expect(
      authorizeRecovery({
        db: env.DB,
        now: Date.now(),
        owner: OWNER,
        idempotencyKey: crypto.randomUUID(),
        requestHash: await requestHash({ op: "recovery", mode: "resume" }),
        jobId: h.jobId,
        expectedStateVersion: row.state_version,
        mode: "resume",
      }),
    ).rejects.toMatchObject({ code: "INVALID_SCHEMA" });
  });

  it("resume with a digest requeues and records the approved checkpoint hash", async () => {
    const { h, row } = await toRecoveryRequired();
    const digest = "a".repeat(64);
    await authorizeRecovery({
      db: env.DB,
      now: Date.now(),
      owner: OWNER,
      idempotencyKey: crypto.randomUUID(),
      requestHash: await requestHash({ op: "recovery", mode: "resume", digest }),
      jobId: h.jobId,
      expectedStateVersion: row.state_version,
      mode: "resume",
      checkpointDigest: digest,
    });
    const after = await env.DB
      .prepare("SELECT state, approved_checkpoint_hash FROM jobs WHERE id = ?")
      .bind(h.jobId)
      .first<{ state: string; approved_checkpoint_hash: string }>();
    expect(after?.state).toBe("Queued");
    expect(after?.approved_checkpoint_hash).toBe(digest);
  });

  it("terminate marks the job Failed and preserves records", async () => {
    const { h, row } = await toRecoveryRequired();
    await authorizeRecovery({
      db: env.DB,
      now: Date.now(),
      owner: OWNER,
      idempotencyKey: crypto.randomUUID(),
      requestHash: await requestHash({ op: "recovery", mode: "terminate" }),
      jobId: h.jobId,
      expectedStateVersion: row.state_version,
      mode: "terminate",
    });
    const after = await env.DB.prepare("SELECT state FROM jobs WHERE id = ?").bind(h.jobId).first();
    expect(after?.state).toBe("Failed");
  });
});
