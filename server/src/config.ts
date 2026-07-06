import * as dotenv from 'dotenv';
import * as fs from 'fs';
import * as os from 'os';
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
   * Default working directory for brand-new sessions that don't specify one (file edits, shell
   * commands run here). Defaults to a "workspace" folder next to the server so the agent can't
   * wander the whole filesystem by default. Set WORK_DIR to an absolute path to point it elsewhere.
   */
  workDir: string;
  /**
   * Allow-list of directories a client may pick as a new session's working directory (via the
   * folder-browsing/git-clone flow), and the boundary used to filter which past CLI sessions show
   * up in the Sessions list. Configured via a comma-separated BROWSE_ROOTS env var; each entry is
   * resolved to an absolute path and must exist as a directory, or it's skipped with a warning.
   * Falls back to the user's home directory if unset or if every configured entry is invalid.
   */
  browseRoots: string[];
  /**
   * Path to the JSON sidecar file storing our own per-session annotations (label, archived) that
   * the CLI's own session-store.db has no concept of. Overridable via SESSION_META_FILE (mainly so
   * tests can point it at a throwaway temp file instead of the real one).
   */
  sessionMetaFilePath: string;
  /**
   * Port for the internal control API (used by the session-control MCP server to dispatch turns
   * to other sessions on this same machine - see wsServer.ts/internalControlApi.ts design notes).
   * Always bound to 127.0.0.1 only, never exposed on the network. Defaults to PORT+1.
   */
  internalControlPort: number;
}

function resolveBrowseRoots(): string[] {
  const raw = process.env.BROWSE_ROOTS;
  const candidates = raw
    ? raw
        .split(',')
        .map((p) => p.trim())
        .filter(Boolean)
        .map((p) => path.resolve(p))
    : [os.homedir()];

  const valid = candidates.filter((p) => {
    try {
      return fs.statSync(p).isDirectory();
    } catch {
      console.warn(`[config] BROWSE_ROOTS entry does not exist or is not a directory, skipping: ${p}`);
      return false;
    }
  });

  if (valid.length === 0) {
    console.warn('[config] No valid BROWSE_ROOTS entries found; falling back to the home directory.');
    return [os.homedir()];
  }
  return valid;
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
  browseRoots: resolveBrowseRoots(),
  sessionMetaFilePath: process.env.SESSION_META_FILE
    ? path.resolve(process.env.SESSION_META_FILE)
    : path.resolve(__dirname, '..', 'data', 'session-meta.json'),
  internalControlPort: parseInt(process.env.INTERNAL_CONTROL_PORT ?? '', 10) || parseInt(process.env.PORT ?? '5219', 10) + 1,
};
