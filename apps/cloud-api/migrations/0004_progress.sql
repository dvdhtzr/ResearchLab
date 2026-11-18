-- Migration 0004 — Verbose per-job progress stream (diagnostic, owner-visible).
-- Separate from the authoritative append-only job_events audit (INV-04): this is a
-- detailed, NON-authoritative progress log the worker emits during execution so the owner
-- can watch a run in fine detail. Private to the owner; never part of public projections.
CREATE TABLE job_progress (
  progress_id          TEXT    PRIMARY KEY,
  job_id               TEXT    NOT NULL,
  sequence             INTEGER NOT NULL,
  execution_generation INTEGER NOT NULL,
  stage                TEXT    NOT NULL,                 -- plan | search | acquire | research | skeptic | report | ...
  level                TEXT    NOT NULL DEFAULT 'info',  -- info | detail
  message              TEXT    NOT NULL,
  detail_json          TEXT    NOT NULL DEFAULT '{}',
  created_at           INTEGER NOT NULL,
  CHECK (level IN ('info','detail')),
  CHECK (length(stage) > 0),
  FOREIGN KEY (job_id) REFERENCES jobs (id)
);

-- Ordered read of a job's progress stream.
CREATE UNIQUE INDEX ux_job_progress_job_sequence ON job_progress (job_id, sequence);
