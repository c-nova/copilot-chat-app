import * as os from 'os';
import * as path from 'path';
import { DatabaseSync } from 'node:sqlite';

export interface SessionSummary {
  id: string;
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

/** Lists past Copilot CLI sessions whose working directory matches `workDir`, most recently updated first. */
export function listWorkspaceSessions(workDir: string, limit = 50): SessionSummary[] {
  const db = openDb();
  if (!db) return [];
  try {
    const stmt = db.prepare(
      `SELECT s.id as id, s.summary as summary, s.created_at as createdAt, s.updated_at as updatedAt,
              (SELECT COUNT(*) FROM turns t WHERE t.session_id = s.id) as turnCount
       FROM sessions s
       WHERE s.cwd = ?
       ORDER BY s.updated_at DESC
       LIMIT ?`,
    );
    const rows = stmt.all(workDir, limit) as any[];
    return rows.map((r) => ({
      id: String(r.id),
      summary: r.summary ? String(r.summary) : '(no summary)',
      createdAt: String(r.createdAt),
      updatedAt: String(r.updatedAt),
      turnCount: Number(r.turnCount),
    }));
  } catch (err) {
    console.warn('[sessionHistory] listWorkspaceSessions failed:', (err as Error)?.message);
    return [];
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
