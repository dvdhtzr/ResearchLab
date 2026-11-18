import type { OwnerJobView } from "../types.js";
import { StateBadge } from "./StateBadge.js";

export function JobsList({
  jobs,
  selectedId,
  onSelect,
  loading,
}: {
  jobs: OwnerJobView[];
  selectedId: string | null;
  onSelect: (id: string) => void;
  loading: boolean;
}) {
  return (
    <div className="panel">
      <h2>Jobs</h2>
      {loading && jobs.length === 0 && <p className="loading">Loading…</p>}
      {!loading && jobs.length === 0 && <p className="empty">No investigations yet.</p>}
      <ul className="jobs">
        {jobs.map((j) => (
          <li
            key={j.jobId}
            className={j.jobId === selectedId ? "selected" : ""}
            onClick={() => onSelect(j.jobId)}
            tabIndex={0}
            role="button"
            aria-pressed={j.jobId === selectedId}
            onKeyDown={(e) => (e.key === "Enter" || e.key === " ") && onSelect(j.jobId)}
          >
            <div className="q">{j.jobId.slice(0, 8)} · {j.executionProfile}</div>
            <div className="meta">
              <StateBadge state={j.state} />
              <span>{j.priority}{j.researchDisposition ? ` · ${j.researchDisposition}` : ""}</span>
            </div>
          </li>
        ))}
      </ul>
    </div>
  );
}
