import type { D1Migration } from "@cloudflare/vitest-pool-workers/config";

declare module "cloudflare:test" {
  interface ProvidedEnv {
    DB: D1Database;
    TEST_MIGRATIONS: D1Migration[];
    RMC_ENVIRONMENT: string;
    RMC_CONTRACT_VERSION: string;
    RMC_LEASE_SECONDS: string;
    RMC_DEV_AUTH_ENABLED: string;
  }
}
