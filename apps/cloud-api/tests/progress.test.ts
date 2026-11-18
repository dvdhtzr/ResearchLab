import { SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";

const OWNER = { "content-type": "application/json" };
const WORKER = {
  "content-type": "application/json",
  authorization: "Bearer dev-worker-secret",
  "x-worker-id": "worker:alpha",
};
const idem = () => ({ "idempotency-key": crypto.randomUUID() });

interface Lease { leaseId: string; generation: number }

async function submitClaimStart(): Promise<{ jobId: string; lease: Lease }> {
  const jobId = ((await (await SELF.fetch("https://api.local/api/v1/owner/jobs", {
    method: "POST", headers: { ...OWNER, ...idem() },
    body: JSON.stringify({
      contractVersion: "1.0", jobType: "research.investigation.v1",
      request: { question: "What is PostgreSQL?", evidencePolicy: "hybrid-governed" },
      executionProfile: "standard", priority: "normal", visibility: "private",
    }),
  })).json()) as { jobId: string }).jobId;
  const claim = (await (await SELF.fetch("https://api.local/api/v1/worker/claims", {
    method: "POST", headers: { ...WORKER, ...idem() }, body: "{}",
  })).json()) as { leaseId: string; executionGeneration: number };
  const lease = { leaseId: claim.leaseId, generation: claim.executionGeneration };
  await SELF.fetch(`https://api.local/api/v1/worker/jobs/${jobId}/start`, {
    method: "POST", headers: { ...WORKER, ...idem() }, body: JSON.stringify(lease),
  });
  return { jobId, lease };
}

const postProgress = (jobId: string, lease: Lease, entries: unknown[]) =>
  SELF.fetch(`https://api.local/api/v1/worker/jobs/${jobId}/progress`, {
    method: "POST", headers: { ...WORKER }, body: JSON.stringify({ ...lease, entries }),
  });

const getProgress = (jobId: string, ownerHeaders: Record<string, string> = OWNER) =>
  SELF.fetch(`https://api.local/api/v1/owner/jobs/${jobId}/progress`, { headers: ownerHeaders });

describe("verbose job progress stream", () => {
  it("records worker progress and the owner reads it in order with detail", async () => {
    const { jobId, lease } = await submitClaimStart();
    const r1 = await postProgress(jobId, lease, [
      { stage: "plan", message: "planned 2 queries", level: "info", detail: { queries: ["PostgreSQL", "MySQL"] } },
      { stage: "search", message: "wikipedia: 3 hits", level: "detail" },
    ]);
    expect(r1.status).toBe(200);
    expect(((await r1.json()) as { appended: number }).appended).toBe(2);
    await postProgress(jobId, lease, [{ stage: "report", message: "disposition supported", level: "info" }]);

    const res = await getProgress(jobId);
    expect(res.status).toBe(200);
    const items = ((await res.json()) as {
      items: { sequence: number; stage: string; level: string; message: string; detail: unknown }[];
    }).items;
    expect(items.map((i) => i.sequence)).toEqual([1, 2, 3]);
    expect(items[0]!.stage).toBe("plan");
    expect((items[0]!.detail as { queries: string[] }).queries).toEqual(["PostgreSQL", "MySQL"]);
    expect(items[1]!.level).toBe("detail");
  });

  it("rejects progress from a stale lease (fencing) and records nothing", async () => {
    const { jobId } = await submitClaimStart();
    const res = await postProgress(jobId, { leaseId: "not-the-lease", generation: 999 }, [{ stage: "plan", message: "x" }]);
    expect(res.status).toBe(409);
    const items = ((await (await getProgress(jobId)).json()) as { items: unknown[] }).items;
    expect(items.length).toBe(0);
  });

  it("bounds the number of entries per call", async () => {
    const { jobId, lease } = await submitClaimStart();
    const many = Array.from({ length: 60 }, (_, i) => ({ stage: "search", message: `hit ${i}` }));
    expect((await postProgress(jobId, lease, many)).status).toBe(400);
  });

  it("returns 404 reading progress for a job that is not the owner's", async () => {
    const { jobId } = await submitClaimStart();
    expect((await getProgress(jobId, { ...OWNER, "X-Dev-Owner-Id": "github:not-owner" })).status).toBe(404);
  });
});
