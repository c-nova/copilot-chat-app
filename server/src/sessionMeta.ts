import * as fs from 'fs';
import * as path from 'path';
import { config } from './config';

/**
 * Our own per-session annotations that the CLI's session-store.db has no concept of (label,
 * archived). Stored as a small JSON sidecar file rather than inside the CLI's own SQLite db, so we
 * never need to write to state the CLI itself owns/manages (see the Soft/Hard delete design notes -
 * this store is exactly what a "soft delete" toggles).
 */
export interface SessionMetaEntry {
  label?: string;
  archived?: boolean;
  updatedAt: string;
  /**
   * turnIndex values (see SessionTurn.turnIndex) that were dispatched via the session-control MCP
   * server's run_turn_on_session tool rather than typed by a human in the app - lets the client
   * show those turns distinctly ("message from another session") instead of looking like a normal
   * turn the session's own user typed. Sidecar data, same rationale as label/archived: the CLI's
   * session-store.db has no concept of turn origin and we never write to that db.
   */
  sessionControlTurnIndexes?: number[];
  /**
   * Completed tool activity shown during a turn, keyed by the CLI turnIndex. The CLI session DB
   * persists only the user/final-assistant text, so this sidecar lets the client restore the same
   * compact tool rows after reopening a chat. Details are already secret-redacted by copilotRunner.
   */
  toolActivitiesByTurnIndex?: Record<string, PersistedToolActivity[]>;
  /**
   * PBI-025: the session id of this session's "main"/parent session in the Orchestrator screen, if
   * this session was Spawned as a child (either manually from the UI or automatically via the
   * session-control MCP's spawn_session tool). Absent for ordinary top-level sessions. Sidecar-only,
   * same rationale as the fields above - this is purely an app-level relationship the CLI's own
   * session-store.db has no concept of.
   */
  parentSessionId?: string;
  /**
   * PBI-027: true if this session has ever been opened as the Orchestrator screen's "main" session
   * (either resumed there or started brand-new via "Orchestratorとして開始") - lets the client
   * remember to re-open it in the Orchestrator screen next time, rather than the plain chat screen,
   * without the user having to re-pick every time. Sidecar-only, same rationale as the fields above.
   */
  orchestratorMain?: boolean;
}

export interface PersistedToolActivity {
  name: string;
  summary?: string;
  detail?: string;
  success?: boolean;
}

type SessionMetaStore = Record<string, SessionMetaEntry>;

function readStore(): SessionMetaStore {
  try {
    const raw = fs.readFileSync(config.sessionMetaFilePath, 'utf8');
    const parsed = JSON.parse(raw);
    return parsed && typeof parsed === 'object' ? parsed : {};
  } catch {
    // Missing file (first run) or corrupt content - either way, start from an empty store rather
    // than throwing, since this is annotation data, not something a turn should ever fail over.
    return {};
  }
}

/** Writes via a temp-file-then-rename so a crash mid-write can't leave a truncated/corrupt file behind. */
function writeStore(store: SessionMetaStore): void {
  const dir = path.dirname(config.sessionMetaFilePath);
  fs.mkdirSync(dir, { recursive: true });
  const tmpPath = `${config.sessionMetaFilePath}.tmp-${process.pid}`;
  fs.writeFileSync(tmpPath, JSON.stringify(store, null, 2), 'utf8');
  fs.renameSync(tmpPath, config.sessionMetaFilePath);
}

export function getSessionMeta(sessionId: string): SessionMetaEntry | undefined {
  return readStore()[sessionId];
}

export function getAllSessionMeta(): SessionMetaStore {
  return readStore();
}

/** Sets (or, with `label: null`, clears) a session's display label. */
export function setSessionLabel(sessionId: string, label: string | null): SessionMetaEntry {
  const store = readStore();
  const updated: SessionMetaEntry = { ...store[sessionId], updatedAt: new Date().toISOString() };
  if (label === null) {
    delete updated.label;
  } else {
    updated.label = label;
  }
  store[sessionId] = updated;
  writeStore(store);
  return updated;
}

/** Sets a session's archived flag (the "soft delete" - hides it from the default list without touching the CLI's own db). */
export function setSessionArchived(sessionId: string, archived: boolean): SessionMetaEntry {
  const store = readStore();
  const updated: SessionMetaEntry = { ...store[sessionId], archived, updatedAt: new Date().toISOString() };
  store[sessionId] = updated;
  writeStore(store);
  return updated;
}

/** Removes a session's sidecar entry entirely - used when a session is hard-deleted from the CLI's own db too. */
export function deleteSessionMeta(sessionId: string): void {
  const store = readStore();
  if (sessionId in store) {
    delete store[sessionId];
    writeStore(store);
  }
}

/**
 * Marks a single turnIndex as having been dispatched via session-control's run_turn_on_session,
 * so the client can render it distinctly from turns the session's own human user actually typed.
 * Called by internalControlApi.ts right after a successful cross-session dispatch.
 */
export function markSessionControlTurn(sessionId: string, turnIndex: number): void {
  const store = readStore();
  const existing = store[sessionId];
  const indexes = new Set(existing?.sessionControlTurnIndexes ?? []);
  indexes.add(turnIndex);
  store[sessionId] = { ...existing, updatedAt: new Date().toISOString(), sessionControlTurnIndexes: [...indexes].sort((a, b) => a - b) };
  writeStore(store);
}

/** Persists the completed tool timeline for one turn without disturbing other session metadata. */
export function setTurnToolActivities(sessionId: string, turnIndex: number, activities: PersistedToolActivity[]): void {
  if (activities.length === 0) return;
  const store = readStore();
  const existing = store[sessionId];
  store[sessionId] = {
    ...existing,
    updatedAt: new Date().toISOString(),
    toolActivitiesByTurnIndex: {
      ...existing?.toolActivitiesByTurnIndex,
      [String(turnIndex)]: activities,
    },
  };
  writeStore(store);
}

/**
 * Records that `sessionId` was Spawned as a child of `parentSessionId` (PBI-025's Orchestrator
 * screen) - called once, whether the child was newly created or an existing session got attached.
 */
export function setSessionParent(sessionId: string, parentSessionId: string): void {
  const store = readStore();
  const existing = store[sessionId];
  store[sessionId] = { ...existing, updatedAt: new Date().toISOString(), parentSessionId };
  writeStore(store);
}

/** Returns every session id currently recorded as a child of `parentSessionId`, in no particular order. */
export function getChildSessionIds(parentSessionId: string): string[] {
  const store = readStore();
  return Object.entries(store)
    .filter(([, meta]) => meta.parentSessionId === parentSessionId)
    .map(([sessionId]) => sessionId);
}

/**
 * Marks `sessionId` as an Orchestrator "main" session (PBI-027) - called once whenever the
 * Orchestrator screen opens for it (both the brand-new and resumed-session paths), idempotent.
 */
export function setSessionOrchestratorMain(sessionId: string): void {
  const store = readStore();
  const existing = store[sessionId];
  if (existing?.orchestratorMain) return; // already marked - avoid a needless write on every open
  store[sessionId] = { ...existing, updatedAt: new Date().toISOString(), orchestratorMain: true };
  writeStore(store);
}
