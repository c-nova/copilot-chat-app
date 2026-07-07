import * as dotenv from 'dotenv';
import * as fs from 'fs';
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
   * Falls back to just `workDir` if unset or if every configured entry is invalid - NOT the home
   * directory: this app has always been scoped to specific working directories (see README/USAGE),
   * and defaulting to the whole home directory would surface every Copilot CLI session ever run on
   * the machine (including ones from completely unrelated tools) in this app's Sessions list.
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
  /**
   * EXPERIMENTAL: optional ntfy (https://ntfy.sh) topic to push a best-effort notification to
   * whenever a chat turn finishes - lets you know Copilot replied without needing the client app
   * open, without this server ever needing its own Apple/Google push credentials (ntfy's own
   * server forwards to APNs/FCM on your behalf). Empty/unset = feature disabled entirely (default).
   * Treat the topic name like a password - anyone who knows it can publish to/read it on a public
   * ntfy.sh server, so use a long random string, not something guessable.
   */
  ntfyTopic: string;
  /** ntfy server base URL. Defaults to the public https://ntfy.sh; override to use a self-hosted instance. */
  ntfyServer: string;
}

function resolveBrowseRoots(fallbackRoot: string): string[] {
  const raw = process.env.BROWSE_ROOTS;
  const candidates = raw
    ? raw
        .split(',')
        .map((p) => p.trim())
        .filter(Boolean)
        .map((p) => path.resolve(p))
    : [fallbackRoot];

  const valid = candidates.filter((p) => {
    try {
      return fs.statSync(p).isDirectory();
    } catch {
      console.warn(`[config] BROWSE_ROOTS entry does not exist or is not a directory, skipping: ${p}`);
      return false;
    }
  });

  if (valid.length === 0) {
    console.warn(`[config] No valid BROWSE_ROOTS entries found; falling back to: ${fallbackRoot}`);
    return [fallbackRoot];
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

const resolvedWorkDir = process.env.WORK_DIR
  ? path.resolve(process.env.WORK_DIR)
  : path.resolve(__dirname, '..', 'workspace');

function resolveInternalControlPort(): number {
  const raw = process.env.INTERNAL_CONTROL_PORT;
  if (raw !== undefined && raw !== '') {
    const parsed = parseInt(raw, 10);
    // Deliberately not `parsed || fallback` - that would treat an explicit "0" (ephemeral port,
    // used by tests) as falsy and silently ignore it in favor of the default.
    if (!Number.isNaN(parsed)) return parsed;
  }
  return parseInt(process.env.PORT ?? '5219', 10) + 1;
}

export const config: ServerConfig = {
  port: parseInt(process.env.PORT ?? '5219', 10),
  authToken: requireAuthToken(),
  copilotCommand: process.env.COPILOT_COMMAND ?? 'copilot',
  model: process.env.COPILOT_MODEL ?? '',
  workDir: resolvedWorkDir,
  browseRoots: resolveBrowseRoots(resolvedWorkDir),
  sessionMetaFilePath: process.env.SESSION_META_FILE
    ? path.resolve(process.env.SESSION_META_FILE)
    : path.resolve(__dirname, '..', 'data', 'session-meta.json'),
  internalControlPort: resolveInternalControlPort(),
  ntfyTopic: process.env.NTFY_TOPIC ?? '',
  ntfyServer: (process.env.NTFY_SERVER || 'https://ntfy.sh').replace(/\/+$/, ''),
};
