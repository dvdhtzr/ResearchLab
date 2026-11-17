import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { App } from "../App.js";
import { NewJobForm } from "../components/NewJobForm.js";
import type { OwnerJobView } from "../types.js";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const sampleJob: OwnerJobView = {
  jobId: "abcdef12-0000-0000-0000-000000000000",
  jobType: "research.investigation.v1",
  state: "Running",
  stateVersion: 3,
  executionGeneration: 1,
  priority: "normal",
  executionProfile: "standard",
  visibility: "private",
  holdRequested: false,
  cancelRequested: false,
  researchDisposition: null,
  publicationStatus: "private",
  currentStage: "plan",
  leaseExpiresAt: null,
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString(),
};

function mockFetch(handler: (url: string, init?: RequestInit) => unknown) {
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    const body = handler(url, init);
    return new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });
  }));
}

describe("App (owner dashboard)", () => {
  it("renders real authoritative jobs from the API with their state", async () => {
    mockFetch((url) => {
      if (url.endsWith("/api/v1/owner/jobs")) return { items: [sampleJob] };
      return {};
    });
    render(<App />);
    expect(await screen.findByText(/Research Mission Control/i)).toBeInTheDocument();
    expect(await screen.findByText(/abcdef12/)).toBeInTheDocument();
    expect(await screen.findByLabelText("state Running")).toBeInTheDocument();
  });

  it("shows an offline/stale indicator when sync fails", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response("{}", { status: 503 })));
    render(<App />);
    await waitFor(() => expect(screen.getByRole("status").textContent).toMatch(/Offline/i));
  });
});

describe("NewJobForm", () => {
  it("submits a job and reports the new id", async () => {
    const created = "11111111-2222-3333-4444-555555555555";
    mockFetch((url, init) => {
      if (url.endsWith("/api/v1/owner/jobs") && init?.method === "POST") return { jobId: created };
      return {};
    });
    const onCreated = vi.fn();
    render(<NewJobForm onCreated={onCreated} />);
    fireEvent.change(screen.getByLabelText(/Research question/i), {
      target: { value: "Compare SQL Server and PostgreSQL" },
    });
    fireEvent.click(screen.getByRole("button", { name: /Submit investigation/i }));
    await waitFor(() => expect(onCreated).toHaveBeenCalledWith(created));
  });

  it("surfaces an API error", async () => {
    vi.stubGlobal("fetch", vi.fn(async () =>
      new Response(JSON.stringify({ contractVersion: "1.0", error: { code: "INVALID_SCHEMA", message: "bad", retryable: false } }), { status: 400 }),
    ));
    render(<NewJobForm onCreated={vi.fn()} />);
    fireEvent.change(screen.getByLabelText(/Research question/i), { target: { value: "x" } });
    fireEvent.click(screen.getByRole("button", { name: /Submit investigation/i }));
    expect(await screen.findByRole("alert")).toHaveTextContent(/INVALID_SCHEMA/);
  });
});
