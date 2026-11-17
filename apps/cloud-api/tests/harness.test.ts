import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";

// Confirms the local D1 harness is wired correctly and migrations applied.
describe("test harness (M0-12: local D1, no cloud)", () => {
  it("has the core tables present after migrations", async () => {
    const { results } = await env.DB.prepare(
      "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name",
    ).all<{ name: string }>();
    const tables = results.map((r) => r.name);
    expect(tables).toContain("jobs");
    expect(tables).toContain("job_events");
    expect(tables).toContain("mutation_receipts");
    expect(tables).toContain("owner_sessions");
    expect(tables).toContain("artifact_manifests");
    expect(tables).toContain("published_reports");
  });

  it("enforces the owner/idempotency-key unique constraint", async () => {
    const now = Date.now();
    const insert = (id: string, key: string) =>
      env.DB.prepare(
        `INSERT INTO jobs (id, contract_version, job_type, owner_id, request_json,
           execution_profile, budget_json, priority, state, state_version,
           execution_generation, admission_idempotency_key, admission_request_hash,
           last_mutation_id, created_at, updated_at)
         VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)`,
      )
        .bind(
          id, "1.0", "research.investigation.v1", "owner-1", "{}",
          "standard", "{}", "normal", "Queued", 0, 0,
          key, "hash", "mut-" + id, now, now,
        )
        .run();

    await insert("job-a", "dup-key");
    await expect(insert("job-b", "dup-key")).rejects.toThrow();
  });
});
