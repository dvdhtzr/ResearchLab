import { describe, expect, it } from "vitest";
import { DEV_WORKER_SECRET, resolveWorker } from "../src/api/auth.js";
import type { Env } from "../src/index.js";

function envOf(overrides: Partial<Env>): Env {
  return {
    RMC_ENVIRONMENT: "local",
    RMC_CONTRACT_VERSION: "1.0",
    RMC_LEASE_SECONDS: "180",
    RMC_DEV_AUTH_ENABLED: "true",
    ...overrides,
  } as unknown as Env;
}

function workerRequest(secret: string): Request {
  return new Request("https://api.local/api/v1/worker/claims", {
    method: "POST",
    headers: { authorization: `Bearer ${secret}`, "x-worker-id": "worker:alpha" },
  });
}

describe("worker auth", () => {
  it("accepts the dev secret in local mode with no configured secret", () => {
    const actor = resolveWorker(workerRequest(DEV_WORKER_SECRET), envOf({ RMC_WORKER_SHARED_SECRET: undefined }));
    expect(actor).toEqual({ type: "worker", id: "worker:alpha" });
  });

  it("rejects a wrong credential even in local mode", () => {
    expect(() => resolveWorker(workerRequest("wrong"), envOf({ RMC_WORKER_SHARED_SECRET: undefined }))).toThrow();
  });

  it("uses a configured secret when present (overrides the dev fallback)", () => {
    const env = envOf({ RMC_WORKER_SHARED_SECRET: "configured-secret" });
    expect(resolveWorker(workerRequest("configured-secret"), env)).toEqual({ type: "worker", id: "worker:alpha" });
    expect(() => resolveWorker(workerRequest(DEV_WORKER_SECRET), env)).toThrow();
  });

  it("refuses when not local and no secret is configured", () => {
    const env = envOf({ RMC_ENVIRONMENT: "production", RMC_DEV_AUTH_ENABLED: "false", RMC_WORKER_SHARED_SECRET: undefined });
    expect(() => resolveWorker(workerRequest(DEV_WORKER_SECRET), env)).toThrow();
  });
});
