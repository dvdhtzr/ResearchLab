import { ApiError } from "./errors.js";
import {
  EVIDENCE_POLICIES,
  EXECUTION_PROFILES,
  PRIORITIES,
  VISIBILITIES,
  isRegisteredJobType,
  type EvidencePolicy,
  type ExecutionProfile,
  type Priority,
  type Visibility,
} from "./states.js";

export const CONTRACT_VERSION = "1.0";
export const QUESTION_MIN = 1;
export const QUESTION_MAX = 4000;

// Only visibilities a client may *request* at admission time.
const REQUESTABLE_VISIBILITIES: readonly Visibility[] = [
  "private",
  "public_demo_requested",
];

export interface JobRequestInput {
  question: string;
  evidencePolicy: EvidencePolicy;
}

export interface ValidatedJobCreation {
  contractVersion: "1.0";
  jobType: "research.investigation.v1";
  request: JobRequestInput;
  executionProfile: ExecutionProfile;
  priority: Priority;
  visibility: Visibility;
}

function asObject(v: unknown): Record<string, unknown> {
  if (v === null || typeof v !== "object" || Array.isArray(v)) {
    throw new ApiError("INVALID_SCHEMA", "Body must be a JSON object.");
  }
  return v as Record<string, unknown>;
}

function rejectUnknownKeys(obj: Record<string, unknown>, allowed: string[], where: string) {
  for (const key of Object.keys(obj)) {
    if (!allowed.includes(key)) {
      throw new ApiError("INVALID_SCHEMA", `Unknown property '${key}' in ${where}.`);
    }
  }
}

function oneOf<T extends string>(
  value: unknown,
  allowed: readonly T[],
  field: string,
): T {
  if (typeof value !== "string" || !(allowed as readonly string[]).includes(value)) {
    throw new ApiError("INVALID_SCHEMA", `Field '${field}' must be one of: ${allowed.join(", ")}.`);
  }
  return value as T;
}

/**
 * Validates a job-creation request body. Rejects unknown properties, enforces
 * the question length bounds (after trimming), the registered job type, the
 * contract version, and all enumerations. Never trusts owner identity, budget,
 * IDs, or timestamps from the body — those are server-controlled.
 */
export function validateJobCreation(body: unknown): ValidatedJobCreation {
  const obj = asObject(body);
  rejectUnknownKeys(
    obj,
    ["contractVersion", "jobType", "request", "executionProfile", "priority", "visibility"],
    "job creation",
  );

  if (obj.contractVersion !== CONTRACT_VERSION) {
    throw new ApiError("UNSUPPORTED_VERSION", `contractVersion must be exactly '${CONTRACT_VERSION}'.`);
  }

  if (typeof obj.jobType !== "string" || !isRegisteredJobType(obj.jobType)) {
    throw new ApiError("INVALID_SCHEMA", "jobType must be a registered workflow.");
  }

  const request = asObject(obj.request);
  rejectUnknownKeys(request, ["question", "evidencePolicy"], "request");

  const rawQuestion = request.question;
  if (typeof rawQuestion !== "string") {
    throw new ApiError("INVALID_SCHEMA", "request.question must be a string.");
  }
  const question = rawQuestion.trim();
  if (question.length < QUESTION_MIN || question.length > QUESTION_MAX) {
    throw new ApiError(
      "INVALID_SCHEMA",
      `request.question must be ${QUESTION_MIN}–${QUESTION_MAX} characters after trimming.`,
    );
  }

  const evidencePolicy = oneOf(request.evidencePolicy, EVIDENCE_POLICIES, "request.evidencePolicy");
  const executionProfile = oneOf(obj.executionProfile, EXECUTION_PROFILES, "executionProfile");
  const priority = oneOf(obj.priority, PRIORITIES, "priority");
  const visibility = oneOf(obj.visibility, REQUESTABLE_VISIBILITIES, "visibility");

  return {
    contractVersion: "1.0",
    jobType: "research.investigation.v1",
    request: { question, evidencePolicy },
    executionProfile,
    priority,
    visibility,
  };
}
