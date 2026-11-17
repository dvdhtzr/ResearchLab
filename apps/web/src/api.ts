import type { JobEventView, OwnerJobView } from "./types.js";

export class ApiError extends Error {
  code: string;
  constructor(code: string, message: string) {
    super(message);
    this.code = code;
  }
}

async function parse<T>(res: Response): Promise<T> {
  const text = await res.text();
  const data = text ? JSON.parse(text) : {};
  if (!res.ok) {
    const code = data?.error?.code ?? "HTTP_" + res.status;
    const message = data?.error?.message ?? `Request failed (${res.status})`;
    throw new ApiError(code, message);
  }
  return data as T;
}

function idempotencyKey(): string {
  return (globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random()}`).toString();
}

export interface NewJobInput {
  question: string;
  evidencePolicy: string;
  executionProfile: string;
  priority: string;
  visibility: string;
}

export const api = {
  async session(): Promise<{ ownerId: string; authenticated: boolean }> {
    return parse(await fetch("/api/v1/owner/session"));
  },

  async listJobs(): Promise<OwnerJobView[]> {
    const data = await parse<{ items: OwnerJobView[] }>(await fetch("/api/v1/owner/jobs"));
    return data.items;
  },

  async getJob(jobId: string): Promise<OwnerJobView> {
    return parse(await fetch(`/api/v1/owner/jobs/${jobId}`));
  },

  async getEvents(jobId: string): Promise<JobEventView[]> {
    const data = await parse<{ items: JobEventView[] }>(await fetch(`/api/v1/owner/jobs/${jobId}/events`));
    return data.items;
  },

  async createJob(input: NewJobInput): Promise<{ jobId: string }> {
    return parse(
      await fetch("/api/v1/owner/jobs", {
        method: "POST",
        headers: { "content-type": "application/json", "idempotency-key": idempotencyKey() },
        body: JSON.stringify({
          contractVersion: "1.0",
          jobType: "research.investigation.v1",
          request: { question: input.question, evidencePolicy: input.evidencePolicy },
          executionProfile: input.executionProfile,
          priority: input.priority,
          visibility: input.visibility,
        }),
      }),
    );
  },

  async control(jobId: string, action: string, priority?: string): Promise<unknown> {
    return parse(
      await fetch(`/api/v1/owner/jobs/${jobId}/control`, {
        method: "POST",
        headers: { "content-type": "application/json", "idempotency-key": idempotencyKey() },
        body: JSON.stringify({ action, priority }),
      }),
    );
  },
};

export interface PublicJobView {
  jobId: string;
  state: string;
  stage: string | null;
  disposition: string | null;
  updatedAt: string;
}

export interface PublicReportView {
  schemaVersion: string;
  reportVersion: number;
  question: string;
  executiveSummary: string;
  findings: { id: string; statement: string; evidence: string[]; limitations?: string }[];
  challenges: { target: string; classification: string; explanation: string }[];
  remainingUncertainties?: string[];
  disposition: string;
  limitations?: string;
}

export const publicApi = {
  async listJobs(): Promise<PublicJobView[]> {
    const data = await parse<{ items: PublicJobView[] }>(await fetch("/api/v1/public/jobs"));
    return data.items;
  },
  async getReport(reportId: string): Promise<PublicReportView> {
    return parse(await fetch(`/api/v1/public/reports/${reportId}`));
  },
};
