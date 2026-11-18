import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App.js";
import { PublicShowcase } from "./components/PublicShowcase.js";
import { PublishedReportView } from "./components/PublishedReportView.js";
import "./styles.css";

// Minimal path-based routing (no router dependency):
//   /reports/:id  → public published report
//   /owner*       → owner console (authoritative administration)
//   / , /jobs     → public showcase
function Root() {
  const path = window.location.pathname;
  const reportMatch = /^\/reports\/([^/]+)$/.exec(path);
  if (reportMatch) return <PublishedReportView reportId={reportMatch[1]!} />;
  if (path.startsWith("/owner")) return <App />;
  return <PublicShowcase />;
}

const root = document.getElementById("root");
if (root) {
  createRoot(root).render(
    <StrictMode>
      <Root />
    </StrictMode>,
  );
}
