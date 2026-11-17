// Canonical job domain — primary states and orthogonal status fields (spec §8).

export const JOB_STATES = [
  "Queued",
  "Claimed",
  "Running",
  "RecoveryRequired",
  "Completed",
  "Failed",
  "Cancelled",
] as const;
export type JobState = (typeof JOB_STATES)[number];

export const TERMINAL_STATES: ReadonlySet<JobState> = new Set([
  "Completed",
  "Failed",
  "Cancelled",
]);

export function isTerminal(state: JobState): boolean {
  return TERMINAL_STATES.has(state);
}

export const PRIORITIES = ["low", "normal", "high"] as const;
export type Priority = (typeof PRIORITIES)[number];

/** Deterministic ordering rank: high wins, then normal, then low. */
export function priorityRank(p: Priority): number {
  return p === "high" ? 0 : p === "normal" ? 1 : 2;
}

export const EXECUTION_PROFILES = ["quick", "standard", "extended"] as const;
export type ExecutionProfile = (typeof EXECUTION_PROFILES)[number];

export const VISIBILITIES = [
  "private",
  "public_demo_requested",
  "public_demo",
] as const;
export type Visibility = (typeof VISIBILITIES)[number];

export const RESEARCH_DISPOSITIONS = [
  "not_determined",
  "supported",
  "mixed",
  "inconclusive",
] as const;
export type ResearchDisposition = (typeof RESEARCH_DISPOSITIONS)[number];

export const PUBLICATION_STATUSES = [
  "private",
  "candidate",
  "approved",
  "published",
  "withdrawn",
] as const;
export type PublicationStatus = (typeof PUBLICATION_STATUSES)[number];

export const RECOVERY_MODES = ["resume", "restart", "terminate"] as const;
export type RecoveryMode = (typeof RECOVERY_MODES)[number];

export const EVIDENCE_POLICIES = [
  "hybrid-governed",
  "local-only",
  "approved-sources-only",
] as const;
export type EvidencePolicy = (typeof EVIDENCE_POLICIES)[number];

/** Registered workflow job types. */
export const REGISTERED_JOB_TYPES = ["research.investigation.v1"] as const;
export type JobType = (typeof REGISTERED_JOB_TYPES)[number];

export function isRegisteredJobType(v: string): v is JobType {
  return (REGISTERED_JOB_TYPES as readonly string[]).includes(v);
}
