import { ApiError } from "../domain/errors.js";
import type { Actor } from "../data/types.js";
import type { Env } from "../index.js";

const DEV_OWNER_ID = "github:dev-owner";

/**
 * Resolve the owner identity.
 *
 * Local development: a deterministic fake owner, allowed ONLY when
 * RMC_DEV_AUTH_ENABLED=true AND RMC_ENVIRONMENT=local. Production refuses to
 * start with dev auth enabled (see assertProductionAuthSafe), so this path is
 * impossible there. Real GitHub-OAuth session resolution arrives in M5.
 */
export function resolveOwner(request: Request, env: Env): Actor {
  if (devAuthActive(env)) {
    const override = request.headers.get("X-Dev-Owner-Id");
    return { type: "owner", id: override ?? DEV_OWNER_ID };
  }
  // Real owner-session cookie validation is implemented in M5.
  throw new ApiError("UNAUTHENTICATED", "Owner authentication is not configured.");
}

/**
 * Resolve the worker identity from a bearer machine credential. Separate from
 * owner auth. For local dev the shared secret comes from RMC_WORKER_SHARED_SECRET
 * (a .dev.vars / secret binding), never from code. Credential-store validation
 * with rotation/revocation arrives with the worker_credentials table usage.
 */
export function resolveWorker(request: Request, env: Env): Actor {
  const auth = request.headers.get("Authorization") ?? "";
  const match = /^Bearer\s+(.+)$/i.exec(auth);
  const workerId = request.headers.get("X-Worker-Id");
  const expected = env.RMC_WORKER_SHARED_SECRET;

  if (!expected) {
    throw new ApiError("DEPENDENCY_UNAVAILABLE", "Worker credential is not configured.");
  }
  if (!match || !workerId) {
    throw new ApiError("UNAUTHENTICATED", "Worker credential required.");
  }
  if (!timingSafeEqual(match[1]!, expected)) {
    throw new ApiError("UNAUTHENTICATED", "Invalid worker credential.");
  }
  return { type: "worker", id: workerId };
}

export function devAuthActive(env: Env): boolean {
  return env.RMC_DEV_AUTH_ENABLED === "true" && env.RMC_ENVIRONMENT === "local";
}

/**
 * Fail closed: production must refuse to run with development authentication.
 * Called at request entry; throws if misconfigured.
 */
export function assertProductionAuthSafe(env: Env): void {
  if (env.RMC_ENVIRONMENT !== "local" && env.RMC_DEV_AUTH_ENABLED === "true") {
    throw new ApiError(
      "DEPENDENCY_UNAVAILABLE",
      "Refusing to serve: development authentication is enabled on a non-local environment.",
    );
  }
}

function timingSafeEqual(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}
