import * as dotenv from 'dotenv';
import * as path from 'path';
dotenv.config();

export interface ServerConfig {
  port: number;
  /** Shared secret clients must send as `Authorization: Bearer <token>` */
  authToken: string;
  /** Path/command used to invoke the GitHub Copilot CLI. Defaults to "copilot" (must be on PATH). */
  copilotCommand: string;
  /** Optional model override passed to the CLI (e.g. "gpt-5.4"). Empty = let CLI decide. */
  model: string;
  /**
   * Working directory the Copilot CLI operates in (file edits, shell commands run here).
   * Defaults to a "workspace" folder next to the server so the agent can't wander the whole filesystem
   * by default. Set WORK_DIR to an absolute path to point it elsewhere.
   */
  workDir: string;
}

function requireEnv(name: string, fallback?: string): string {
  const value = process.env[name] ?? fallback;
  if (value === undefined) {
    throw new Error(`Missing required environment variable: ${name}`);
  }
  return value;
}

/** Minimum acceptable length for AUTH_TOKEN, to reject empty/trivial shared secrets at startup. */
const MIN_AUTH_TOKEN_LENGTH = 16;

function requireAuthToken(): string {
  const value = requireEnv('AUTH_TOKEN');
  if (value.trim().length < MIN_AUTH_TOKEN_LENGTH) {
    throw new Error(
      `AUTH_TOKEN must be at least ${MIN_AUTH_TOKEN_LENGTH} characters long (got ${value.trim().length}). ` +
        `Set a long random secret in server/.env, e.g. via: openssl rand -hex 32`,
    );
  }
  return value;
}

export const config: ServerConfig = {
  port: parseInt(process.env.PORT ?? '5219', 10),
  authToken: requireAuthToken(),
  copilotCommand: process.env.COPILOT_COMMAND ?? 'copilot',
  model: process.env.COPILOT_MODEL ?? '',
  workDir: process.env.WORK_DIR
    ? path.resolve(process.env.WORK_DIR)
    : path.resolve(__dirname, '..', 'workspace'),
};
