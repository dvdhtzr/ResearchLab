import { ApiError } from "./errors.js";

// Strict public-report schema (spec §27). Rejects unexpected fields so no private path,
// prompt, credential, or raw agent trace can ride along in a published artifact.
export const PUBLIC_REPORT_SCHEMA_VERSION = "public-report.v1";
export const PUBLIC_REPORT_MAX_BYTES = 128 * 1024;

const TOP_LEVEL_KEYS = new Set([
  "schemaVersion",
  "reportVersion",
  "question",
  "executiveSummary",
  "findings",
  "challenges",
  "remainingUncertainties",
  "disposition",
  "limitations",
]);
const FINDING_KEYS = new Set(["id", "statement", "evidence", "limitations"]);
const CHALLENGE_KEYS = new Set(["target", "classification", "explanation"]);
const DISPOSITIONS = new Set(["supported", "mixed", "inconclusive", "not_determined"]);

function assert(cond: unknown, message: string): asserts cond {
  if (!cond) throw new ApiError("INVALID_SCHEMA", message);
}

function onlyKeys(obj: Record<string, unknown>, allowed: Set<string>, where: string) {
  for (const key of Object.keys(obj)) {
    if (!allowed.has(key)) throw new ApiError("INVALID_SCHEMA", `Disallowed field '${key}' in ${where}.`);
  }
}

function isObject(v: unknown): v is Record<string, unknown> {
  return !!v && typeof v === "object" && !Array.isArray(v);
}

function str(v: unknown, field: string, min: number, max: number): string {
  assert(typeof v === "string" && v.length >= min && v.length <= max, `${field} must be a string (${min}-${max} chars).`);
  return v as string;
}

/** Validates the public report structure. Throws ApiError INVALID_SCHEMA on any deviation. */
export function validatePublicReport(parsed: unknown): void {
  assert(isObject(parsed), "Report must be a JSON object.");
  const o = parsed as Record<string, unknown>;
  onlyKeys(o, TOP_LEVEL_KEYS, "public report");

  assert(o.schemaVersion === PUBLIC_REPORT_SCHEMA_VERSION, `schemaVersion must be '${PUBLIC_REPORT_SCHEMA_VERSION}'.`);
  assert(typeof o.reportVersion === "number" && Number.isInteger(o.reportVersion) && o.reportVersion >= 1, "reportVersion must be a positive integer.");
  str(o.question, "question", 1, 4000);
  str(o.executiveSummary, "executiveSummary", 1, 20000);
  assert(typeof o.disposition === "string" && DISPOSITIONS.has(o.disposition), "disposition is invalid.");

  assert(Array.isArray(o.findings) && o.findings.length <= 100, "findings must be an array (<=100).");
  for (const f of o.findings as unknown[]) {
    assert(isObject(f), "each finding must be an object.");
    onlyKeys(f, FINDING_KEYS, "finding");
    str(f.id, "finding.id", 1, 64);
    str(f.statement, "finding.statement", 1, 2000);
    assert(Array.isArray(f.evidence) && f.evidence.length <= 50, "finding.evidence must be an array (<=50).");
    for (const e of f.evidence as unknown[]) str(e, "finding.evidence[]", 1, 200);
    if (f.limitations !== undefined) str(f.limitations, "finding.limitations", 0, 2000);
  }

  assert(Array.isArray(o.challenges) && o.challenges.length <= 200, "challenges must be an array (<=200).");
  for (const c of o.challenges as unknown[]) {
    assert(isObject(c), "each challenge must be an object.");
    onlyKeys(c, CHALLENGE_KEYS, "challenge");
    str(c.target, "challenge.target", 1, 64);
    str(c.classification, "challenge.classification", 1, 64);
    str(c.explanation, "challenge.explanation", 1, 2000);
  }

  if (o.remainingUncertainties !== undefined) {
    assert(Array.isArray(o.remainingUncertainties) && o.remainingUncertainties.length <= 100, "remainingUncertainties must be an array.");
    for (const u of o.remainingUncertainties as unknown[]) str(u, "remainingUncertainties[]", 1, 1000);
  }
  if (o.limitations !== undefined) str(o.limitations, "limitations", 0, 4000);
}

/** UTF-8 byte length. */
export function byteLength(text: string): number {
  return new TextEncoder().encode(text).length;
}
