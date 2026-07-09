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
 * to power `--resume`/`--continue` and cross-session search - we just read it (read-only, except
 * for deleteSessionHard's deliberate read-write open) to list and replay past conversations that
 * happened in our configured workspace directory.
 *
 * Overridable via COPILOT_SESSION_STORE_DB (same rationale/pattern as sessionMeta.ts's
 * SESSION_META_FILE) so tests can point this at a disposable temp file instead of touching a real
 * `~/.copilot/session-store.db` on the machine running them.
 */
function dbPath(): string {
  return process.env.COPILOT_SESSION_STORE_DB || path.join(os.homedir(), '.copilot', 'session-store.db');
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

/**
 * Looks up a single session's summary by id, regardless of BROWSE_ROOTS filtering (unlike
 * listSessions) - used by PBI-025's sessions:children handler, which already knows the exact ids
 * it wants (from sessionMeta's getChildSessionIds) and just needs their display info. Returns null
 * if the CLI's session-store.db has no record of that id (e.g. it was hard-deleted).
 */
export function getSessionSummary(sessionId: string): SessionSummary | null {
  const db = openDb();
  if (!db) return null;
  try {
    const stmt = db.prepare(
      `SELECT s.id as id, s.cwd as cwd, s.summary as summary, s.created_at as createdAt, s.updated_at as updatedAt,
              (SELECT COUNT(*) FROM turns t WHERE t.session_id = s.id) as turnCount
       FROM sessions s
       WHERE s.id = ?`,
    );
    const row = stmt.get(sessionId) as any;
    if (!row) return null;
    return {
      id: String(row.id),
      cwd: row.cwd ? String(row.cwd) : '',
      summary: row.summary ? String(row.summary) : '(no summary)',
      createdAt: String(row.createdAt),
      updatedAt: String(row.updatedAt),
      turnCount: Number(row.turnCount),
    };
  } catch (err) {
    console.warn('[sessionHistory] getSessionSummary failed:', (err as Error)?.message);
    return null;
  } finally {
    db.close();
  }
}

/**
 * Permanently deletes a session and everything referencing it from the CLI's own
 * session-store.db (PBI-021's "hard delete" - irreversible, unlike sessionMeta.setSessionArchived's
 * reversible sidecar-only "soft delete"). Opens the db read-write, unlike every other function in
 * this file, purely for this one operation - callers are responsible for confirming with the user
 * first and for refusing this while the session has an in-progress turn (see wsServer.ts's
 * activeConversationTurns).
 *
 * The CLI's schema has several tables with a `session_id` FOREIGN KEY REFERENCES sessions(id) -
 * turns, checkpoints, session_files, session_refs, forge_trajectory_events, assistant_usage_events
 * (as of this schema; a real `~/.copilot/session-store.db` was inspected directly to enumerate
 * these, since this app's own sessionHistory.ts only ever read a subset of columns/tables). All of
 * them must be deleted before the sessions row itself, or SQLite's FK enforcement rejects the
 * DELETE.
 *
 * Deliberately does NOT touch the FTS5 `search_index` virtual table: verified live against a real
 * session-store.db that Node's built-in `node:sqlite` module errors with "no such module: fts5" on
 * any query against it (that SQLite build doesn't include the FTS5 extension, unlike whatever
 * SQLite library the CLI itself links against to have created that table). search_index isn't
 * FK-enforced, so leaving a hard-deleted session's rows behind there can't break anything - worst
 * case is the CLI's own full-text search still surfaces content from a session this app no longer
 * lists, a cosmetic gap versus refusing to hard-delete at all.
 */
export function deleteSessionHard(sessionId: string): void {
  const db = new DatabaseSync(dbPath());
  try {
    db.exec('BEGIN');
    for (const table of [
      'forge_trajectory_events',
      'assistant_usage_events',
      'session_refs',
      'session_files',
      'checkpoints',
      'turns',
    ]) {
      db.prepare(`DELETE FROM ${table} WHERE session_id = ?`).run(sessionId);
    }
    db.prepare(`DELETE FROM sessions WHERE id = ?`).run(sessionId);
    db.exec('COMMIT');
  } catch (err) {
    try {
      db.exec('ROLLBACK');
    } catch {
      // ignore - the original error below is what matters
    }
    throw err;
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

/**
 * Full-text search across session titles (summary) AND the actual conversation content (turns'
 * user_message/assistant_response) - the CLI's own auto-generated summary is often the same generic
 * text across many sessions (see DisplayTitle's fallback chain client-side), so title-only search
 * would miss most real matches. Case-insensitive substring match (SQL LIKE with both sides wrapped
 * in %) - good enough for a personal session list; not attempting real FTS ranking.
 */
export function searchSessions(query: string, allowedRoots: string[], limit = 50): SessionSummary[] {
  const trimmed = query.trim();
  if (!trimmed) return [];
  const db = openDb();
  if (!db) return [];
  try {
    const like = `%${trimmed}%`;
    const stmt = db.prepare(
      `SELECT s.id as id, s.cwd as cwd, s.summary as summary, s.created_at as createdAt, s.updated_at as updatedAt,
              (SELECT COUNT(*) FROM turns t WHERE t.session_id = s.id) as turnCount
       FROM sessions s
       WHERE s.summary LIKE ?
          OR EXISTS (
               SELECT 1 FROM turns t
               WHERE t.session_id = s.id
                 AND (t.user_message LIKE ? OR t.assistant_response LIKE ?)
             )
       ORDER BY s.updated_at DESC
       LIMIT ?`,
    );
    const rows = stmt.all(like, like, like, Math.max(limit * 10, 200)) as any[];
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
    console.warn('[sessionHistory] searchSessions failed:', (err as Error)?.message);
    return [];
  } finally {
    db.close();
  }
}
