// config.ts calls dotenv.config() on import, which would otherwise repopulate any env var this
// file deletes (e.g. BROWSE_ROOTS below) straight back out of the real server/.env on a dev
// machine that has one configured - making "unset" tests pass/fail depending on what's in that
// file rather than on config.ts's own fallback logic. Stub it out so every test here is driven
// purely by whatever this file explicitly sets on process.env.
jest.mock('dotenv', () => ({ config: jest.fn() }));

describe('config dotenv loading', () => {
  it('loads server/.env independently of the process working directory', () => {
    jest.resetModules();
    const dotenv = require('dotenv');
    require('../src/config');

    expect(dotenv.config).toHaveBeenCalledWith({ path: require('path').resolve(__dirname, '..', '.env') });
  });
});

describe('config AUTH_TOKEN validation', () => {
  const originalAuthToken = process.env.AUTH_TOKEN;

  afterEach(() => {
    process.env.AUTH_TOKEN = originalAuthToken;
    jest.resetModules();
  });

  // Note: we don't test the "AUTH_TOKEN entirely unset" case here, because dotenv.config() only
  // fills in variables that aren't already present in process.env - if a real server/.env exists
  // on the machine running the tests (as it does for local dev), deleting the env var would just
  // let dotenv repopulate it from that file, making the test's outcome environment-dependent.
  // Explicitly setting an empty/too-short string (below) avoids that: dotenv won't override a
  // variable that's already set, even to ''.

  it('throws at startup when AUTH_TOKEN is an empty string', () => {
    process.env.AUTH_TOKEN = '';
    jest.resetModules();
    expect(() => require('../src/config')).toThrow(/at least 16 characters/);
  });

  it('throws at startup when AUTH_TOKEN is shorter than the minimum length', () => {
    process.env.AUTH_TOKEN = 'too-short';
    jest.resetModules();
    expect(() => require('../src/config')).toThrow(/at least 16 characters/);
  });

  it('accepts an AUTH_TOKEN at least 16 characters long', () => {
    process.env.AUTH_TOKEN = 'a'.repeat(16);
    jest.resetModules();
    expect(() => require('../src/config')).not.toThrow();
  });
});

describe('config BROWSE_ROOTS resolution', () => {
  const originalBrowseRoots = process.env.BROWSE_ROOTS;
  const os = require('os');
  const path = require('path');

  afterEach(() => {
    if (originalBrowseRoots === undefined) delete process.env.BROWSE_ROOTS;
    else process.env.BROWSE_ROOTS = originalBrowseRoots;
    jest.resetModules();
  });

  it('defaults to workDir (not the home directory) when BROWSE_ROOTS is unset', () => {
    delete process.env.BROWSE_ROOTS;
    jest.resetModules();
    const { config } = require('../src/config');
    // Defaulting to the home directory would surface every Copilot CLI session ever run on the
    // machine (including unrelated tools) in this app's Sessions list - see config.ts comments.
    expect(config.browseRoots).toEqual([config.workDir]);
    expect(config.browseRoots).not.toEqual([os.homedir()]);
  });

  it('parses a comma-separated list of existing directories', () => {
    process.env.BROWSE_ROOTS = `${__dirname},${os.tmpdir()}`;
    jest.resetModules();
    const { config } = require('../src/config');
    expect(config.browseRoots).toEqual([path.resolve(__dirname), path.resolve(os.tmpdir())]);
  });

  it('skips nonexistent entries and keeps valid ones', () => {
    process.env.BROWSE_ROOTS = `${__dirname},/this/path/does/not/exist/hopefully`;
    jest.resetModules();
    const { config } = require('../src/config');
    expect(config.browseRoots).toEqual([path.resolve(__dirname)]);
  });

  it('falls back to workDir when every configured entry is invalid', () => {
    process.env.BROWSE_ROOTS = '/this/path/does/not/exist/hopefully,/nor/does/this/one';
    jest.resetModules();
    const { config } = require('../src/config');
    expect(config.browseRoots).toEqual([config.workDir]);
  });
});

describe('config PEER_SERVERS resolution', () => {
  const originalPeerServers = process.env.PEER_SERVERS;

  afterEach(() => {
    if (originalPeerServers === undefined) delete process.env.PEER_SERVERS;
    else process.env.PEER_SERVERS = originalPeerServers;
    jest.resetModules();
  });

  it('defaults to an empty array when PEER_SERVERS is unset', () => {
    delete process.env.PEER_SERVERS;
    jest.resetModules();
    const { config } = require('../src/config');
    expect(config.peers).toEqual([]);
  });

  it('parses a valid JSON array of peer definitions', () => {
    process.env.PEER_SERVERS = JSON.stringify([{ name: 'windows-pc', url: 'ws://192.168.1.50:5219', token: 'abc123' }]);
    jest.resetModules();
    const { config } = require('../src/config');
    expect(config.peers).toEqual([{ name: 'windows-pc', url: 'ws://192.168.1.50:5219', token: 'abc123' }]);
  });

  it('skips entries missing a required field and keeps valid ones', () => {
    process.env.PEER_SERVERS = JSON.stringify([
      { name: 'windows-pc', url: 'ws://192.168.1.50:5219', token: 'abc123' },
      { name: 'missing-token', url: 'ws://192.168.1.51:5219' },
    ]);
    jest.resetModules();
    const { config } = require('../src/config');
    expect(config.peers).toEqual([{ name: 'windows-pc', url: 'ws://192.168.1.50:5219', token: 'abc123' }]);
  });

  it('falls back to an empty array when PEER_SERVERS is not valid JSON', () => {
    process.env.PEER_SERVERS = 'not json at all';
    jest.resetModules();
    const { config } = require('../src/config');
    expect(config.peers).toEqual([]);
  });

  it('falls back to an empty array when PEER_SERVERS is valid JSON but not an array', () => {
    process.env.PEER_SERVERS = JSON.stringify({ name: 'not-an-array' });
    jest.resetModules();
    const { config } = require('../src/config');
    expect(config.peers).toEqual([]);
  });
});
