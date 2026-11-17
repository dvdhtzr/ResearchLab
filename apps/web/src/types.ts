export type JobState =
  | "Queued"
  | "Claimed"
  | "Running"
  | "RecoveryRequired"
  | "Completed"
  | "Failed"
  | "Cancelled";

export interface OwnerJobView {
  jobId: string;
  jobType: string;
  state: JobState;
  stateVersion: number;
  executionGeneration: number;
  priority: string;
  executionProfile: string;
  visibility: string;
  holdRequested: boolean;
  cancelRequested: boolean;
  researchDisposition: string | null;
  publicationStatus: string;
  currentStage: string | null;
  leaseExpiresAt: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface JobEventView {
  sequence: number;
  fromState: string | null;
  toState: string;
  eventType: string;
  actorType: string;
  executionGeneration: number;
  at: string;
  details: unknown;
}

export interface ApiErrorBody {
  contractVersion: string;
  error: { code: string; message: string; retryable: boolean };
}
