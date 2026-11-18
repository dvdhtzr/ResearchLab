// Mock worker driver (M1). Stands in for the Windows .NET Worker Service until M3.
// It performs NO model inference — it simply drives the authoritative lifecycle:
//   claim → start → (simulated work) → complete
// Everything authoritative (state, lease, fencing, events) lives in the cloud API.

export type Fetcher = (path: string, init: RequestInit) => Promise<Response>;

export interface WorkerConfig {
  workerId: string;
  bearer: string;
  leaseSeconds?: number;
}

function workerHeaders(cfg: WorkerConfig, idempotencyKey: string): Record<string, string> {
  return {
    "content-type": "application/json",
    authorization: `Bearer ${cfg.bearer}`,
    "x-worker-id": cfg.workerId,
    "idempotency-key": idempotencyKey,
  };
}

const uuid = () => globalThis.crypto.randomUUID();

export interface ClaimOutcome {
  claimed: boolean;
  jobId?: string;
  leaseId?: string;
  generation?: number;
}

export async function claim(fetcher: Fetcher, cfg: WorkerConfig): Promise<ClaimOutcome> {
  const res = await fetcher("/api/v1/worker/claims", {
    method: "POST",
    headers: workerHeaders(cfg, uuid()),
    body: "{}",
  });
  const data = (await res.json()) as Record<string, unknown>;
  if (data.outcome === "accepted") {
    return {
      claimed: true,
      jobId: data.jobId as string,
      leaseId: data.leaseId as string,
      generation: data.executionGeneration as number,
    };
  }
  return { claimed: false };
}

async function post(fetcher: Fetcher, cfg: WorkerConfig, path: string, body: unknown): Promise<Record<string, unknown>> {
  const res = await fetcher(path, {
    method: "POST",
    headers: workerHeaders(cfg, uuid()),
    body: JSON.stringify(body),
  });
  const data = (await res.json()) as Record<string, unknown>;
  if (!res.ok) {
    throw new Error(`${path} failed: ${res.status} ${JSON.stringify(data)}`);
  }
  return data;
}

/**
 * Run one full investigation cycle for the next eligible job.
 * Returns the jobId if a job was processed, or null if there was no work.
 */
export async function runOneCycle(
  fetcher: Fetcher,
  cfg: WorkerConfig,
  opts: { disposition?: string; onStage?: (stage: string) => void } = {},
): Promise<string | null> {
  const c = await claim(fetcher, cfg);
  if (!c.claimed || !c.jobId) return null;
  const lease = { leaseId: c.leaseId, generation: c.generation };

  opts.onStage?.("claimed");
  await post(fetcher, cfg, `/api/v1/worker/jobs/${c.jobId}/start`, lease);
  opts.onStage?.("running");

  // (In M3+ the real worker performs governed Ollama research here.)
  await post(fetcher, cfg, `/api/v1/worker/jobs/${c.jobId}/complete`, {
    ...lease,
    disposition: opts.disposition ?? "not_determined",
  });
  opts.onStage?.("completed");
  return c.jobId;
}
