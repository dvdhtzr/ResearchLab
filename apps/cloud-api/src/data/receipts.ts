import { ApiError } from "../domain/errors.js";
import type { Actor } from "./types.js";

interface ReceiptRow {
  request_hash: string;
  response_json: string;
  mutation_id: string;
}

/**
 * Resolves an idempotency receipt for (actor, operation, key).
 * - Returns the stored response when a receipt exists and the request hash matches (replay).
 * - Throws IDEMPOTENCY_CONFLICT when a receipt exists with a different payload hash.
 * - Returns null when no receipt exists yet.
 *
 * Used both before attempting a mutation (fast replay path) and after a
 * zero-row conditional update (to detect a concurrent duplicate that already won).
 */
export async function resolveReceipt(
  db: D1Database,
  actor: Actor,
  operation: string,
  idempotencyKey: string,
  requestHash: string,
): Promise<unknown | null> {
  const row = await db
    .prepare(
      `SELECT request_hash, response_json, mutation_id
         FROM mutation_receipts
        WHERE actor_id = ? AND operation = ? AND idempotency_key = ?`,
    )
    .bind(actor.id, operation, idempotencyKey)
    .first<ReceiptRow>();

  if (!row) return null;
  if (row.request_hash !== requestHash) {
    throw new ApiError(
      "IDEMPOTENCY_CONFLICT",
      "This idempotency key was already used with a different request payload.",
    );
  }
  return JSON.parse(row.response_json);
}
