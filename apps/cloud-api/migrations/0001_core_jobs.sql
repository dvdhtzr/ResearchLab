-- Migration 0001 — Core job orchestration tables.
-- All IDs are text UUIDs. All timestamps are UTC Unix milliseconds (INTEGER).
-- PRAGMA foreign_keys is enforced by D1 by default.

----------------------------------------------------------------------
-- jobs: authoritative job records (admission, state, lease, fencing).
----------------------------------------------------------------------
CREATE TABLE jobs (
  id                       TEXT    PRIMARY KEY,
  contract_version         TEXT    NOT NULL,
  job_type                 TEXT    NOT NULL,
  owner_id                 TEXT    NOT NULL,
  request_json             TEXT    NOT NULL,
  execution_profile        TEXT    NOT NULL,
  budget_json              TEXT    NOT NULL,
  priority                 TEXT    NOT NULL,
  state                    TEXT    NOT NULL,
  state_version            INTEGER NOT NULL,
  execution_generation     INTEGER NOT NULL,
  worker_id                TEXT    NULL,
  lease_id                 TEXT    NULL,
  lease_expires_at         INTEGER NULL,
  hold_requested           INTEGER NOT NULL DEFAULT 0,
  cancel_requested         INTEGER NOT NULL DEFAULT 0,
  recovery_mode            TEXT    NULL,
  approved_checkpoint_hash TEXT    NULL,
  research_disposition     TEXT    NULL,
  evaluation_status        TEXT    NOT NULL DEFAULT 'none',
  publication_status       TEXT    NOT NULL DEFAULT 'private',
  visibility               TEXT    NOT NULL DEFAULT 'private',
  current_stage            TEXT    NULL,
  linked_job_id            TEXT    NULL,
  admission_idempotency_key TEXT   NOT NULL,
  admission_request_hash   TEXT    NOT NULL,
  last_mutation_id         TEXT    NOT NULL,
  created_at               INTEGER NOT NULL,
  updated_at               INTEGER NOT NULL,

  -- Valid enumerated primary states.
  CHECK (state IN ('Queued','Claimed','Running','RecoveryRequired','Completed','Failed','Cancelled')),
  CHECK (priority IN ('low','normal','high')),
  CHECK (execution_profile IN ('quick','standard','extended')),
  CHECK (visibility IN ('private','public_demo_requested','public_demo')),
  CHECK (publication_status IN ('private','candidate','approved','published','withdrawn')),
  CHECK (research_disposition IS NULL OR research_disposition IN
        ('not_determined','supported','mixed','inconclusive')),
  -- Nonnegative version and generation.
  CHECK (state_version >= 0),
  CHECK (execution_generation >= 0),
  -- Nonempty identifiers.
  CHECK (length(id) > 0 AND length(owner_id) > 0 AND length(job_type) > 0),
  CHECK (length(admission_idempotency_key) > 0 AND length(last_mutation_id) > 0),
  -- A lease, if present, must carry a worker and an expiry.
  CHECK ((lease_id IS NULL AND worker_id IS NULL AND lease_expires_at IS NULL)
      OR (lease_id IS NOT NULL AND worker_id IS NOT NULL AND lease_expires_at IS NOT NULL))
);

-- One job per (owner, admission idempotency key).
CREATE UNIQUE INDEX ux_jobs_owner_admission
  ON jobs (owner_id, admission_idempotency_key);

-- Queue eligibility: deterministic priority then oldest-first. Stored priority
-- rank keeps ORDER BY index-friendly (high=0, normal=1, low=2).
CREATE INDEX ix_jobs_queue
  ON jobs (state, priority, created_at);

-- Active lease expiration scan (reconciliation).
CREATE INDEX ix_jobs_active_lease
  ON jobs (state, lease_expires_at);

-- Owner job listing.
CREATE INDEX ix_jobs_owner
  ON jobs (owner_id, created_at);

-- Public projection listing.
CREATE INDEX ix_jobs_visibility
  ON jobs (visibility, created_at);

----------------------------------------------------------------------
-- job_events: append-only authoritative audit trail (INV-04).
----------------------------------------------------------------------
CREATE TABLE job_events (
  event_id             TEXT    PRIMARY KEY,
  job_id               TEXT    NOT NULL,
  sequence             INTEGER NOT NULL,
  from_state           TEXT    NULL,
  to_state             TEXT    NOT NULL,
  event_type           TEXT    NOT NULL,
  actor_type           TEXT    NOT NULL,
  actor_id             TEXT    NOT NULL,
  execution_generation INTEGER NOT NULL,
  mutation_id          TEXT    NOT NULL,
  details_json         TEXT    NOT NULL,
  created_at           INTEGER NOT NULL,
  CHECK (actor_type IN ('owner','worker','system')),
  FOREIGN KEY (job_id) REFERENCES jobs (id)
);

-- Unique sequence per job; unique mutation per authoritative transition.
CREATE UNIQUE INDEX ux_job_events_job_sequence ON job_events (job_id, sequence);
CREATE UNIQUE INDEX ux_job_events_mutation     ON job_events (mutation_id);
CREATE INDEX        ix_job_events_job_created   ON job_events (job_id, created_at);

----------------------------------------------------------------------
-- mutation_receipts: durable idempotency receipts (INV-05).
----------------------------------------------------------------------
CREATE TABLE mutation_receipts (
  receipt_id      TEXT    PRIMARY KEY,
  actor_id        TEXT    NOT NULL,
  operation       TEXT    NOT NULL,
  idempotency_key TEXT    NOT NULL,
  request_hash    TEXT    NOT NULL,
  mutation_id     TEXT    NOT NULL,
  job_id          TEXT    NULL,
  response_json   TEXT    NOT NULL,
  accepted_at     INTEGER NOT NULL
);

-- Idempotency identity: one receipt per (actor, operation, key).
CREATE UNIQUE INDEX ux_receipts_identity
  ON mutation_receipts (actor_id, operation, idempotency_key);
