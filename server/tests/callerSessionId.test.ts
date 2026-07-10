jest.mock('child_process', () => ({ execFileSync: jest.fn() }));

import { execFileSync } from 'child_process';
import { getCallerSessionId, __resetCallerSessionIdCacheForTests } from '../src/callerSessionId';

const mockExecFileSync = execFileSync as jest.Mock;

describe('getCallerSessionId', () => {
  const originalPlatform = process.platform;
  const originalPpid = process.ppid;

  afterEach(() => {
    jest.resetAllMocks();
    __resetCallerSessionIdCacheForTests();
    Object.defineProperty(process, 'platform', { value: originalPlatform, configurable: true });
    Object.defineProperty(process, 'ppid', { value: originalPpid, configurable: true });
  });

  it('extracts the session id from the parent process command line on macOS/Linux (via `ps`)', () => {
    Object.defineProperty(process, 'platform', { value: 'darwin', configurable: true });
    Object.defineProperty(process, 'ppid', { value: 56789, configurable: true });
    mockExecFileSync.mockReturnValue(
      'copilot -p hi --allow-all-tools -C /tmp --session-id=6c2ed704-d2dc-4a6f-9e9f-3e612f865b8c --no-color\n',
    );

    expect(getCallerSessionId()).toBe('6c2ed704-d2dc-4a6f-9e9f-3e612f865b8c');
    expect(mockExecFileSync).toHaveBeenCalledWith('ps', ['-p', '56789', '-o', 'command='], expect.anything());
  });

  it('extracts the session id via PowerShell Get-CimInstance on Windows', () => {
    Object.defineProperty(process, 'platform', { value: 'win32', configurable: true });
    Object.defineProperty(process, 'ppid', { value: 4242, configurable: true });
    mockExecFileSync.mockReturnValue('copilot.exe -p hi --session-id=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\n');

    expect(getCallerSessionId()).toBe('aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee');
    const [command, args] = mockExecFileSync.mock.calls[0];
    expect(command).toBe('powershell.exe');
    expect(args.join(' ')).toContain('ProcessId=4242');
  });

  it('returns null when the parent command line has no --session-id', () => {
    mockExecFileSync.mockReturnValue('some-unrelated-process --foo bar\n');
    expect(getCallerSessionId()).toBeNull();
  });

  it('returns null instead of throwing if the OS lookup itself fails', () => {
    mockExecFileSync.mockImplementation(() => {
      throw new Error('ps: no such process');
    });
    expect(getCallerSessionId()).toBeNull();
  });

  it('returns null for an empty/whitespace-only command line', () => {
    mockExecFileSync.mockReturnValue('   \n');
    expect(getCallerSessionId()).toBeNull();
  });

  it('memoizes the result - a second call does not re-invoke the OS lookup (PBI-028 perf fix)', () => {
    Object.defineProperty(process, 'platform', { value: 'darwin', configurable: true });
    Object.defineProperty(process, 'ppid', { value: 56789, configurable: true });
    mockExecFileSync.mockReturnValue(
      'copilot -p hi --session-id=6c2ed704-d2dc-4a6f-9e9f-3e612f865b8c --no-color\n',
    );

    expect(getCallerSessionId()).toBe('6c2ed704-d2dc-4a6f-9e9f-3e612f865b8c');
    expect(getCallerSessionId()).toBe('6c2ed704-d2dc-4a6f-9e9f-3e612f865b8c');
    expect(getCallerSessionId()).toBe('6c2ed704-d2dc-4a6f-9e9f-3e612f865b8c');
    expect(mockExecFileSync).toHaveBeenCalledTimes(1);
  });

  it('memoizes a failed (null) lookup too - does not retry on every call', () => {
    mockExecFileSync.mockImplementation(() => {
      throw new Error('ps: no such process');
    });

    expect(getCallerSessionId()).toBeNull();
    expect(getCallerSessionId()).toBeNull();
    expect(mockExecFileSync).toHaveBeenCalledTimes(1);
  });
});
