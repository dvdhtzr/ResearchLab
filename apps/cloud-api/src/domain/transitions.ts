import type { JobState } from "./states.js";
import { isTerminal } from "./states.js";

// Operations that drive authoritative state transitions (spec §8).
export type TransitionOp =
  | "claim" // Queued -> Claimed
  | "start" // Claimed -> Running
  | "lease_lost" // Claimed|Running -> RecoveryRequired
  | "startup_fail" // Claimed -> Failed
  | "complete" // Running -> Completed
  | "exec_fail" // Running -> Failed
  | "cancel_queued" // Queued -> Cancelled
  | "cancel_acked" // Claimed|Running -> Cancelled
  | "recovery_requeue" // RecoveryRequired -> Queued (resume or restart)
  | "recovery_fail" // RecoveryRequired -> Failed
  | "recovery_cancel"; // RecoveryRequired -> Cancelled

interface TransitionRule {
  op: TransitionOp;
  from: readonly JobState[];
  to: JobState;
}

// The complete, closed set of allowed transitions. Anything not listed is rejected.
export const TRANSITION_RULES: readonly TransitionRule[] = [
  { op: "claim", from: ["Queued"], to: "Claimed" },
  { op: "start", from: ["Claimed"], to: "Running" },
  { op: "lease_lost", from: ["Claimed", "Running"], to: "RecoveryRequired" },
  { op: "startup_fail", from: ["Claimed"], to: "Failed" },
  { op: "complete", from: ["Running"], to: "Completed" },
  { op: "exec_fail", from: ["Running"], to: "Failed" },
  { op: "cancel_queued", from: ["Queued"], to: "Cancelled" },
  { op: "cancel_acked", from: ["Claimed", "Running"], to: "Cancelled" },
  { op: "recovery_requeue", from: ["RecoveryRequired"], to: "Queued" },
  { op: "recovery_fail", from: ["RecoveryRequired"], to: "Failed" },
  { op: "recovery_cancel", from: ["RecoveryRequired"], to: "Cancelled" },
];

const RULE_BY_OP = new Map<TransitionOp, TransitionRule>(
  TRANSITION_RULES.map((r) => [r.op, r]),
);

export function ruleFor(op: TransitionOp): TransitionRule {
  const r = RULE_BY_OP.get(op);
  if (!r) throw new Error(`Unknown transition op: ${op}`);
  return r;
}

/** True iff `op` legally applies when the job is currently in `from`. */
export function isTransitionAllowed(op: TransitionOp, from: JobState): boolean {
  const r = RULE_BY_OP.get(op);
  return !!r && r.from.includes(from);
}

/** The resulting state for an op (independent of current state). */
export function targetState(op: TransitionOp): JobState {
  return ruleFor(op).to;
}

/** Terminal states can never be reopened (INV-07). */
export function canLeaveState(from: JobState): boolean {
  return !isTerminal(from);
}
