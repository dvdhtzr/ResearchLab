// Standalone mock-worker CLI. Polls a running cloud API and drives jobs to completion.
//
//   RMC_API_BASE=http://localhost:8787 \
//   RMC_WORKER_ID=worker:alpha \
//   RMC_WORKER_SECRET=dev-worker-secret \
//   npm start --prefix apps/worker-mock
//
// No Ollama. Stops with Ctrl-C.

import { runOneCycle, type Fetcher } from "./mockWorker.ts";

const base = process.env.RMC_API_BASE ?? "http://localhost:8787";
const workerId = process.env.RMC_WORKER_ID ?? "worker:alpha";
const bearer = process.env.RMC_WORKER_SECRET ?? "dev-worker-secret";
const pollMs = Number(process.env.RMC_POLL_MS ?? "15000");

const fetcher: Fetcher = (path, init) => fetch(base + path, init);

console.log(`[mock-worker] ${workerId} polling ${base} every ${pollMs}ms`);

let running = true;
process.on("SIGINT", () => {
  running = false;
  console.log("\n[mock-worker] stopping…");
});

while (running) {
  try {
    const jobId = await runOneCycle(fetcher, { workerId, bearer }, {
      disposition: "inconclusive",
      onStage: (s) => console.log(`[mock-worker] ${s}`),
    });
    if (jobId) console.log(`[mock-worker] completed ${jobId}`);
    else await new Promise((r) => setTimeout(r, pollMs));
  } catch (e) {
    console.error(`[mock-worker] error: ${(e as Error).message}`);
    await new Promise((r) => setTimeout(r, pollMs));
  }
}
