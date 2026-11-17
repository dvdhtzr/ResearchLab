import { useEffect, useState } from "react";
import { ApiError, publicApi, type PublicReportView } from "../api.js";

type Load =
  | { status: "loading" }
  | { status: "ok"; report: PublicReportView }
  | { status: "unavailable" }
  | { status: "error"; message: string };

// Public, read-only view of a published report. Renders ONLY the strict public schema
// fields — there is no path by which private internals could appear here. A withdrawn or
// never-published report resolves to an honest "unavailable" (404), not a blank page.
export function PublishedReportView({ reportId }: { reportId: string }) {
  const [state, setState] = useState<Load>({ status: "loading" });

  useEffect(() => {
    let active = true;
    (async () => {
      try {
        const report = await publicApi.getReport(reportId);
        if (active) setState({ status: "ok", report });
      } catch (err) {
        if (!active) return;
        if (err instanceof ApiError && err.code === "NOT_FOUND") setState({ status: "unavailable" });
        else setState({ status: "error", message: err instanceof ApiError ? err.message : "Could not load report" });
      }
    })();
    return () => {
      active = false;
    };
  }, [reportId]);

  if (state.status === "loading") return <div className="panel"><p className="loading">Loading report…</p></div>;
  if (state.status === "unavailable")
    return <div className="panel"><p className="empty">This report is not available. It may have been withdrawn or was never published.</p></div>;
  if (state.status === "error") return <div className="panel"><p className="error" role="alert">{state.message}</p></div>;

  const r = state.report;
  return (
    <article className="panel" aria-label="Published report">
      <h2>Published research report</h2>
      <h3>{r.question}</h3>
      <p className="eat">Disposition: {r.disposition} · version {r.reportVersion}</p>
      <p>{r.executiveSummary}</p>

      <h2>Findings</h2>
      {r.findings.length === 0 ? (
        <p className="empty">No findings.</p>
      ) : (
        <ul className="jobs">
          {r.findings.map((f) => (
            <li key={f.id}>
              <div className="q">{f.statement}</div>
              <div className="meta"><span>{f.id}</span><span>{f.evidence.length} evidence ref(s)</span></div>
            </li>
          ))}
        </ul>
      )}

      {r.challenges.length > 0 && (
        <>
          <h2>Challenges &amp; resolutions</h2>
          <ul className="jobs">
            {r.challenges.map((c, i) => (
              <li key={i}>
                <div className="q">{c.explanation}</div>
                <div className="meta"><span>{c.target}</span><span>{c.classification}</span></div>
              </li>
            ))}
          </ul>
        </>
      )}

      {r.remainingUncertainties && r.remainingUncertainties.length > 0 && (
        <>
          <h2>Remaining uncertainties</h2>
          <ul>{r.remainingUncertainties.map((u, i) => <li key={i}>{u}</li>)}</ul>
        </>
      )}
    </article>
  );
}
