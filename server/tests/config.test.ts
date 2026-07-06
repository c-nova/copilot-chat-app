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
