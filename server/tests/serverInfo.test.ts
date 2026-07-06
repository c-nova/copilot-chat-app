import * as os from 'os';
import { getServerInfo } from '../src/serverInfo';

describe('getServerInfo', () => {
  it('returns the expected static fields', async () => {
    const info = await getServerInfo();
    expect(info.os).toBe(os.platform());
    expect(info.hostname).toBe(os.hostname());
    expect(info.nodeVersion).toBe(process.version);
    expect(typeof info.appVersion).toBe('string');
    expect(info.appVersion.length).toBeGreaterThan(0);
    expect(Array.isArray(info.browseRoots)).toBe(true);
    expect(typeof info.workDir).toBe('string');
  });

  it('resolves copilotCliVersion to a string even if the CLI binary is unavailable', async () => {
    // Doesn't assume `copilot` is installed in whatever environment runs the tests - getCopilotCliVersion()
    // resolves to "unknown" on a spawn error rather than rejecting, and this just checks that contract.
    const info = await getServerInfo();
    expect(typeof info.copilotCliVersion).toBe('string');
    expect(info.copilotCliVersion.length).toBeGreaterThan(0);
  });

  it('caches the CLI version across repeated calls', async () => {
    const first = await getServerInfo();
    const second = await getServerInfo();
    expect(second.copilotCliVersion).toBe(first.copilotCliVersion);
  });
});
