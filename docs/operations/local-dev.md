# Local development

Everything in M0/M1 runs locally with **Node only** — no Cloudflare account, no GitHub OAuth, no
Ollama, no network.

## Cloud API (authoritative)

```bash
cd apps/cloud-api
npm install
npm test          # M0 contract + state-machine + concurrency + failure-injection suite
npm run typecheck # tsc --noEmit
npm run dev       # wrangler dev — local Worker + local D1 (Miniflare)
```

The test suite uses `@cloudflare/vitest-pool-workers`, which runs the tests inside `workerd` with a
real **local** D1 database (Miniflare-backed SQLite). Migrations in `migrations/` are applied
automatically by `tests/apply-migrations.ts`.

### D1 migrations

```bash
# apply migrations to the local dev database
npx wrangler d1 migrations apply rmc-local --local
```

## Secrets & auth (local)

- Local dev uses a **deterministic fake owner identity**, enabled only when `RMC_DEV_AUTH_ENABLED=true`
  **and** the environment is `local`. Production refuses to start with dev-auth enabled.
- Real secrets (owner allowlist, worker verifiers, OAuth client) are provided via `.dev.vars`
  (gitignored) locally and Cloudflare secrets in production. Never commit them. See
  `.dev.vars.example`.

## PowerShell operations scripts

`scripts/*.ps1` wrap the above for Windows (`setup`, `start`, `stop`, `check`, `check-ollama`,
`smoke`, `check-security`). They are the canonical operator entry points per the spec; on a
non-Windows dev box, run the `npm` commands directly as shown above.
