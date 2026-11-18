-- Migration 0002 — Authentication tables.
-- Never store plaintext bearer credentials or OAuth client secrets.

-- Owner application sessions (opaque token; only its hash is stored).
CREATE TABLE owner_sessions (
  session_id        TEXT    PRIMARY KEY,
  session_hash      TEXT    NOT NULL,
  github_account_id TEXT    NOT NULL,
  created_at        INTEGER NOT NULL,
  expires_at        INTEGER NOT NULL,
  revoked_at        INTEGER NULL,
  last_seen_at      INTEGER NULL,
  CHECK (length(session_hash) > 0 AND length(github_account_id) > 0)
);
CREATE INDEX ix_owner_sessions_expires ON owner_sessions (expires_at);
CREATE INDEX ix_owner_sessions_account ON owner_sessions (github_account_id);

-- In-flight OAuth authorization-code flows (state + PKCE verifier, server-side).
CREATE TABLE oauth_flows (
  flow_id        TEXT    PRIMARY KEY,
  state_hash     TEXT    NOT NULL,
  pkce_verifier  TEXT    NOT NULL,
  redirect_uri   TEXT    NOT NULL,
  created_at     INTEGER NOT NULL,
  expires_at     INTEGER NOT NULL,
  consumed_at    INTEGER NULL,
  CHECK (length(state_hash) > 0 AND length(pkce_verifier) > 0)
);
CREATE UNIQUE INDEX ux_oauth_flows_state ON oauth_flows (state_hash);
CREATE INDEX        ix_oauth_flows_expires ON oauth_flows (expires_at);

-- Worker machine credentials (verifier only; never the plaintext secret).
CREATE TABLE worker_credentials (
  worker_id        TEXT    NOT NULL,
  credential_id    TEXT    PRIMARY KEY,
  secret_verifier  TEXT    NOT NULL,
  enabled          INTEGER NOT NULL DEFAULT 1,
  created_at       INTEGER NOT NULL,
  rotated_at       INTEGER NULL,
  revoked_at       INTEGER NULL,
  CHECK (length(worker_id) > 0 AND length(secret_verifier) > 0)
);
CREATE INDEX ix_worker_credentials_worker ON worker_credentials (worker_id, enabled);
