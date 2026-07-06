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
