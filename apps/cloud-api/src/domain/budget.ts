import type { ExecutionProfile } from "./states.js";

// Server-controlled resource budgets (spec §25). The client never supplies these.
export interface BudgetSnapshot {
  modelAttempts: number;
  toolExecutions: number;
  researchRounds: number;
  activeDurationSeconds: number;
  maxSimultaneousInference: number;
}

const PROFILE_BUDGETS: Record<ExecutionProfile, BudgetSnapshot> = {
  quick: {
    modelAttempts: 8,
    toolExecutions: 15,
    researchRounds: 1,
    activeDurationSeconds: 15 * 60,
    maxSimultaneousInference: 1,
  },
  standard: {
    modelAttempts: 24,
    toolExecutions: 50,
    researchRounds: 3,
    activeDurationSeconds: 40 * 60,
    maxSimultaneousInference: 1,
  },
  extended: {
    modelAttempts: 48,
    toolExecutions: 100,
    researchRounds: 5,
    activeDurationSeconds: 90 * 60,
    maxSimultaneousInference: 1,
  },
};

export function budgetForProfile(profile: ExecutionProfile): BudgetSnapshot {
  return { ...PROFILE_BUDGETS[profile] };
}
