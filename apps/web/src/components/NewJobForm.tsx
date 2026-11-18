import { useState } from "react";
import { api, ApiError } from "../api.js";

export function NewJobForm({ onCreated }: { onCreated: (jobId: string) => void }) {
  const [question, setQuestion] = useState("");
  const [evidencePolicy, setEvidencePolicy] = useState("hybrid-governed");
  const [executionProfile, setExecutionProfile] = useState("standard");
  const [priority, setPriority] = useState("normal");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      const { jobId } = await api.createJob({
        question,
        evidencePolicy,
        executionProfile,
        priority,
        visibility: "private",
      });
      setQuestion("");
      onCreated(jobId);
    } catch (err) {
      setError(err instanceof ApiError ? `${err.code}: ${err.message}` : "Failed to submit");
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="panel" onSubmit={submit} aria-label="New investigation">
      <h2>New investigation</h2>
      <label htmlFor="question">Research question</label>
      <textarea
        id="question"
        value={question}
        onChange={(e) => setQuestion(e.target.value)}
        maxLength={4000}
        required
        placeholder="e.g. Compare SQL Server and PostgreSQL for OLTP"
      />
      <div className="row">
        <div>
          <label htmlFor="profile">Profile</label>
          <select id="profile" value={executionProfile} onChange={(e) => setExecutionProfile(e.target.value)}>
            <option value="quick">quick</option>
            <option value="standard">standard</option>
            <option value="extended">extended</option>
          </select>
        </div>
        <div>
          <label htmlFor="priority">Priority</label>
          <select id="priority" value={priority} onChange={(e) => setPriority(e.target.value)}>
            <option value="low">low</option>
            <option value="normal">normal</option>
            <option value="high">high</option>
          </select>
        </div>
        <div>
          <label htmlFor="policy">Evidence policy</label>
          <select id="policy" value={evidencePolicy} onChange={(e) => setEvidencePolicy(e.target.value)}>
            <option value="hybrid-governed">hybrid-governed</option>
            <option value="local-only">local-only</option>
            <option value="approved-sources-only">approved-sources-only</option>
          </select>
        </div>
      </div>
      {error && <p className="error" role="alert">{error}</p>}
      <div style={{ marginTop: 12 }}>
        <button type="submit" disabled={busy || question.trim().length === 0}>
          {busy ? "Submitting…" : "Submit investigation"}
        </button>
      </div>
    </form>
  );
}
