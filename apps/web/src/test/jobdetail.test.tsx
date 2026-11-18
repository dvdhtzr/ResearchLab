import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { JobDetail } from "../components/JobDetail.js";
import type { OwnerJobView } from "../types.js";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const job: OwnerJobView = {
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
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      const body = handler(url, init);
      return new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });
    }),
  );
}

describe("JobDetail", () => {
  it("deletes the job after confirmation and notifies the parent", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    let deleteCalled = false;
    mockFetch((url, init) => {
      if (url.endsWith(`/jobs/${job.jobId}`) && init?.method === "DELETE") {
        deleteCalled = true;
        return { deleted: true, jobId: job.jobId };
      }
      if (url.endsWith(`/jobs/${job.jobId}`)) return job; // GET
      if (url.endsWith("/events")) return { items: [] };
      if (url.endsWith("/progress")) return { items: [] };
      return {};
    });

    const onDeleted = vi.fn();
    render(<JobDetail jobId={job.jobId} onChanged={vi.fn()} onDeleted={onDeleted} />);
    await screen.findByText(/Investigation abcdef12/i);

    fireEvent.click(screen.getByRole("button", { name: /Delete/i }));
    await waitFor(() => expect(onDeleted).toHaveBeenCalled());
    expect(deleteCalled).toBe(true);
  });

  it("does not delete when the confirmation is dismissed", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(false);
    let deleteCalled = false;
    mockFetch((url, init) => {
      if (url.endsWith(`/jobs/${job.jobId}`) && init?.method === "DELETE") deleteCalled = true;
      if (url.endsWith(`/jobs/${job.jobId}`)) return job;
      if (url.endsWith("/events")) return { items: [] };
      if (url.endsWith("/progress")) return { items: [] };
      return {};
    });

    const onDeleted = vi.fn();
    render(<JobDetail jobId={job.jobId} onChanged={vi.fn()} onDeleted={onDeleted} />);
    await screen.findByText(/Investigation abcdef12/i);

    fireEvent.click(screen.getByRole("button", { name: /Delete/i }));
    expect(deleteCalled).toBe(false);
    expect(onDeleted).not.toHaveBeenCalled();
  });

  it("reveals the live verbose progress stream only when toggled on", async () => {
    mockFetch((url) => {
      if (url.endsWith(`/jobs/${job.jobId}`)) return job;
      if (url.endsWith("/events")) return { items: [] };
      if (url.endsWith("/progress")) {
        return {
          items: [
            { sequence: 1, executionGeneration: 1, stage: "plan", level: "info", message: "planned 2 queries", detail: { queries: ["PostgreSQL", "MySQL"] }, at: new Date().toISOString() },
            { sequence: 2, executionGeneration: 1, stage: "search", level: "detail", message: "wikipedia: 3 hits", detail: {}, at: new Date().toISOString() },
          ],
        };
      }
      return {};
    });

    render(<JobDetail jobId={job.jobId} onChanged={vi.fn()} onDeleted={vi.fn()} />);
    await screen.findByText(/Investigation abcdef12/i);

    // Hidden until the toggle is on.
    expect(screen.queryByText(/planned 2 queries/)).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("checkbox", { name: /Verbose/i }));

    expect(await screen.findByText(/planned 2 queries/)).toBeInTheDocument();
    expect(screen.getByText(/wikipedia: 3 hits/)).toBeInTheDocument();
  });
});
