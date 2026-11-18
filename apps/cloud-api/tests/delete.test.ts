import { SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { sha256Hex } from "../src/domain/ids.js";
import { byteLength } from "../src/domain/publicReport.js";

const OWNER = { "content-type": "application/json" };
const WORKER = {
  "content-type": "application/json",
  authorization: "Bearer dev-worker-secret",
  "x-worker-id": "worker:alpha",
};
const idem = () => ({ "idempotency-key": crypto.randomUUID() });

async function submit(ownerHeaders: Record<string, string> = OWNER): Promise<string> {
  const res = await SELF.fetch("https://api.local/api/v1/owner/jobs", {
    method: "POST",
    headers: { ...ownerHeaders, ...idem() },
    body: JSON.stringify({
      contractVersion: "1.0",
      jobType: "research.investigation.v1",
      request: { question: "What is PostgreSQL?", evidencePolicy: "hybrid-governed" },
      executionProfile: "standard",
      priority: "normal",
      visibility: "private",
    }),
  });
  return ((await res.json()) as { jobId: string }).jobId;
}

async function runToComplete(jobId: string) {
  const claim = (await (await SELF.fetch("https://api.local/api/v1/worker/claims", {
    method: "POST", headers: { ...WORKER, ...idem() }, body: "{}",
  })).json()) as { leaseId: string; executionGeneration: number };
  const lease = { leaseId: claim.leaseId, generation: claim.executionGeneration };
  await SELF.fetch(`https://api.local/api/v1/worker/jobs/${jobId}/start`, {
    method: "POST", headers: { ...WORKER, ...idem() }, body: JSON.stringify(lease),
  });
  await SELF.fetch(`https://api.local/api/v1/worker/jobs/${jobId}/complete`, {
    method: "POST", headers: { ...WORKER, ...idem() }, body: JSON.stringify({ ...lease, disposition: "supported" }),
  });
}

const del = (jobId: string, ownerHeaders: Record<string, string> = OWNER) =>
  SELF.fetch(`https://api.local/api/v1/owner/jobs/${jobId}`, { method: "DELETE", headers: ownerHeaders });

describe("owner job deletion (cascade)", () => {
  it("removes the job and its audit trail", async () => {
    const jobId = await submit();
    await runToComplete(jobId);
    expect((await SELF.fetch(`https://api.local/api/v1/owner/jobs/${jobId}/events`, { headers: OWNER })).status).toBe(200);

    const res = await del(jobId);
    expect(res.status).toBe(200);
    expect(((await res.json()) as { deleted: boolean }).deleted).toBe(true);

    expect((await SELF.fetch(`https://api.local/api/v1/owner/jobs/${jobId}`, { headers: OWNER })).status).toBe(404);
    expect((await SELF.fetch(`https://api.local/api/v1/owner/jobs/${jobId}/events`, { headers: OWNER })).status).toBe(404);
    const list = (await (await SELF.fetch("https://api.local/api/v1/owner/jobs", { headers: OWNER })).json()) as {
      items: { jobId: string }[];
    };
    expect(list.items.find((j) => j.jobId === jobId)).toBeUndefined();
  });

  it("cascades a published report out of the public showcase", async () => {
    const jobId = await submit();
    const bytes = JSON.stringify({
      schemaVersion: "public-report.v1", reportVersion: 1, question: "What is PostgreSQL?",
      executiveSummary: "An open-source RDBMS.",
      findings: [{ id: "f1", statement: "Uses MVCC", evidence: ["p1"] }], challenges: [], disposition: "supported",
    });
    const digest = await sha256Hex(bytes);
    const { artifactId } = (await (await SELF.fetch("https://api.local/api/v1/worker/artifacts/manifests", {
      method: "POST", headers: { ...WORKER, ...idem() },
      body: JSON.stringify({ jobId, contentDigest: digest, contentLength: byteLength(bytes), schemaVersion: "public-report.v1", summary: {} }),
    })).json()) as { artifactId: string };
    await SELF.fetch(`https://api.local/api/v1/owner/jobs/${jobId}/publication`, {
      method: "POST", headers: { ...OWNER, ...idem() },
      body: JSON.stringify({ action: "approve_digest", artifactId, contentDigest: digest }),
    });
    const { reportId } = (await (await SELF.fetch(`https://api.local/api/v1/worker/artifacts/content?jobId=${jobId}`, {
      method: "POST", headers: { ...WORKER, ...idem() }, body: bytes,
    })).json()) as { reportId: string };
    expect((await SELF.fetch(`https://api.local/api/v1/public/reports/${reportId}`)).status).toBe(200);

    expect((await del(jobId)).status).toBe(200);
    // The published report is gone from the public surface (cascade through publication tables).
    expect((await SELF.fetch(`https://api.local/api/v1/public/reports/${reportId}`)).status).toBe(404);
  });

  it("refuses to delete another owner's job", async () => {
    const jobId = await submit(); // default dev owner
    const res = await del(jobId, { ...OWNER, "X-Dev-Owner-Id": "github:someone-else" });
    expect(res.status).toBe(403);
    // still present for the real owner
    expect((await SELF.fetch(`https://api.local/api/v1/owner/jobs/${jobId}`, { headers: OWNER })).status).toBe(200);
  });

  it("returns 404 deleting a non-existent job", async () => {
    expect((await del("job_does_not_exist")).status).toBe(404);
  });
});
