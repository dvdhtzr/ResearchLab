import { handle } from "./api/router.js";

export interface Env {
  DB: D1Database;
  RMC_ENVIRONMENT: string;
  RMC_CONTRACT_VERSION: string;
  RMC_LEASE_SECONDS: string;
  RMC_DEV_AUTH_ENABLED: string;
  /** Worker machine credential (secret binding / .dev.vars). Optional in some contexts. */
  RMC_WORKER_SHARED_SECRET?: string;
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    return handle(request, env);
  },
} satisfies ExportedHandler<Env>;
