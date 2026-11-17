import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { validateJobCreation } from "../src/domain/validation.js";
import { admitJob } from "../src/data/mutations.js";
import { isApiError } from "../src/domain/errors.js";
import { requestHash } from "../src/domain/ids.js";
import { admit, creationBody, OWNER } from "./helpers.js";

describe("M0-01 invalid schemas rejected", () => {
  it("rejects unknown top-level properties", () => {
    expect(() => validateJobCreation(creationBody({ extra: true }))).toThrowError(/Unknown property/);
  });
  it("rejects wrong contract version", () => {
    try {
      validateJobCreation(creationBody({ contractVersion: "2.0" }));
      throw new Error("should have thrown");
    } catch (e) {
      expect(isApiError(e) && e.code).toBe("UNSUPPORTED_VERSION");
    }
  });
  it("rejects unregistered job type", () => {
    expect(() => validateJobCreation(creationBody({ jobType: "unknown.v1" }))).toThrow();
  });
  it("rejects empty question after trim", () => {
    expect(() =>
      validateJobCreation(creationBody({ request: { question: "   ", evidencePolicy: "hybrid-governed" } })),
    ).toThrowError(/characters after trimming/);
  });
  it("rejects over-length question", () => {
    const q = "a".repeat(4001);
    expect(() =>
      validateJobCreation(creationBody({ request: { question: q, evidencePolicy: "hybrid-governed" } })),
    ).toThrow();
  });
  it("rejects invalid enum values", () => {
    expect(() => validateJobCreation(creationBody({ priority: "urgent" }))).toThrow();
    expect(() => validateJobCreation(creationBody({ executionProfile: "turbo" }))).toThrow();
  });
  it("rejects a client attempt to self-grant public_demo", () => {
    expect(() => validateJobCreation(creationBody({ visibility: "public_demo" }))).toThrow();
  });
});

describe("M0-02 duplicate job admission creates one job", () => {
  it("same idempotency key + payload returns the same job id and persists one row", async () => {
    const key = "idem-key-1";
    const first = await admit({ key });
    const second = await admit({ key });
    expect(second.jobId).toBe(first.jobId);

    const { results } = await env.DB
      .prepare("SELECT id FROM jobs WHERE owner_id = ?")
      .bind(OWNER.id)
      .all();
    expect(results.length).toBe(1);
  });
});

describe("M0-03 conflicting idempotency payload rejected", () => {
  it("same key with a different payload is a conflict, original job untouched", async () => {
    const key = "idem-key-2";
    const original = await admit({ key, body: creationBody({ priority: "low" }) });

    const conflicting = creationBody({ priority: "high" });
    const creation = validateJobCreation(conflicting);
    const hash = await requestHash(conflicting);

    await expect(
      admitJob({
        db: env.DB,
        now: Date.now(),
        owner: OWNER,
        idempotencyKey: key,
        requestHash: hash,
        creation,
      }),
    ).rejects.toMatchObject({ code: "IDEMPOTENCY_CONFLICT" });

    const row = await env.DB.prepare("SELECT priority FROM jobs WHERE id = ?").bind(original.jobId).first();
    expect(row?.priority).toBe("low");
  });
});
