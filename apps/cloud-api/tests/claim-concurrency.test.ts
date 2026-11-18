import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { claimNext } from "../src/data/mutations.js";
import { requestHash } from "../src/domain/ids.js";
import { admit, AUTHORIZED_JOB_TYPES, creationBody, WORKER } from "./helpers.js";

async function claim(key: string, now = Date.now()) {
  return claimNext({
    db: env.DB,
    now,
    worker: WORKER,
    idempotencyKey: key,
    requestHash: await requestHash({ op: "claim", worker: WORKER.id }),
    leaseSeconds: 180,
    authorizedJobTypes: AUTHORIZED_JOB_TYPES,
  });
}

describe("M0-04 concurrent claims produce one winner", () => {
  it("N concurrent claims on one queued job yield exactly one success", async () => {
    await admit({ key: "c-job-1" });
    const results = await Promise.all(
      Array.from({ length: 8 }, (_, i) => claim(`claim-key-${i}`)),
    );
    const winners = results.filter((r) => r.claimed);
    expect(winners.length).toBe(1);

    const claimedRows = await env.DB
      .prepare("SELECT COUNT(*) AS n FROM jobs WHERE state = 'Claimed'")
      .first<{ n: number }>();
    expect(claimedRows?.n).toBe(1);
  });

  it("INV-03: a second claim finds no work while an unexpired active lease exists", async () => {
    await admit({ key: "c-job-a" });
    await admit({ key: "c-job-b" });
    const first = await claim("k-first");
    const second = await claim("k-second");
    expect(first.claimed).toBe(true);
    expect(second.claimed).toBe(false);
    expect((second.response as { outcome: string }).outcome).toBe("no_work");
  });
});

describe("claim response carries the research request", () => {
  it("includes the question, evidence policy, and execution profile for the worker", async () => {
    await admit({ key: "c-req", body: creationBody({ executionProfile: "quick" }) });
    const res = await claim("k-req");
    expect(res.claimed).toBe(true);
    const r = res.response as unknown as { executionProfile: string; request: { question: string; evidencePolicy: string } };
    expect(r.executionProfile).toBe("quick");
    expect(r.request.question).toBe("Compare SQL Server and PostgreSQL");
    expect(r.request.evidencePolicy).toBe("hybrid-governed");
  });
});

describe("deterministic claim ordering", () => {
  it("claims high priority before normal before low (oldest within a priority)", async () => {
    const t0 = Date.now();
    await admit({ key: "p-low", body: creationBody({ priority: "low" }), now: t0 });
    await admit({ key: "p-high", body: creationBody({ priority: "high" }), now: t0 + 1 });
    await admit({ key: "p-normal", body: creationBody({ priority: "normal" }), now: t0 + 2 });

    const res = await claim("k-pri");
    expect(res.claimed).toBe(true);
    const job = await env.DB
      .prepare("SELECT priority FROM jobs WHERE id = ?")
      .bind((res.response as { jobId: string }).jobId)
      .first<{ priority: string }>();
    expect(job?.priority).toBe("high");
  });
});
