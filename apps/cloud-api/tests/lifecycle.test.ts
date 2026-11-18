import { SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";

// M1: a repeatable end-to-end local job lifecycle driven entirely through the
// authoritative HTTP API: Submit → Queued → Claimed → Running → Completed → Display.
// No Ollama. Uses SELF (the real Worker) against local D1.

const OWNER_HEADERS = { "content-type": "application/json" };
const WORKER_HEADERS = {
  "content-type": "application/json",
  authorization: "Bearer dev-worker-secret",
  "x-worker-id": "worker:alpha",
};

function idem() {
  return { "idempotency-key": crypto.randomUUID() };
}

async function submitJob(body?: unknown) {
  return SELF.fetch("https://api.local/api/v1/owner/jobs", {
    method: "POST",
    headers: { ...OWNER_HEADERS, ...idem() },
    body: JSON.stringify(
      body ?? {
        contractVersion: "1.0",
        jobType: "research.investigation.v1",
        request: { question: "Compare SQL Server and PostgreSQL", evidencePolicy: "hybrid-governed" },
        executionProfile: "standard",
        priority: "normal",
        visibility: "private",
      },
    ),
  });
}

describe("M1 end-to-end job lifecycle", () => {
  it("Submit → Queued → Claimed → Running → Completed → Display", async () => {
    // Submit
    const created = await submitJob();
    expect(created.status).toBe(201);
    const { jobId } = (await created.json()) as { jobId: string };
    expect(jobId).toBeTruthy();

    // Queued
    let view = await (await SELF.fetch(`https://api.local/api/v1/owner/jobs/${jobId}`, { headers: OWNER_HEADERS })).json() as { state: string };
    expect(view.state).toBe("Queued");

    // Claim
    const claimRes = await SELF.fetch("https://api.local/api/v1/worker/claims", {
      method: "POST",
      headers: { ...WORKER_HEADERS, ...idem() },
      body: "{}",
    });
    const claim = (await claimRes.json()) as { outcome: string; jobId: string; leaseId: string; executionGeneration: number };
    expect(claim.outcome).toBe("accepted");
    expect(claim.jobId).toBe(jobId);
    const lease = { leaseId: claim.leaseId, generation: claim.executionGeneration };

    view = await (await SELF.fetch(`https://api.local/api/v1/owner/jobs/${jobId}`, { headers: OWNER_HEADERS })).json() as { state: string };
    expect(view.state).toBe("Claimed");

    // Start → Running
    const startRes = await SELF.fetch(`https://api.local/api/v1/worker/jobs/${jobId}/start`, {
      method: "POST",
      headers: { ...WORKER_HEADERS, ...idem() },
      body: JSON.stringify(lease),
    });
    expect(startRes.status).toBe(200);
    view = await (await SELF.fetch(`https://api.local/api/v1/owner/jobs/${jobId}`, { headers: OWNER_HEADERS })).json() as { state: string };
    expect(view.state).toBe("Running");

    // Complete → Completed
    const completeRes = await SELF.fetch(`https://api.local/api/v1/worker/jobs/${jobId}/complete`, {
      method: "POST",
      headers: { ...WORKER_HEADERS, ...idem() },
      body: JSON.stringify({ ...lease, disposition: "supported" }),
    });
    expect(completeRes.status).toBe(200);

    const finalView = await (await SELF.fetch(`https://api.local/api/v1/owner/jobs/${jobId}`, { headers: OWNER_HEADERS })).json() as { state: string; researchDisposition: string };
    expect(finalView.state).toBe("Completed");
    expect(finalView.researchDisposition).toBe("supported");

    // Display: the timeline shows the full sequence of real persisted events.
    const events = (await (await SELF.fetch(`https://api.local/api/v1/owner/jobs/${jobId}/events`, { headers: OWNER_HEADERS })).json() as { items: { eventType: string }[] }).items;
    const types = events.map((e) => e.eventType);
    expect(types).toEqual(["admitted", "claimed", "started", "completed"]);
  });

  it("is repeatable: a second job runs the same lifecycle independently", async () => {
    const created = await submitJob();
    const { jobId } = (await created.json()) as { jobId: string };

    const claim = (await (await SELF.fetch("https://api.local/api/v1/worker/claims", {
      method: "POST", headers: { ...WORKER_HEADERS, ...idem() }, body: "{}",
    })).json()) as { jobId: string; leaseId: string; executionGeneration: number };
    const lease = { leaseId: claim.leaseId, generation: claim.executionGeneration };
    await SELF.fetch(`https://api.local/api/v1/worker/jobs/${claim.jobId}/start`, {
      method: "POST", headers: { ...WORKER_HEADERS, ...idem() }, body: JSON.stringify(lease),
    });
    await SELF.fetch(`https://api.local/api/v1/worker/jobs/${claim.jobId}/complete`, {
      method: "POST", headers: { ...WORKER_HEADERS, ...idem() }, body: JSON.stringify({ ...lease, disposition: "mixed" }),
    });
    const view = await (await SELF.fetch(`https://api.local/api/v1/owner/jobs/${jobId}`, { headers: OWNER_HEADERS })).json() as { state: string };
    expect(view.state).toBe("Completed");
  });
});

describe("M1 API guards", () => {
  it("rejects worker calls without a valid credential (401)", async () => {
    const res = await SELF.fetch("https://api.local/api/v1/worker/claims", {
      method: "POST",
      headers: { "content-type": "application/json", "x-worker-id": "worker:alpha", ...idem() },
      body: "{}",
    });
    expect(res.status).toBe(401);
  });

  it("rejects job creation without an Idempotency-Key (400)", async () => {
    const res = await SELF.fetch("https://api.local/api/v1/owner/jobs", {
      method: "POST",
      headers: OWNER_HEADERS,
      body: JSON.stringify({ contractVersion: "1.0", jobType: "research.investigation.v1", request: { question: "x", evidencePolicy: "hybrid-governed" }, executionProfile: "quick", priority: "low", visibility: "private" }),
    });
    expect(res.status).toBe(400);
  });

  it("rejects an invalid schema with a machine-readable error", async () => {
    const res = await SELF.fetch("https://api.local/api/v1/owner/jobs", {
      method: "POST",
      headers: { ...OWNER_HEADERS, ...idem() },
      body: JSON.stringify({ contractVersion: "9.9", jobType: "x", request: {}, executionProfile: "quick", priority: "low", visibility: "private" }),
    });
    expect(res.status).toBe(400);
    const body = (await res.json()) as { error: { code: string } };
    expect(body.error.code).toBe("UNSUPPORTED_VERSION");
  });

  it("owner cannot see another owner's job", async () => {
    const created = await submitJob();
    const { jobId } = (await created.json()) as { jobId: string };
    const res = await SELF.fetch(`https://api.local/api/v1/owner/jobs/${jobId}`, {
      headers: { ...OWNER_HEADERS, "x-dev-owner-id": "github:someone-else" },
    });
    expect(res.status).toBe(404);
  });
});
