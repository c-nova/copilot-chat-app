import * as os from 'os';
import * as path from 'path';
import { DatabaseSync } from 'node:sqlite';
import { isPathAllowed } from './pathAccess';

export interface SessionSummary {
  id: string;
  /** Working directory this session was created with (from the CLI's own session-store.db). */
  cwd: string;
  summary: string;
  createdAt: string;
  updatedAt: string;
  turnCount: number;
}

export interface SessionTurn {
  turnIndex: number;
  userMessage: string;
  assistantResponse: string;
  timestamp: string;
}

/**
 * Path to the GitHub Copilot CLI's local session store (SQLite). Same file used by the CLI itself
 * to power `--resume`/`--continue` and cross-session search - we just read it (read-only) to list
 * and replay past conversations that happened in our configured workspace directory.
 */
function dbPath(): string {
  return path.join(os.homedir(), '.copilot', 'session-store.db');
}

/** Opens the CLI's session-store.db read-only. Returns null if the file doesn't exist yet (e.g. brand-new machine). */
function openDb(): DatabaseSync | null {
  try {
    return new DatabaseSync(dbPath(), { readOnly: true });
  } catch (err) {
    console.warn('[sessionHistory] could not open session-store.db:', (err as Error)?.message);
    return null;
  }
}

/**
 * Lists past Copilot CLI sessions whose working directory falls within one of `allowedRoots`,
 * most recently updated first. Sessions run outside those roots (e.g. ad hoc `copilot` CLI usage
 * elsewhere on this machine, from before BROWSE_ROOTS existed, or unrelated to this app) are left
 * out - this app was always scoped to specific working directories, and expanding the Sessions
 * list to literally every session the CLI has ever recorded on the machine would widen that scope
 * beyond what a client should be able to see/resume through this server.
 *
 * The containment filter can't be expressed cleanly as a single SQL WHERE clause across an
 * arbitrary root list, so this pulls a generously-sized recent window and filters in JS instead.
 */
export function listSessions(allowedRoots: string[], limit = 50): SessionSummary[] {
  const db = openDb();
  if (!db) return [];
  try {
    const stmt = db.prepare(
      `SELECT s.id as id, s.cwd as cwd, s.summary as summary, s.created_at as createdAt, s.updated_at as updatedAt,
              (SELECT COUNT(*) FROM turns t WHERE t.session_id = s.id) as turnCount
       FROM sessions s
       ORDER BY s.updated_at DESC
       LIMIT ?`,
    );
    const rows = stmt.all(Math.max(limit * 10, 200)) as any[];
    const mapped = rows.map((r) => ({
      id: String(r.id),
      cwd: r.cwd ? String(r.cwd) : '',
      summary: r.summary ? String(r.summary) : '(no summary)',
      createdAt: String(r.createdAt),
      updatedAt: String(r.updatedAt),
      turnCount: Number(r.turnCount),
    }));
    return mapped.filter((s) => s.cwd && isPathAllowed(s.cwd, allowedRoots)).slice(0, limit);
  } catch (err) {
    console.warn('[sessionHistory] listSessions failed:', (err as Error)?.message);
    return [];
  } finally {
    db.close();
  }
}

/**
 * Looks up the working directory a given session id was created with, per the CLI's own record.
 * Used to resume a session with the same cwd it started in, without relying on a client to resend
 * it (and without ever letting a resume silently switch a session to a different folder).
 */
export function getSessionCwd(sessionId: string): string | null {
  const db = openDb();
  if (!db) return null;
  try {
    const stmt = db.prepare(`SELECT cwd FROM sessions WHERE id = ?`);
    const row = stmt.get(sessionId) as any;
    return row?.cwd ? String(row.cwd) : null;
  } catch (err) {
    console.warn('[sessionHistory] getSessionCwd failed:', (err as Error)?.message);
    return null;
  } finally {
    db.close();
  }
}

/** Returns the full turn-by-turn transcript (user message + assistant response) for one session id. */
export function getSessionHistory(sessionId: string): SessionTurn[] {
  const db = openDb();
  if (!db) return [];
  try {
    const stmt = db.prepare(
      `SELECT turn_index as turnIndex, user_message as userMessage, assistant_response as assistantResponse, timestamp
       FROM turns
       WHERE session_id = ?
       ORDER BY turn_index ASC`,
    );
    const rows = stmt.all(sessionId) as any[];
    return rows.map((r) => ({
      turnIndex: Number(r.turnIndex),
      userMessage: r.userMessage ? String(r.userMessage) : '',
      assistantResponse: r.assistantResponse ? String(r.assistantResponse) : '',
      timestamp: String(r.timestamp),
    }));
  } catch (err) {
    console.warn('[sessionHistory] getSessionHistory failed:', (err as Error)?.message);
    return [];
  } finally {
    db.close();
  }
}
