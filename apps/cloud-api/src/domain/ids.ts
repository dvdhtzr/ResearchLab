// Identity and content-hash helpers. Uses Web Crypto (available in workerd).

export function newId(): string {
  return crypto.randomUUID();
}

/** SHA-256 hex over the exact UTF-8 bytes of the input string. */
export async function sha256Hex(input: string): Promise<string> {
  const bytes = new TextEncoder().encode(input);
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return [...new Uint8Array(digest)]
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}

/** SHA-256 hex over exact bytes (for digest verification of submitted artifacts). */
export async function sha256HexBytes(bytes: Uint8Array): Promise<string> {
  // Copy into a fresh ArrayBuffer so we hash exactly these bytes.
  const copy = bytes.slice();
  const digest = await crypto.subtle.digest("SHA-256", copy);
  return [...new Uint8Array(digest)]
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}

/**
 * Deterministic canonical JSON (stable key order) for request-hash computation.
 * Two semantically identical requests hash identically regardless of key order.
 */
export function canonicalJson(value: unknown): string {
  return JSON.stringify(sortDeep(value));
}

function sortDeep(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(sortDeep);
  if (value && typeof value === "object") {
    const obj = value as Record<string, unknown>;
    const out: Record<string, unknown> = {};
    for (const key of Object.keys(obj).sort()) out[key] = sortDeep(obj[key]);
    return out;
  }
  return value;
}

export async function requestHash(value: unknown): Promise<string> {
  return sha256Hex(canonicalJson(value));
}
