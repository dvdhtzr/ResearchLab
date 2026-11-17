import { ApiError } from "../domain/errors.js";
import { newId, sha256Hex } from "../domain/ids.js";
import {
  PUBLIC_REPORT_MAX_BYTES,
  byteLength,
  validatePublicReport,
} from "../domain/publicReport.js";
import { CONTRACT_VERSION, type Actor } from "./types.js";

async function ownsJob(db: D1Database, ownerId: string, jobId: string): Promise<boolean> {
  const row = await db.prepare("SELECT 1 FROM jobs WHERE id = ? AND owner_id = ?").bind(jobId, ownerId).first();
  return !!row;
}

// Worker registers an artifact manifest (metadata only; content uploaded later).
export async function registerManifest(args: {
  db: D1Database;
  now: number;
  jobId: string;
  artifactVersion: number;
  schemaVersion: string;
  contentDigest: string;
  contentLength: number;
  summary: unknown;
}): Promise<{ contractVersion: string; artifactId: string; outcome: "accepted" }> {
  const { db, now, jobId, artifactVersion, schemaVersion, contentDigest, contentLength, summary } = args;

  if (!/^[0-9a-f]{64}$/.test(contentDigest)) {
    throw new ApiError("INVALID_SCHEMA", "contentDigest must be a lowercase hex SHA-256.");
  }
  if (contentLength < 0 || contentLength > PUBLIC_REPORT_MAX_BYTES) {
    throw new ApiError("INVALID_SCHEMA", `contentLength exceeds the ${PUBLIC_REPORT_MAX_BYTES}-byte limit.`);
  }
  const jobRow = await db.prepare("SELECT 1 FROM jobs WHERE id = ?").bind(jobId).first();
  if (!jobRow) throw new ApiError("NOT_FOUND", "Job not found.");

  // Idempotent by exact digest.
  const existing = await db
    .prepare("SELECT artifact_id FROM artifact_manifests WHERE content_digest = ?")
    .bind(contentDigest)
    .first<{ artifact_id: string }>();
  if (existing) {
    return { contractVersion: CONTRACT_VERSION, artifactId: existing.artifact_id, outcome: "accepted" };
  }

  const artifactId = newId();
  await db
    .prepare(
      `INSERT INTO artifact_manifests
         (artifact_id, job_id, artifact_version, schema_version, content_digest, content_length, summary_json, validation_state, created_at)
       VALUES (?,?,?,?,?,?,?, 'valid', ?)`,
    )
    .bind(artifactId, jobId, artifactVersion, schemaVersion, contentDigest, contentLength, JSON.stringify(summary ?? {}), now)
    .run();
  return { contractVersion: CONTRACT_VERSION, artifactId, outcome: "accepted" };
}

// Owner approves an EXACT content digest for publication.
export async function approveDigest(args: {
  db: D1Database;
  now: number;
  owner: Actor;
  jobId: string;
  artifactId: string;
  contentDigest: string;
}): Promise<{ contractVersion: string; approvalId: string; outcome: "accepted" }> {
  const { db, now, owner, jobId, artifactId, contentDigest } = args;
  if (!(await ownsJob(db, owner.id, jobId))) throw new ApiError("UNAUTHORIZED", "Not the owner of this job.");

  const manifest = await db
    .prepare("SELECT content_digest, job_id FROM artifact_manifests WHERE artifact_id = ?")
    .bind(artifactId)
    .first<{ content_digest: string; job_id: string }>();
  if (!manifest || manifest.job_id !== jobId) throw new ApiError("NOT_FOUND", "Manifest not found for this job.");
  if (manifest.content_digest !== contentDigest) {
    throw new ApiError("STATE_CONFLICT", "The provided digest does not match the manifest; a changed artifact needs a new manifest.");
  }

  const approvalId = newId();
  await db
    .prepare(
      `INSERT INTO publication_approvals (approval_id, artifact_id, content_digest, owner_id, approval_state, approved_at)
       VALUES (?,?,?,?, 'approved', ?)`,
    )
    .bind(approvalId, artifactId, contentDigest, owner.id, now)
    .run();
  return { contractVersion: CONTRACT_VERSION, approvalId, outcome: "accepted" };
}

export async function revokeApproval(args: {
  db: D1Database;
  now: number;
  owner: Actor;
  jobId: string;
  artifactId: string;
  contentDigest: string;
}): Promise<{ contractVersion: string; outcome: "accepted" }> {
  const { db, now, owner, jobId, artifactId, contentDigest } = args;
  if (!(await ownsJob(db, owner.id, jobId))) throw new ApiError("UNAUTHORIZED", "Not the owner of this job.");
  await db
    .prepare(
      `INSERT INTO publication_approvals (approval_id, artifact_id, content_digest, owner_id, approval_state, approved_at)
       VALUES (?,?,?,?, 'revoked', ?)`,
    )
    .bind(newId(), artifactId, contentDigest, owner.id, now)
    .run();
  return { contractVersion: CONTRACT_VERSION, outcome: "accepted" };
}

// Returns the current approval id for a digest iff its most-recent decision is 'approved'.
async function currentApprovalId(db: D1Database, contentDigest: string): Promise<string | null> {
  const row = await db
    .prepare(
      `SELECT approval_id, approval_state FROM publication_approvals
        WHERE content_digest = ? ORDER BY approved_at DESC, rowid DESC LIMIT 1`,
    )
    .bind(contentDigest)
    .first<{ approval_id: string; approval_state: string }>();
  if (!row || row.approval_state !== "approved") return null;
  return row.approval_id;
}

// Worker uploads the approved bytes. The cloud computes SHA-256 over the EXACT submitted
// bytes and publishes only if an approval for that exact digest is current (INV-09).
export async function publishContent(args: {
  db: D1Database;
  now: number;
  jobId: string;
  rawBody: string;
}): Promise<{ contractVersion: string; reportId: string; contentDigest: string; outcome: "published" }> {
  const { db, now, jobId, rawBody } = args;

  if (byteLength(rawBody) > PUBLIC_REPORT_MAX_BYTES) {
    throw new ApiError("INVALID_SCHEMA", "Report exceeds the public size limit; publication stopped.");
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(rawBody);
  } catch {
    throw new ApiError("INVALID_SCHEMA", "Report body is not valid JSON.");
  }
  // Digest over the EXACT received bytes — never re-serialized.
  const contentDigest = await sha256Hex(rawBody);

  const manifest = await db
    .prepare("SELECT artifact_id, job_id FROM artifact_manifests WHERE content_digest = ?")
    .bind(contentDigest)
    .first<{ artifact_id: string; job_id: string }>();
  if (!manifest || manifest.job_id !== jobId) {
    throw new ApiError("STATE_CONFLICT", "No manifest matches the submitted bytes for this job.");
  }

  const approvalId = await currentApprovalId(db, contentDigest);
  if (!approvalId) {
    throw new ApiError("UNAUTHORIZED", "These exact bytes have no current publication approval.");
  }

  // Strict public schema — rejects any private field leakage.
  validatePublicReport(parsed);

  // Idempotent: already published and not withdrawn.
  const already = await db
    .prepare("SELECT report_id FROM published_reports WHERE job_id = ? AND content_digest = ? AND withdrawn_at IS NULL")
    .bind(jobId, contentDigest)
    .first<{ report_id: string }>();
  if (already) {
    return { contractVersion: CONTRACT_VERSION, reportId: already.report_id, contentDigest, outcome: "published" };
  }

  const versionRow = await db
    .prepare("SELECT COALESCE(MAX(report_version), 0) + 1 AS v FROM published_reports WHERE job_id = ?")
    .bind(jobId)
    .first<{ v: number }>();
  const reportVersion = versionRow?.v ?? 1;
  const reportId = newId();

  await db.batch([
    db
      .prepare(
        `INSERT INTO published_reports
           (report_id, job_id, report_version, content_digest, content_json, publication_approval_id, published_at)
         VALUES (?,?,?,?,?,?,?)`,
      )
      .bind(reportId, jobId, reportVersion, contentDigest, rawBody, approvalId, now),
    db.prepare("UPDATE jobs SET publication_status = 'published', updated_at = ? WHERE id = ?").bind(now, jobId),
  ]);

  return { contractVersion: CONTRACT_VERSION, reportId, contentDigest, outcome: "published" };
}

export async function withdrawReport(args: {
  db: D1Database;
  now: number;
  owner: Actor;
  jobId: string;
  reportId: string;
}): Promise<{ contractVersion: string; outcome: "accepted" }> {
  const { db, now, owner, jobId, reportId } = args;
  if (!(await ownsJob(db, owner.id, jobId))) throw new ApiError("UNAUTHORIZED", "Not the owner of this job.");
  await db
    .prepare("UPDATE published_reports SET withdrawn_at = ? WHERE report_id = ? AND job_id = ? AND withdrawn_at IS NULL")
    .bind(now, reportId, jobId)
    .run();
  return { contractVersion: CONTRACT_VERSION, outcome: "accepted" };
}

// Public retrieval — approved, non-withdrawn bytes only.
export async function getPublishedReport(db: D1Database, reportId: string): Promise<unknown | null> {
  const row = await db
    .prepare("SELECT content_json FROM published_reports WHERE report_id = ? AND withdrawn_at IS NULL")
    .bind(reportId)
    .first<{ content_json: string }>();
  if (!row) return null;
  return JSON.parse(row.content_json);
}

export async function listPublications(db: D1Database, ownerId: string): Promise<unknown[]> {
  const { results } = await db
    .prepare(
      `SELECT m.artifact_id, m.job_id, m.content_digest, m.content_length, m.created_at,
              (SELECT approval_state FROM publication_approvals a WHERE a.content_digest = m.content_digest
                ORDER BY a.approved_at DESC, a.rowid DESC LIMIT 1) AS approval_state,
              (SELECT report_id FROM published_reports r WHERE r.content_digest = m.content_digest AND r.withdrawn_at IS NULL LIMIT 1) AS report_id
         FROM artifact_manifests m
         JOIN jobs j ON j.id = m.job_id
        WHERE j.owner_id = ?
        ORDER BY m.created_at DESC LIMIT 100`,
    )
    .bind(ownerId)
    .all<Record<string, unknown>>();
  return results;
}
