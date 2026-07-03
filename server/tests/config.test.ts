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
