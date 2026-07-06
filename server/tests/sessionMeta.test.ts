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
});
