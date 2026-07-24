import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

// sessionMeta.ts reads its file path from config.sessionMetaFilePath, which is only computed once
// at module load - point it at a throwaway temp file (via SESSION_META_FILE) before requiring
// either module, and reset both between tests so runs don't interfere with each other or with a
// real server/data/session-meta.json on this machine.
describe('sessionMeta', () => {
  let tmpDir: string;
  const originalEnv = process.env.SESSION_META_FILE;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'session-meta-test-'));
    process.env.SESSION_META_FILE = path.join(tmpDir, 'session-meta.json');
    jest.resetModules();
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
    if (originalEnv === undefined) delete process.env.SESSION_META_FILE;
    else process.env.SESSION_META_FILE = originalEnv;
    jest.resetModules();
  });

  it('returns undefined for a session with no stored meta', () => {
    const { getSessionMeta } = require('../src/sessionMeta');
    expect(getSessionMeta('unknown-session')).toBeUndefined();
  });

  it('sets and reads back a label', () => {
    const { getSessionMeta, setSessionLabel } = require('../src/sessionMeta');
    setSessionLabel('session-1', 'My Project');
    expect(getSessionMeta('session-1')?.label).toBe('My Project');
  });

  it('clears a label when passed null', () => {
    const { getSessionMeta, setSessionLabel } = require('../src/sessionMeta');
    setSessionLabel('session-1', 'My Project');
    setSessionLabel('session-1', null);
    expect(getSessionMeta('session-1')?.label).toBeUndefined();
  });

  it('sets the archived flag independently of the label', () => {
    const { getSessionMeta, setSessionLabel, setSessionArchived } = require('../src/sessionMeta');
    setSessionLabel('session-1', 'My Project');
    setSessionArchived('session-1', true);
    const meta = getSessionMeta('session-1');
    expect(meta?.label).toBe('My Project');
    expect(meta?.archived).toBe(true);
  });

  it('persists across a fresh read of the store (survives "restart")', () => {
    const first = require('../src/sessionMeta');
    first.setSessionLabel('session-1', 'Persisted');
    jest.resetModules();
    const second = require('../src/sessionMeta');
    expect(second.getSessionMeta('session-1')?.label).toBe('Persisted');
  });

  it('deleteSessionMeta removes the entry entirely', () => {
    const { getSessionMeta, setSessionLabel, deleteSessionMeta } = require('../src/sessionMeta');
    setSessionLabel('session-1', 'To be deleted');
    deleteSessionMeta('session-1');
    expect(getSessionMeta('session-1')).toBeUndefined();
  });

  it('tolerates a missing store file (first run)', () => {
    const { getAllSessionMeta } = require('../src/sessionMeta');
    expect(getAllSessionMeta()).toEqual({});
  });

  it('tolerates a corrupt store file rather than throwing', () => {
    fs.mkdirSync(path.dirname(process.env.SESSION_META_FILE as string), { recursive: true });
    fs.writeFileSync(process.env.SESSION_META_FILE as string, 'not valid json{{{', 'utf8');
    const { getAllSessionMeta } = require('../src/sessionMeta');
    expect(getAllSessionMeta()).toEqual({});
  });

  it('marks a turnIndex as session-control-originated', () => {
    const { getSessionMeta, markSessionControlTurn } = require('../src/sessionMeta');
    markSessionControlTurn('session-1', 3);
    expect(getSessionMeta('session-1')?.sessionControlTurnIndexes).toEqual([3]);
  });

  it('accumulates multiple marked turnIndexes, sorted, without duplicates', () => {
    const { getSessionMeta, markSessionControlTurn } = require('../src/sessionMeta');
    markSessionControlTurn('session-1', 5);
    markSessionControlTurn('session-1', 1);
    markSessionControlTurn('session-1', 5);
    expect(getSessionMeta('session-1')?.sessionControlTurnIndexes).toEqual([1, 5]);
  });

  it('marking a turn preserves an existing label/archived flag on the same session', () => {
    const { getSessionMeta, setSessionLabel, markSessionControlTurn } = require('../src/sessionMeta');
    setSessionLabel('session-1', 'My Project');
    markSessionControlTurn('session-1', 2);
    const meta = getSessionMeta('session-1');
    expect(meta?.label).toBe('My Project');
    expect(meta?.sessionControlTurnIndexes).toEqual([2]);
  });

  it('persists completed tool activities by turnIndex without disturbing other metadata', () => {
    const { getSessionMeta, setSessionLabel, setTurnToolActivities } = require('../src/sessionMeta');
    setSessionLabel('session-1', 'With tools');
    setTurnToolActivities('session-1', 4, [
      { name: 'view', summary: 'README.md', detail: '{\n  "path": "README.md"\n}', success: true },
      { name: 'edit', detail: '{\n  "path": "README.md"\n}', success: false },
    ]);
    const meta = getSessionMeta('session-1');
    expect(meta?.label).toBe('With tools');
    expect(meta?.toolActivitiesByTurnIndex?.['4']).toEqual([
      { name: 'view', summary: 'README.md', detail: '{\n  "path": "README.md"\n}', success: true },
      { name: 'edit', detail: '{\n  "path": "README.md"\n}', success: false },
    ]);
  });

  it('keeps tool activities from separate turns independently', () => {
    const { getSessionMeta, setTurnToolActivities } = require('../src/sessionMeta');
    setTurnToolActivities('session-1', 1, [{ name: 'view', success: true }]);
    setTurnToolActivities('session-1', 2, [{ name: 'search', success: true }]);
    expect(getSessionMeta('session-1')?.toolActivitiesByTurnIndex).toEqual({
      '1': [{ name: 'view', success: true }],
      '2': [{ name: 'search', success: true }],
    });
  });

  it('does not create metadata for an empty tool activity list', () => {
    const { getSessionMeta, setTurnToolActivities } = require('../src/sessionMeta');
    setTurnToolActivities('session-1', 1, []);
    expect(getSessionMeta('session-1')).toBeUndefined();
  });

  it('records a session as a child of a parent session', () => {
    const { getSessionMeta, setSessionParent } = require('../src/sessionMeta');
    setSessionParent('child-1', 'main-session');
    expect(getSessionMeta('child-1')?.parentSessionId).toBe('main-session');
  });

  it('lists every child of a parent session', () => {
    const { setSessionParent, getChildSessionIds } = require('../src/sessionMeta');
    setSessionParent('child-1', 'main-session');
    setSessionParent('child-2', 'main-session');
    setSessionParent('unrelated', 'some-other-session');
    const children = getChildSessionIds('main-session').sort();
    expect(children).toEqual(['child-1', 'child-2']);
  });

  it('returns an empty array for a parent with no children', () => {
    const { getChildSessionIds } = require('../src/sessionMeta');
    expect(getChildSessionIds('no-such-parent')).toEqual([]);
  });

  it('setting a parent preserves an existing label on the same session', () => {
    const { getSessionMeta, setSessionLabel, setSessionParent } = require('../src/sessionMeta');
    setSessionLabel('child-1', 'Worker A');
    setSessionParent('child-1', 'main-session');
    const meta = getSessionMeta('child-1');
    expect(meta?.label).toBe('Worker A');
    expect(meta?.parentSessionId).toBe('main-session');
  });

  it('marks a session as an Orchestrator main session', () => {
    const { getSessionMeta, setSessionOrchestratorMain } = require('../src/sessionMeta');
    setSessionOrchestratorMain('main-1');
    expect(getSessionMeta('main-1')?.orchestratorMain).toBe(true);
  });

  it('marking a session as Orchestrator main preserves an existing label', () => {
    const { getSessionMeta, setSessionLabel, setSessionOrchestratorMain } = require('../src/sessionMeta');
    setSessionLabel('main-1', 'My Orchestrator');
    setSessionOrchestratorMain('main-1');
    const meta = getSessionMeta('main-1');
    expect(meta?.label).toBe('My Orchestrator');
    expect(meta?.orchestratorMain).toBe(true);
  });

  it('marking a session as Orchestrator main is idempotent (safe to call every time the screen opens)', () => {
    const { getSessionMeta, setSessionOrchestratorMain } = require('../src/sessionMeta');
    setSessionOrchestratorMain('main-1');
    const firstUpdatedAt = getSessionMeta('main-1')?.updatedAt;
    setSessionOrchestratorMain('main-1');
    expect(getSessionMeta('main-1')?.updatedAt).toBe(firstUpdatedAt);
  });
});
