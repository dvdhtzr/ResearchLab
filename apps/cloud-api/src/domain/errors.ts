// Machine-readable error contract (spec §14). Never leak SQL, stack traces, or secrets.

export const ERROR_CODES = {
  INVALID_SCHEMA: { status: 400, retryable: false },
  UNSUPPORTED_VERSION: { status: 400, retryable: false },
  UNAUTHENTICATED: { status: 401, retryable: false },
  UNAUTHORIZED: { status: 403, retryable: false },
  NOT_FOUND: { status: 404, retryable: false },
  STATE_CONFLICT: { status: 409, retryable: false },
  STALE_LEASE: { status: 409, retryable: false },
  STALE_EXECUTION_GENERATION: { status: 409, retryable: false },
  IDEMPOTENCY_CONFLICT: { status: 409, retryable: false },
  VERSION_CONFLICT: { status: 409, retryable: false },
  BUDGET_EXHAUSTED: { status: 409, retryable: false },
  RATE_LIMITED: { status: 429, retryable: true },
  DEPENDENCY_UNAVAILABLE: { status: 503, retryable: true },
  CLOUD_PERSISTENCE_FAILURE: { status: 500, retryable: true },
} as const;

export type ErrorCode = keyof typeof ERROR_CODES;

export class ApiError extends Error {
  readonly code: ErrorCode;
  readonly status: number;
  readonly retryable: boolean;

  constructor(code: ErrorCode, message: string) {
    super(message);
    this.name = "ApiError";
    this.code = code;
    this.status = ERROR_CODES[code].status;
    this.retryable = ERROR_CODES[code].retryable;
  }

  toBody(contractVersion: string): string {
    return JSON.stringify({
      contractVersion,
      error: { code: this.code, message: this.message, retryable: this.retryable },
    });
  }
}

export function isApiError(e: unknown): e is ApiError {
  return e instanceof ApiError;
}
