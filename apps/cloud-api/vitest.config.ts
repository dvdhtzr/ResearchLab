import path from "node:path";
import {
  defineWorkersConfig,
  readD1Migrations,
} from "@cloudflare/vitest-pool-workers/config";

// Runs tests inside workerd with a real local D1 (Miniflare-backed SQLite).
// Fully local — no Cloudflare account, no network. Satisfies M0-12.
// Migrations are read from disk here (Node context) and applied inside the
// worker by tests/apply-migrations.ts.
export default defineWorkersConfig(async () => {
  const migrations = await readD1Migrations(
    path.join(import.meta.dirname, "migrations"),
  );
  return {
    test: {
      setupFiles: ["./tests/apply-migrations.ts"],
      poolOptions: {
        workers: {
          isolatedStorage: true,
          singleWorker: true,
          miniflare: {
            compatibilityDate: "2025-04-01",
            compatibilityFlags: ["nodejs_compat"],
            d1Databases: { DB: "rmc-local" },
            bindings: {
              TEST_MIGRATIONS: migrations,
              RMC_ENVIRONMENT: "local",
              RMC_CONTRACT_VERSION: "1.0",
              RMC_LEASE_SECONDS: "180",
              RMC_DEV_AUTH_ENABLED: "true",
            },
          },
        },
      },
    },
  };
});
