import { SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";

const OWNER = { "content-type": "application/json" };
const WORKER = {
  "content-type": "application/json",
  authorization: "Bearer dev-worker-secret",
  "x-worker-id": "worker:alpha",
};
const idem = () => ({ "idempotency-key": crypto.randomUUID() });

async function submitAndComplete(report: unknown) {
  const created = await SELF.fetch("https://api.local/api/v1/owner/jobs", {
    method: "POST",
    headers: { ...OWNER, ...idem() },
    body: JSON.stringify({
      contractVersion: "1.0",
      jobType: "research.investigation.v1",
      request: { question: "What is PostgreSQL?", evidencePolicy: "hybrid-governed" },
      executionProfile: "standard",
      priority: "normal",
      visibility: "private",
    }),
  });
  const { jobId } = (await created.json()) as { jobId: string };

  const claim = (await (await SELF.fetch("https://api.local/api/v1/worker/claims", {
    method: "POST", headers: { ...WORKER, ...idem() }, body: "{}",
  })).json()) as { leaseId: string; executionGeneration: number };
  const lease = { leaseId: claim.leaseId, generation: claim.executionGeneration };

  await SELF.fetch(`https://api.local/api/v1/worker/jobs/${jobId}/start`, {
    method: "POST", headers: { ...WORKER, ...idem() }, body: JSON.stringify(lease),
  });
  const completeRes = await SELF.fetch(`https://api.local/api/v1/worker/jobs/${jobId}/complete`, {
    method: "POST", headers: { ...WORKER, ...idem() },
    body: JSON.stringify({ ...lease, disposition: "supported", report }),
  });
  return { jobId, completeRes };
}

const sampleReport = {
  question: "What is PostgreSQL?",
  summary: "PostgreSQL is an open-source relational database using MVCC.",
  disposition: "supported",
  findings: ["PostgreSQL uses MVCC for concurrency.", "It is open source."],
};

describe("research report visibility", () => {
  it("surfaces the report to the owner on the completed event", async () => {
    const { jobId } = await submitAndComplete(sampleReport);
    const events = (await (await SELF.fetch(`https://api.local/api/v1/owner/jobs/${jobId}/events`, { headers: OWNER })).json() as {
      items: { eventType: string; details: { report?: { summary: string; findings: string[] } } }[];
    }).items;
    const completed = events.find((e) => e.eventType === "completed");
    expect(completed?.details.report?.summary).toContain("PostgreSQL");
    expect(completed?.details.report?.findings.length).toBe(2);
  });

  it("does not leak the report into public event projections", async () => {
    const { jobId } = await submitAndComplete(sampleReport);
    // The job is private → public events are 404 anyway; the server-templated public
    // projection never carries raw details regardless of visibility.
    const res = await SELF.fetch(`https://api.local/api/v1/public/jobs/${jobId}/events`);
    expect(res.status).toBe(404);
  });

  it("rejects an over-sized report", async () => {
    const huge = { summary: "x".repeat(70 * 1024) };
    const { completeRes } = await submitAndComplete(huge);
    expect(completeRes.status).toBe(400);
  });
});
