-- Local worker SQLite schema (spec §12). Authoritative for actual execution,
-- durable journals, evidence snapshots, checkpoints, and the outbound outbox.
-- WAL mode; foreign keys on; unique operation identities for idempotency.
--
-- STATUS: design artifact. Authored in a Linux container WITHOUT .NET/SQLite tooling,
-- so it is NOT yet applied or verified against a live SQLite runtime. The M2
-- migration test (disposable-database apply) is the acceptance gate.

PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;

-- Each attempt to execute a job under one fenced generation.
CREATE TABLE execution_attempts (
  attempt_id            TEXT PRIMARY KEY,
  job_id                TEXT NOT NULL,
  execution_generation  INTEGER NOT NULL,
  lease_id              TEXT NOT NULL,
  started_at            INTEGER NOT NULL,
  ended_at              INTEGER NULL,
  outcome               TEXT NULL,          -- completed | failed | interrupted | cancelled
  UNIQUE (job_id, execution_generation)
);

-- Durable operation journal: one row reserved BEFORE every model/tool invocation,
-- so an interrupted call is always recorded (never reconstructed or invented).
CREATE TABLE operation_journal (
  operation_id          TEXT PRIMARY KEY,
  job_id                TEXT NOT NULL,
  execution_generation  INTEGER NOT NULL,
  operation_type        TEXT NOT NULL,      -- inference | tool
  agent_role            TEXT NULL,
  workflow_stage        TEXT NULL,
  reservation_id        TEXT NULL,          -- budget reservation
  input_digest          TEXT NULL,
  model_or_tool_version TEXT NULL,
  started_at            INTEGER NOT NULL,
  completed_at          INTEGER NULL,
  outcome               TEXT NULL,          -- committed | model_unavailable | digest_mismatch |
                                            -- timeout | cancelled | transport_error | invalid_json |
                                            -- invalid_schema | budget_exhausted | interrupted_unknown
  error_category        TEXT NULL,
  result_digest         TEXT NULL,
  UNIQUE (job_id, execution_generation, operation_type, input_digest)
);

-- Inference attempt reservations (survive a crash; INV-08).
CREATE TABLE inference_reservations (
  reservation_id        TEXT PRIMARY KEY,
  job_id                TEXT NOT NULL,
  execution_generation  INTEGER NOT NULL,
  model_id              TEXT NOT NULL,
  model_digest          TEXT NOT NULL,
  reserved_at           INTEGER NOT NULL,
  released_at           INTEGER NULL,
  consumed              INTEGER NOT NULL DEFAULT 0
);

-- Tool execution reservations.
CREATE TABLE tool_reservations (
  reservation_id        TEXT PRIMARY KEY,
  job_id                TEXT NOT NULL,
  execution_generation  INTEGER NOT NULL,
  tool_name             TEXT NOT NULL,
  reserved_at           INTEGER NOT NULL,
  released_at           INTEGER NULL,
  consumed              INTEGER NOT NULL DEFAULT 0
);

-- Validated workflow checkpoints (recoverable only after the SQLite tx commits).
CREATE TABLE workflow_checkpoints (
  checkpoint_id         TEXT PRIMARY KEY,
  job_id                TEXT NOT NULL,
  execution_generation  INTEGER NOT NULL,
  workflow_stage        TEXT NOT NULL,
  round                 INTEGER NOT NULL,
  journal_cursor        TEXT NOT NULL,      -- operation_journal position
  evidence_snapshot_ids TEXT NOT NULL,      -- JSON array
  committed_outputs     TEXT NOT NULL,      -- JSON
  resource_accounting   TEXT NOT NULL,      -- JSON snapshot
  model_prompt_manifest TEXT NOT NULL,      -- JSON
  schema_version        TEXT NOT NULL,
  integrity_digest      TEXT NOT NULL,      -- SHA-256 over the checkpoint body
  created_at            INTEGER NOT NULL
);
CREATE INDEX ix_checkpoints_job ON workflow_checkpoints (job_id, execution_generation, round);

-- Immutable evidence snapshots and their bounded passages.
CREATE TABLE evidence_snapshots (
  snapshot_id           TEXT PRIMARY KEY,
  job_id                TEXT NOT NULL,
  source_identity       TEXT NOT NULL,
  retrieval_url         TEXT NULL,
  retrieved_at          INTEGER NOT NULL,
  content_digest        TEXT NOT NULL,
  rights_metadata       TEXT NULL,
  provenance            TEXT NOT NULL,
  retrieval_outcome     TEXT NOT NULL,
  UNIQUE (job_id, content_digest)
);
CREATE TABLE evidence_passages (
  passage_id            TEXT PRIMARY KEY,
  snapshot_id           TEXT NOT NULL,
  ordinal               INTEGER NOT NULL,
  text                  TEXT NOT NULL,      -- bounded extracted text
  FOREIGN KEY (snapshot_id) REFERENCES evidence_snapshots (snapshot_id),
  UNIQUE (snapshot_id, ordinal)
);

-- Committed research artifacts (plan, findings, challenges, report, evaluation).
CREATE TABLE research_artifacts (
  artifact_id           TEXT PRIMARY KEY,
  job_id                TEXT NOT NULL,
  execution_generation  INTEGER NOT NULL,
  artifact_type         TEXT NOT NULL,      -- ResearchPlanV1 | ResearchFindingsV1 | ...
  schema_version        TEXT NOT NULL,
  content_json          TEXT NOT NULL,
  content_digest        TEXT NOT NULL,
  created_at            INTEGER NOT NULL,
  UNIQUE (job_id, artifact_type, content_digest)
);

-- Evaluation runs (three-lane; see §26).
CREATE TABLE evaluation_runs (
  run_id                TEXT PRIMARY KEY,
  job_id                TEXT NOT NULL,
  lane                  TEXT NOT NULL,      -- technical | semantic | laboratory
  model_digest          TEXT NULL,
  prompt_version        TEXT NULL,
  protocol_version      TEXT NULL,
  result_json           TEXT NOT NULL,
  failure_categories    TEXT NULL,
  runtime_ms            INTEGER NULL,
  usage_json            TEXT NULL,
  created_at            INTEGER NOT NULL
);

-- Durable outbox: local messages persisted BEFORE transmission to the cloud (§13).
CREATE TABLE outbound_events (
  outbox_id             TEXT PRIMARY KEY,
  job_id                TEXT NOT NULL,
  operation             TEXT NOT NULL,
  idempotency_key       TEXT NOT NULL,
  payload_json          TEXT NOT NULL,
  created_at            INTEGER NOT NULL,
  sent_at               INTEGER NULL,
  acked_receipt_id      TEXT NULL,
  UNIQUE (operation, idempotency_key)
);
CREATE INDEX ix_outbound_unsent ON outbound_events (sent_at);
