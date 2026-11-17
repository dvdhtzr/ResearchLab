import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError, publicApi, type PublicJobView } from "../api.js";
import { StateBadge } from "./StateBadge.js";

const POLL_MS = 60000; // public dashboard interval (spec §31)

// Public showcase of approved live demonstrations. Shows real recorded activity, and is
// honest about offline/stale state. Never fabricates activity.
export function PublicShowcase() {
  const [jobs, setJobs] = useState<PublicJobView[]>([]);
  const [loading, setLoading] = useState(true);
  const [lastSync, setLastSync] = useState<number | null>(null);
  const [offline, setOffline] = useState(false);
  const inFlight = useRef(false);

  const refresh = useCallback(async () => {
    if (inFlight.current) return;
    inFlight.current = true;
    try {
      setJobs(await publicApi.listJobs());
      setLastSync(Date.now());
      setOffline(false);
    } catch (err) {
      setOffline(true);
      void (err instanceof ApiError);
    } finally {
      setLoading(false);
      inFlight.current = false;
    }
  }, []);

  useEffect(() => {
    refresh();
    const t = setInterval(() => {
      if (document.visibilityState === "visible") refresh();
    }, POLL_MS);
    return () => clearInterval(t);
  }, [refresh]);

  return (
    <div className="app">
      <header className="topbar">
        <div>
          <h1>Research Mission Control</h1>
          <div className="sub">Public showcase</div>
        </div>
        <div className={offline ? "freshness error" : "freshness"} role="status">
          {offline
            ? `Offline — showing last known data${lastSync ? ` from ${new Date(lastSync).toLocaleTimeString()}` : ""}`
            : lastSync
              ? `Synced ${new Date(lastSync).toLocaleTimeString()}`
              : "Syncing…"}
        </div>
      </header>

      <section className="panel" aria-label="Live demonstrations">
        <h2>Live demonstrations</h2>
        {loading && jobs.length === 0 && <p className="loading">Loading…</p>}
        {!loading && jobs.length === 0 && (
          <p className="empty">No public demonstrations are running right now.</p>
        )}
        <ul className="jobs">
          {jobs.map((j) => (
            <li key={j.jobId}>
              <div className="q">{j.jobId.slice(0, 8)}{j.stage ? ` · ${j.stage}` : ""}</div>
              <div className="meta">
                <StateBadge state={j.state} />
                <span>{j.disposition ?? "in progress"}</span>
              </div>
            </li>
          ))}
        </ul>
      </section>
    </div>
  );
}
