import * as os from 'os';
import * as path from 'path';

jest.mock('../src/copilotRunner', () => ({
  runCopilotTurn: jest.fn(),
}));
jest.mock('../src/sessionHistory', () => ({
  getSessionCwd: jest.fn(),
  getSessionHistory: jest.fn(() => []),
  listSessions: jest.fn(() => []),
}));

import { runCopilotTurn } from '../src/copilotRunner';
import { getSessionCwd } from '../src/sessionHistory';
import { config } from '../src/config';
import { runConversationTurn } from '../src/wsServer';

const mockedRunCopilotTurn = runCopilotTurn as jest.Mock;
const mockedGetSessionCwd = getSessionCwd as jest.Mock;

function uniqueId(label: string): string {
  return `${label}-${Math.random().toString(36).slice(2)}`;
}

describe('runConversationTurn', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockedRunCopilotTurn.mockResolvedValue({ finalText: 'ok', exitCode: 0, sessionId: 'x' });
  });

  it('throws and never calls runCopilotTurn when requireExistingSession is true but no session is found', async () => {
    mockedGetSessionCwd.mockReturnValue(null);
    const id = uniqueId('missing');
    await expect(runConversationTurn(id, 'hi', { requireExistingSession: true })).rejects.toThrow(
      /No existing session found/,
    );
    expect(mockedRunCopilotTurn).not.toHaveBeenCalled();
  });

  it('uses the requested cwd for a brand-new session when it is within the allowed roots', async () => {
    mockedGetSessionCwd.mockReturnValue(null);
    const id = uniqueId('new-allowed');
    const cwd = path.join(os.homedir(), 'some-subfolder');
    await runConversationTurn(id, 'hello', { requestedCwd: cwd });
    expect(mockedRunCopilotTurn).toHaveBeenCalledWith(id, 'hello', expect.any(Function), expect.any(Function), undefined, cwd);
  });

  it('falls back to the default workDir when the requested cwd is outside the allowed roots', async () => {
    mockedGetSessionCwd.mockReturnValue(null);
    const id = uniqueId('new-rejected');
    await runConversationTurn(id, 'hello', { requestedCwd: '/etc' });
    expect(mockedRunCopilotTurn).toHaveBeenCalledWith(
      id,
      'hello',
      expect.any(Function),
      expect.any(Function),
      undefined,
      config.workDir,
    );
  });

  it('uses the CLI-recorded cwd when resuming an existing session, ignoring any requestedCwd', async () => {
    mockedGetSessionCwd.mockReturnValue('/some/existing/cwd');
    const id = uniqueId('resume');
    await runConversationTurn(id, 'hello', { requestedCwd: '/etc' });
    expect(mockedRunCopilotTurn).toHaveBeenCalledWith(
      id,
      'hello',
      expect.any(Function),
      expect.any(Function),
      undefined,
      '/some/existing/cwd',
    );
  });

  it('serializes two overlapping calls for the same conversationId', async () => {
    mockedGetSessionCwd.mockReturnValue('/some/cwd');
    const order: string[] = [];
    mockedRunCopilotTurn.mockImplementation(async () => {
      order.push('start');
      await new Promise((r) => setTimeout(r, 20));
      order.push('end');
      return { finalText: 'ok', exitCode: 0, sessionId: 'x' };
    });
    const id = uniqueId('serial');
    await Promise.all([runConversationTurn(id, 'first', {}), runConversationTurn(id, 'second', {})]);
    expect(order).toEqual(['start', 'end', 'start', 'end']);
  });

  it('does not jam the per-conversation queue after a rejected turn', async () => {
    mockedGetSessionCwd.mockReturnValue('/some/cwd');
    const id = uniqueId('reject-then-succeed');
    mockedRunCopilotTurn.mockRejectedValueOnce(new Error('boom'));
    await expect(runConversationTurn(id, 'first', {})).rejects.toThrow('boom');

    mockedRunCopilotTurn.mockResolvedValueOnce({ finalText: 'recovered', exitCode: 0, sessionId: 'x' });
    await expect(runConversationTurn(id, 'second', {})).resolves.toEqual(
      expect.objectContaining({ finalText: 'recovered' }),
    );
  });
});
