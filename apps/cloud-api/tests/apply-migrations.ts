import { applyD1Migrations, env } from "cloudflare:test";

// Applies all versioned D1 migrations to the isolated per-test database before
// each test file runs. TEST_MIGRATIONS is injected by vitest.config.ts.
await applyD1Migrations(env.DB, env.TEST_MIGRATIONS);
