export interface Env {
  DB: D1Database;
  RMC_ENVIRONMENT: string;
  RMC_CONTRACT_VERSION: string;
  RMC_LEASE_SECONDS: string;
  RMC_DEV_AUTH_ENABLED: string;
}

export default {
  async fetch(_request: Request, _env: Env): Promise<Response> {
    // Router is wired up in M1. M0 exercises the domain + data layer directly.
    return new Response("ResearchMissionControl cloud-api", { status: 200 });
  },
} satisfies ExportedHandler<Env>;
