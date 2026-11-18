-- Migration 0003 — Publication tables (INV-09: unapproved content is never public).

-- Immutable artifact manifests describing a publication candidate's exact bytes.
CREATE TABLE artifact_manifests (
  artifact_id      TEXT    PRIMARY KEY,
  job_id           TEXT    NOT NULL,
  artifact_version INTEGER NOT NULL,
  schema_version   TEXT    NOT NULL,
  content_digest   TEXT    NOT NULL,   -- SHA-256 hex over exact UTF-8 bytes
  content_length   INTEGER NOT NULL,   -- UTF-8 byte length
  summary_json     TEXT    NOT NULL,   -- sanitized metadata only
  validation_state TEXT    NOT NULL DEFAULT 'pending',
  created_at       INTEGER NOT NULL,
  CHECK (content_length >= 0),
  CHECK (validation_state IN ('pending','valid','rejected')),
  FOREIGN KEY (job_id) REFERENCES jobs (id)
);
CREATE UNIQUE INDEX ux_artifact_manifests_digest ON artifact_manifests (content_digest);
CREATE INDEX        ix_artifact_manifests_job    ON artifact_manifests (job_id, artifact_version);

-- Owner approvals bound to an EXACT content digest.
CREATE TABLE publication_approvals (
  approval_id    TEXT    PRIMARY KEY,
  artifact_id    TEXT    NOT NULL,
  content_digest TEXT    NOT NULL,
  owner_id       TEXT    NOT NULL,
  approval_state TEXT    NOT NULL,   -- 'approved' | 'revoked'
  approved_at    INTEGER NOT NULL,
  CHECK (approval_state IN ('approved','revoked')),
  FOREIGN KEY (artifact_id) REFERENCES artifact_manifests (artifact_id)
);
CREATE INDEX ix_pub_approvals_artifact ON publication_approvals (artifact_id, approved_at);
CREATE INDEX ix_pub_approvals_digest   ON publication_approvals (content_digest);

-- Published report versions (approved bytes only).
CREATE TABLE published_reports (
  report_id             TEXT    PRIMARY KEY,
  job_id                TEXT    NOT NULL,
  report_version        INTEGER NOT NULL,
  content_digest        TEXT    NOT NULL,
  content_json          TEXT    NOT NULL,   -- approved UTF-8 JSON bytes
  publication_approval_id TEXT  NOT NULL,
  published_at          INTEGER NOT NULL,
  withdrawn_at          INTEGER NULL,
  FOREIGN KEY (job_id) REFERENCES jobs (id),
  FOREIGN KEY (publication_approval_id) REFERENCES publication_approvals (approval_id)
);
CREATE UNIQUE INDEX ux_published_reports_job_version ON published_reports (job_id, report_version);
CREATE INDEX        ix_published_reports_job         ON published_reports (job_id, published_at);
CREATE INDEX        ix_published_reports_digest      ON published_reports (content_digest);
