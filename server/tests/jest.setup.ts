// Runs before any test file is loaded. `config.ts` throws if AUTH_TOKEN is missing, and several
// modules (e.g. wsServer.ts) import it at the top level, so the env var must be set before those
// modules get required by the test files.
process.env.AUTH_TOKEN = process.env.AUTH_TOKEN ?? 'test-only-token-not-a-secret';
