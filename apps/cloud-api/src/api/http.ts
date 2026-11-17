import { ApiError, isApiError } from "../domain/errors.js";
import { CONTRACT_VERSION } from "../data/types.js";

const SECURITY_HEADERS: Record<string, string> = {
  "content-type": "application/json",
  "cache-control": "no-store",
  "x-content-type-options": "nosniff",
};

export function json(body: unknown, status = 200, extra: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), { status, headers: { ...SECURITY_HEADERS, ...extra } });
}

export function errorResponse(e: unknown): Response {
  if (isApiError(e)) {
    return new Response(e.toBody(CONTRACT_VERSION), {
      status: e.status,
      headers: SECURITY_HEADERS,
    });
  }
  // Never leak internal detail.
  const err = new ApiError("CLOUD_PERSISTENCE_FAILURE", "An unexpected error occurred.");
  return new Response(err.toBody(CONTRACT_VERSION), { status: err.status, headers: SECURITY_HEADERS });
}

export function requireIdempotencyKey(request: Request): string {
  const key = request.headers.get("Idempotency-Key");
  if (!key || key.trim().length < 8 || key.length > 200) {
    throw new ApiError("INVALID_SCHEMA", "A valid Idempotency-Key header (8–200 chars) is required.");
  }
  return key;
}

const MAX_BODY_BYTES = 64 * 1024;

export async function readJsonBody(request: Request): Promise<unknown> {
  const text = await request.text();
  if (text.length > MAX_BODY_BYTES) {
    throw new ApiError("INVALID_SCHEMA", "Request body too large.");
  }
  if (text.trim() === "") return {};
  try {
    return JSON.parse(text);
  } catch {
    throw new ApiError("INVALID_SCHEMA", "Request body must be valid JSON.");
  }
}
