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

function validReport(overrides: Record<string, unknown> = {}) {
  return {
    schemaVersion: "public-report.v1",
    reportVersion: 1,
    question: "Compare SQL Server and PostgreSQL",
    executiveSummary: "Both are capable relational databases.",
    findings: [{ id: "f1", statement: "PostgreSQL uses MVCC", evidence: ["p1"], limitations: "one source" }],
    challenges: [],
    disposition: "supported",
    ...overrides,
  };
}

async function admitJob(): Promise<string> {
  const res = await SELF.fetch("https://api.local/api/v1/owner/jobs", {
    method: "POST",
    headers: { ...OWNER, ...idem() },
    body: JSON.stringify({
      contractVersion: "1.0",
      jobType: "research.investigation.v1",
      request: { question: "Compare SQL Server and PostgreSQL", evidencePolicy: "hybrid-governed" },
      executionProfile: "standard",
      priority: "normal",
      visibility: "private",
    }),
  });
  return ((await res.json()) as { jobId: string }).jobId;
}

async function registerManifest(jobId: string, digest: string, length: number) {
  const res = await SELF.fetch("https://api.local/api/v1/worker/artifacts/manifests", {
    method: "POST",
    headers: { ...WORKER, ...idem() },
    body: JSON.stringify({ jobId, contentDigest: digest, contentLength: length, schemaVersion: "public-report.v1", summary: {} }),
  });
  return res;
}

async function approve(jobId: string, artifactId: string, digest: string) {
  return SELF.fetch(`https://api.local/api/v1/owner/jobs/${jobId}/publication`, {
    method: "POST",
    headers: { ...OWNER, ...idem() },
    body: JSON.stringify({ action: "approve_digest", artifactId, contentDigest: digest }),
  });
}

async function uploadContent(jobId: string, bytes: string) {
  return SELF.fetch(`https://api.local/api/v1/worker/artifacts/content?jobId=${jobId}`, {
    method: "POST",
    headers: { ...WORKER, ...idem() },
    body: bytes,
  });
}

describe("M5 publication pipeline", () => {
  it("full flow: manifest → exact-digest approval → content → public report", async () => {
    const jobId = await admitJob();
    const bytes = JSON.stringify(validReport());
    const digest = await sha256Hex(bytes);

    const manifestRes = await registerManifest(jobId, digest, byteLength(bytes));
    expect(manifestRes.status).toBe(201);
    const { artifactId } = (await manifestRes.json()) as { artifactId: string };

    expect((await approve(jobId, artifactId, digest)).status).toBe(200);

    const uploadRes = await uploadContent(jobId, bytes);
    expect(uploadRes.status).toBe(201);
    const { reportId, outcome } = (await uploadRes.json()) as { reportId: string; outcome: string };
    expect(outcome).toBe("published");

    const publicRes = await SELF.fetch(`https://api.local/api/v1/public/reports/${reportId}`);
    expect(publicRes.status).toBe(200);
    const published = (await publicRes.json()) as { question: string };
    expect(published.question).toBe("Compare SQL Server and PostgreSQL");
  });

  it("a changed artifact cannot reuse a previous approval (exact bytes only)", async () => {
    const jobId = await admitJob();
    const bytes = JSON.stringify(validReport());
    const digest = await sha256Hex(bytes);
    const { artifactId } = (await (await registerManifest(jobId, digest, byteLength(bytes))).json()) as { artifactId: string };
    await approve(jobId, artifactId, digest);

    // Upload DIFFERENT bytes (one extra space) — different digest, no matching manifest/approval.
    const tampered = bytes.replace("}", " }");
    const res = await uploadContent(jobId, tampered);
    expect(res.status).toBe(409); // STATE_CONFLICT: no manifest matches the submitted bytes
  });

  it("unapproved content cannot be published and is never public", async () => {
    const jobId = await admitJob();
    const bytes = JSON.stringify(validReport());
    const digest = await sha256Hex(bytes);
    await registerManifest(jobId, digest, byteLength(bytes)); // manifest but NO approval

    const res = await uploadContent(jobId, bytes);
    expect(res.status).toBe(403); // UNAUTHORIZED: authenticated worker, but these bytes aren't approved
    const body = (await res.json()) as { error: { code: string } };
    expect(body.error.code).toBe("UNAUTHORIZED");
  });

  it("rejects a report carrying a disallowed (private) field even when approved", async () => {
    const jobId = await admitJob();
    const bytes = JSON.stringify(validReport({ internalPrompt: "secret system prompt" }));
    const digest = await sha256Hex(bytes);
    const { artifactId } = (await (await registerManifest(jobId, digest, byteLength(bytes))).json()) as { artifactId: string };
    await approve(jobId, artifactId, digest);

    const res = await uploadContent(jobId, bytes);
    expect(res.status).toBe(400);
    const body = (await res.json()) as { error: { code: string } };
    expect(body.error.code).toBe("INVALID_SCHEMA");
  });

  it("withdrawal makes a published report unavailable", async () => {
    const jobId = await admitJob();
    const bytes = JSON.stringify(validReport());
    const digest = await sha256Hex(bytes);
    const { artifactId } = (await (await registerManifest(jobId, digest, byteLength(bytes))).json()) as { artifactId: string };
    await approve(jobId, artifactId, digest);
    const { reportId } = (await (await uploadContent(jobId, bytes)).json()) as { reportId: string };

    await SELF.fetch(`https://api.local/api/v1/owner/jobs/${jobId}/publication`, {
      method: "POST",
      headers: { ...OWNER, ...idem() },
      body: JSON.stringify({ action: "withdraw", reportId }),
    });

    expect((await SELF.fetch(`https://api.local/api/v1/public/reports/${reportId}`)).status).toBe(404);
  });

  it("private jobs never appear in public projections", async () => {
    const jobId = await admitJob(); // visibility: private
    expect((await SELF.fetch(`https://api.local/api/v1/public/jobs/${jobId}`)).status).toBe(404);
    const list = (await (await SELF.fetch("https://api.local/api/v1/public/jobs")).json()) as { items: unknown[] };
    expect(list.items.length).toBe(0);
  });
});
