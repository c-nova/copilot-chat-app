import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { DatabaseSync } from 'node:sqlite';

// sessionHistory.ts reads its db path from COPILOT_SESSION_STORE_DB (an override added purely for
// testability - see sessionHistory.ts's dbPath()), so we point it at a disposable temp file with a
// schema mimicking the CLI's own session-store.db rather than ever touching a real one.
describe('sessionHistory.deleteSessionHard', () => {
  let tmpDir: string;
  let dbFile: string;
  const originalEnv = process.env.COPILOT_SESSION_STORE_DB;

  function seedDb() {
    const db = new DatabaseSync(dbFile);
    // Mirrors the real ~/.copilot/session-store.db schema closely enough for deleteSessionHard's
    // DELETEs to succeed - several tables have a session_id FOREIGN KEY REFERENCES sessions(id),
    // so deleteSessionHard must clean them all up before the sessions row itself or SQLite's FK
    // enforcement rejects it (a real run against the actual CLI db surfaced this - see
    // sessionHistory.ts's deleteSessionHard doc comment).
    db.exec(`
      PRAGMA foreign_keys = ON;
      CREATE TABLE sessions (
        id TEXT PRIMARY KEY,
        cwd TEXT,
        summary TEXT,
        created_at TEXT,
        updated_at TEXT
      );
      CREATE TABLE turns (
        session_id TEXT NOT NULL REFERENCES sessions(id),
        turn_index INTEGER,
        user_message TEXT,
        assistant_response TEXT,
        timestamp TEXT
      );
      CREATE TABLE checkpoints (
        session_id TEXT NOT NULL REFERENCES sessions(id),
        checkpoint_number INTEGER
      );
      CREATE TABLE session_files (
        session_id TEXT NOT NULL REFERENCES sessions(id),
        file_path TEXT
      );
      CREATE TABLE session_refs (
        session_id TEXT NOT NULL REFERENCES sessions(id),
        ref_type TEXT,
        ref_value TEXT
      );
      CREATE TABLE forge_trajectory_events (
        session_id TEXT NOT NULL REFERENCES sessions(id),
        event_type TEXT
      );
      CREATE TABLE assistant_usage_events (
        session_id TEXT NOT NULL REFERENCES sessions(id),
        model TEXT
      );
    `);
    const insertSession = db.prepare(
      `INSERT INTO sessions (id, cwd, summary, created_at, updated_at) VALUES (?, ?, ?, ?, ?)`,
    );
    insertSession.run('session-to-delete', '/tmp/proj-a', 'summary A', 't0', 't1');
    insertSession.run('session-to-keep', '/tmp/proj-b', 'summary B', 't0', 't1');
    const insertTurn = db.prepare(
      `INSERT INTO turns (session_id, turn_index, user_message, assistant_response, timestamp) VALUES (?, ?, ?, ?, ?)`,
    );
    insertTurn.run('session-to-delete', 0, 'hi', 'hello', 't0');
    insertTurn.run('session-to-delete', 1, 'bye', 'goodbye', 't1');
    insertTurn.run('session-to-keep', 0, 'still here', 'yep', 't0');
    // A row in each of the other referencing tables, only for the session we're about to delete -
    // proves deleteSessionHard actually clears all of them, not just turns.
    db.prepare(`INSERT INTO checkpoints (session_id, checkpoint_number) VALUES (?, ?)`).run('session-to-delete', 1);
    db.prepare(`INSERT INTO session_files (session_id, file_path) VALUES (?, ?)`).run('session-to-delete', '/tmp/proj-a/file.txt');
    db.prepare(`INSERT INTO session_refs (session_id, ref_type, ref_value) VALUES (?, ?, ?)`).run('session-to-delete', 'branch', 'main');
    db.prepare(`INSERT INTO forge_trajectory_events (session_id, event_type) VALUES (?, ?)`).run('session-to-delete', 'shell');
    db.prepare(`INSERT INTO assistant_usage_events (session_id, model) VALUES (?, ?)`).run('session-to-delete', 'gpt-test');
    db.close();
  }

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'session-history-test-'));
    dbFile = path.join(tmpDir, 'session-store.db');
    process.env.COPILOT_SESSION_STORE_DB = dbFile;
    jest.resetModules();
    seedDb();
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
    if (originalEnv === undefined) delete process.env.COPILOT_SESSION_STORE_DB;
    else process.env.COPILOT_SESSION_STORE_DB = originalEnv;
    jest.resetModules();
  });

  it('removes the target session and all its turns', () => {
    const { deleteSessionHard, getSessionCwd, getSessionHistory } = require('../src/sessionHistory');
    deleteSessionHard('session-to-delete');
    expect(getSessionCwd('session-to-delete')).toBeNull();
    expect(getSessionHistory('session-to-delete')).toEqual([]);
  });

  it('leaves other sessions and their turns untouched', () => {
    const { deleteSessionHard, getSessionCwd, getSessionHistory } = require('../src/sessionHistory');
    deleteSessionHard('session-to-delete');
    expect(getSessionCwd('session-to-keep')).toBe('/tmp/proj-b');
    expect(getSessionHistory('session-to-keep')).toHaveLength(1);
  });

  it('no longer appears in listSessions after deletion', () => {
    const { deleteSessionHard, listSessions } = require('../src/sessionHistory');
    deleteSessionHard('session-to-delete');
    const ids = listSessions(['/tmp']).map((s: { id: string }) => s.id);
    expect(ids).not.toContain('session-to-delete');
    expect(ids).toContain('session-to-keep');
  });

  it('is a no-op (does not throw) for a session id that does not exist', () => {
    const { deleteSessionHard } = require('../src/sessionHistory');
    expect(() => deleteSessionHard('no-such-session')).not.toThrow();
  });

  it('also clears the other FK-referencing tables (checkpoints, session_files, session_refs, forge_trajectory_events, assistant_usage_events)', () => {
    const { deleteSessionHard } = require('../src/sessionHistory');
    deleteSessionHard('session-to-delete');

    const db = new DatabaseSync(dbFile, { readOnly: true });
    try {
      for (const table of [
        'checkpoints',
        'session_files',
        'session_refs',
        'forge_trajectory_events',
        'assistant_usage_events',
      ]) {
        const row = db.prepare(`SELECT COUNT(*) as n FROM ${table} WHERE session_id = ?`).get('session-to-delete') as any;
        expect(row.n).toBe(0);
      }
    } finally {
      db.close();
    }
  });
});
