import type { JobState } from "../types.js";

export function StateBadge({ state }: { state: JobState | string }) {
  return (
    <span className={`badge ${state}`} aria-label={`state ${state}`}>
      {state}
    </span>
  );
}
