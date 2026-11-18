import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { PublishedReportView } from "../components/PublishedReportView.js";
import { PublicShowcase } from "../components/PublicShowcase.js";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function respond(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

const report = {
  schemaVersion: "public-report.v1",
  reportVersion: 1,
  question: "Compare SQL Server and PostgreSQL",
  executiveSummary: "Both are capable relational databases.",
  findings: [{ id: "f1", statement: "PostgreSQL uses MVCC", evidence: ["p1"] }],
  challenges: [],
  disposition: "supported",
};

describe("PublishedReportView", () => {
  it("renders the published report's public fields", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => respond(report)));
    render(<PublishedReportView reportId="r1" />);
    expect(await screen.findByText(/Compare SQL Server and PostgreSQL/)).toBeInTheDocument();
    expect(screen.getByText(/PostgreSQL uses MVCC/)).toBeInTheDocument();
    expect(screen.getByText(/Disposition: supported/)).toBeInTheDocument();
  });

  it("shows an honest 'unavailable' message for a withdrawn/absent report", async () => {
    vi.stubGlobal("fetch", vi.fn(async () =>
      respond({ contractVersion: "1.0", error: { code: "NOT_FOUND", message: "Not found.", retryable: false } }, 404),
    ));
    render(<PublishedReportView reportId="gone" />);
    expect(await screen.findByText(/not available/i)).toBeInTheDocument();
  });

  it("does not render fields outside the public schema", async () => {
    const leaky = { ...report, internalPrompt: "SECRET SYSTEM PROMPT", localPath: "C:/secret.sqlite" };
    vi.stubGlobal("fetch", vi.fn(async () => respond(leaky)));
    render(<PublishedReportView reportId="r1" />);
    await screen.findByText(/Compare SQL Server and PostgreSQL/);
    expect(screen.queryByText(/SECRET SYSTEM PROMPT/)).toBeNull();
    expect(screen.queryByText(/secret\.sqlite/)).toBeNull();
  });
});

describe("PublicShowcase", () => {
  it("shows an honest empty state when no demos are running", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => respond({ items: [] })));
    render(<PublicShowcase />);
    expect(await screen.findByText(/No public demonstrations/i)).toBeInTheDocument();
  });

  it("shows an offline indicator when the public API is unreachable", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => respond({ error: { code: "DEPENDENCY_UNAVAILABLE" } }, 503)));
    render(<PublicShowcase />);
    await waitFor(() => expect(screen.getByRole("status").textContent).toMatch(/Offline/i));
  });
});
