import { useCallback, useEffect, useRef, useState } from "react";
import { api, ApiError } from "./api.js";
import type { OwnerJobView } from "./types.js";
import { NewJobForm } from "./components/NewJobForm.js";
import { JobsList } from "./components/JobsList.js";
import { JobDetail } from "./components/JobDetail.js";

const POLL_MS = 15000; // owner dashboard interval (spec §31)

export function App() {
  const [jobs, setJobs] = useState<OwnerJobView[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [lastSync, setLastSync] = useState<number | null>(null);
  const [stale, setStale] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const inFlight = useRef(false);

  const refresh = useCallback(async () => {
    if (inFlight.current) return; // one in-flight request per resource
    inFlight.current = true;
    try {
      const items = await api.listJobs();
      setJobs(items);
      setLastSync(Date.now());
      setStale(false);
      setError(null);
    } catch (err) {
      setStale(true);
      setError(err instanceof ApiError ? `${err.code}: ${err.message}` : "Sync unavailable");
    } finally {
      setLoading(false);
      inFlight.current = false;
    }
  }, []);

  useEffect(() => {
    refresh();
    const t = setInterval(() => {
      if (document.visibilityState === "visible") refresh(); // pause when hidden
    }, POLL_MS);
    return () => clearInterval(t);
  }, [refresh]);

  const freshnessClass = error ? "freshness error" : stale ? "freshness stale" : "freshness";
  const freshnessText = error
    ? `Offline — showing last known data${lastSync ? ` from ${new Date(lastSync).toLocaleTimeString()}` : ""}`
    : lastSync
      ? `Synced ${new Date(lastSync).toLocaleTimeString()}`
      : "Syncing…";

  return (
    <div className="app">
      <header className="topbar">
        <div>
          <h1>Research Mission Control</h1>
          <div className="sub">Owner console · local</div>
        </div>
        <div className={freshnessClass} role="status">{freshnessText}</div>
      </header>

      <div className="grid">
        <div>
          <NewJobForm
            onCreated={(id) => {
              setSelected(id);
              refresh();
            }}
          />
          <JobsList jobs={jobs} selectedId={selected} onSelect={setSelected} loading={loading} />
        </div>
        <div>
          {selected ? (
            <JobDetail
              jobId={selected}
              onChanged={refresh}
              onDeleted={() => {
                setSelected(null);
                refresh();
              }}
            />
          ) : (
            <div className="panel"><p className="empty">Select an investigation to see its live timeline.</p></div>
          )}
        </div>
      </div>
    </div>
  );
}
