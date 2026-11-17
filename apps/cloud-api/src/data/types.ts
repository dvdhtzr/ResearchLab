import type {
  ExecutionProfile,
  JobState,
  Priority,
  PublicationStatus,
  ResearchDisposition,
  Visibility,
} from "../domain/states.js";

export type ActorType = "owner" | "worker" | "system";

export interface Actor {
  type: ActorType;
  id: string;
}

/** A row from the `jobs` table, typed. */
export interface JobRow {
  id: string;
  contract_version: string;
  job_type: string;
  owner_id: string;
  request_json: string;
  execution_profile: ExecutionProfile;
  budget_json: string;
  priority: Priority;
  state: JobState;
  state_version: number;
  execution_generation: number;
  worker_id: string | null;
  lease_id: string | null;
  lease_expires_at: number | null;
  hold_requested: number;
  cancel_requested: number;
  recovery_mode: string | null;
  approved_checkpoint_hash: string | null;
  research_disposition: ResearchDisposition | null;
  evaluation_status: string;
  publication_status: PublicationStatus;
  visibility: Visibility;
  current_stage: string | null;
  linked_job_id: string | null;
  admission_idempotency_key: string;
  admission_request_hash: string;
  last_mutation_id: string;
  created_at: number;
  updated_at: number;
}

/** Standard successful-mutation envelope (spec §14). */
export interface MutationSuccess {
  contractVersion: string;
  operationId: string;
  jobId: string;
  stateVersion: number;
  executionGeneration: number;
  acceptedAt: string;
  outcome: "accepted";
  [extra: string]: unknown;
}

export const CONTRACT_VERSION = "1.0";
