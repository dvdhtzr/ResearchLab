import { useEffect, useState } from "react";
import { api, ApiError } from "../api.js";
import type { JobEventView, JobProgressView, OwnerJobView } from "../types.js";
import { StateBadge } from "./StateBadge.js";

const EVENT_LABELS: Record<string, string> = {
  admitted: "Admitted to queue",
  claimed: "Claimed by worker",
  started: "Execution started",
  completed: "Completed",
  failed: "Failed",
  cancelled_by_owner: "Cancelled",
  cancel_requested: "Cancellation requested",
  lease_expired: "Lease expired — recovery required",
  hold_requested: "Hold requested",
  hold_released: "Hold released",
  priority_changed: "Priority changed",
  recovery_restart: "Recovery: restarted",
  recovery_resume: "Recovery: resumed",
  recovery_terminated: "Recovery: terminated",
};

export function JobDetail({
  jobId,
  onChanged,
  onDeleted,
}: {
  jobId: string;
  onChanged: () => void;
  onDeleted: () => void;
}) {
  const [job, setJob] = useState<OwnerJobView | null>(null);
  const [events, setEvents] = useState<JobEventView[]>([]);
  const [progress, setProgress] = useState<JobProgressView[]>([]);
  const [verbose, setVerbose] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    async function load() {
      try {
        const [j, ev] = await Promise.all([api.getJob(jobId), api.getEvents(jobId)]);
        if (!active) return;
        setJob(j);
        setEvents(ev);
        setError(null);
        if (verbose) {
          const pr = await api.getProgress(jobId);
          if (active) setProgress(pr);
        }
      } catch (err) {
        if (active) setError(err instanceof ApiError ? err.message : "Failed to load");
      }
    }
    load();
    const t = setInterval(load, 5000);
    return () => {
      active = false;
      clearInterval(t);
    };
  }, [jobId, verbose]);

  async function control(action: string) {
    try {
      await api.control(jobId, action);
      onChanged();
    } catch (err) {
      setError(err instanceof ApiError ? `${err.code}: ${err.message}` : "Control failed");
    }
  }

  async function remove() {
    if (!window.confirm("Delete this investigation and all of its records? This cannot be undone.")) return;
    try {
      await api.deleteJob(jobId);
      onDeleted();
    } catch (err) {
      setError(err instanceof ApiError ? `${err.code}: ${err.message}` : "Delete failed");
    }
  }

  if (error && !job) return <div className="panel"><p className="error" role="alert">{error}</p></div>;
  if (!job) return <div className="panel"><p className="loading">Loading job…</p></div>;

  const report = extractReport(events);

  const canHold = job.state === "Queued" && !job.holdRequested;
  const canRelease = job.state === "Queued" && job.holdRequested;
  const canCancel = ["Queued", "Claimed", "Running"].includes(job.state);
  const running = ["Claimed", "Running"].includes(job.state);

  return (
    <div className="panel" aria-label="Job detail">
      <h2>Investigation {job.jobId.slice(0, 8)}</h2>
      <div className="row" style={{ alignItems: "center", marginBottom: 8 }}>
        <StateBadge state={job.state} />
        <span className="eat">gen {job.executionGeneration} · v{job.stateVersion} · {job.priority}</span>
      </div>
      {job.currentStage && <p className="eat">Stage: {job.currentStage}</p>}
      {job.researchDisposition && <p className="eat">Disposition: {job.researchDisposition}</p>}

      <div className="row" style={{ marginBottom: 12, alignItems: "center", flexWrap: "wrap" }}>
        <button className="secondary" disabled={!canHold} onClick={() => control("hold")}>Hold</button>
        <button className="secondary" disabled={!canRelease} onClick={() => control("release")}>Release</button>
        <button className="secondary" disabled={!canCancel} onClick={() => control("request_cancel")}>Cancel</button>
        <button className="danger" onClick={remove}>Delete</button>
        <label className="toggle" title="Show the worker's detailed live progress stream">
          <input type="checkbox" checked={verbose} onChange={(e) => setVerbose(e.target.checked)} />
          <span>Verbose</span>
        </label>
      </div>

      {error && job && <p className="error" role="alert">{error}</p>}

      {verbose && (
        <section aria-label="Live progress" style={{ marginBottom: 16 }}>
          <h2>Live progress {running ? "· running" : ""}</h2>
          {progress.length === 0 ? (
            <p className="empty">No verbose progress recorded yet{running ? " — waiting for the worker…" : "."}</p>
          ) : (
            <ol className="timeline progress">
              {progress.map((p) => (
                <li key={p.sequence}>
                  <div className="etype">
                    <span className={`stage stage-${p.stage}`}>{p.stage}</span>{" "}
                    {p.message}
                    {p.level === "detail" && <span className="eat"> · detail</span>}
                  </div>
                  <div className="eat">{new Date(p.at).toLocaleTimeString()}</div>
                  {hasDetail(p.detail) && (
                    <pre className="detail">{JSON.stringify(p.detail, null, 2)}</pre>
                  )}
                </li>
              ))}
            </ol>
          )}
        </section>
      )}

      {report && (
        <section aria-label="Research report" style={{ marginBottom: 16 }}>
          <h2>Research report</h2>
          <p>{report.summary}</p>
          {report.findings.length > 0 && (
            <ul className="jobs">
              {report.findings.map((f, i) => (
                <li key={i}><div className="q">{f}</div></li>
              ))}
            </ul>
          )}
          {report.findings.length === 0 && (
            <p className="empty">
              No evidence-grounded findings for this question{report.reason ? ` (${report.reason})` : ""}. The result is
              honestly reported as {job.researchDisposition ?? "inconclusive"} rather than fabricated.
            </p>
          )}
        </section>
      )}

      <h2>Timeline</h2>
      {events.length === 0 ? (
        <p className="empty">No activity recorded.</p>
      ) : (
        <ol className="timeline">
          {events.map((e) => (
            <li key={e.sequence}>
              <div className="etype">{EVENT_LABELS[e.eventType] ?? e.eventType}</div>
              <div className="eat">
                {e.fromState ?? "—"} → {e.toState} · {e.actorType} · {new Date(e.at).toLocaleString()}
              </div>
            </li>
          ))}
        </ol>
      )}
    </div>
  );
}

function hasDetail(detail: unknown): boolean {
  return !!detail && typeof detail === "object" && Object.keys(detail as object).length > 0;
}

interface ReportView {
  summary: string;
  findings: string[];
  reason?: string;
}

// Pulls the owner-visible research report out of the completed event's details and
// normalizes findings (strings or {statement} objects) for display.
function extractReport(events: JobEventView[]): ReportView | null {
  const completed = events.find((e) => e.eventType === "completed");
  const raw = (completed?.details as { report?: unknown } | undefined)?.report;
  if (!raw || typeof raw !== "object") return null;
  const r = raw as Record<string, unknown>;
  const findingsRaw = Array.isArray(r.findings) ? r.findings : [];
  const findings = findingsRaw
    .map((f) =>
      typeof f === "string"
        ? f
        : f && typeof f === "object" && "statement" in f
          ? String((f as Record<string, unknown>).statement)
          : "",
    )
    .filter((s) => s.length > 0);
  return {
    summary: typeof r.summary === "string" ? r.summary : "",
    findings,
    reason: typeof r.reason === "string" ? r.reason : undefined,
  };
}
