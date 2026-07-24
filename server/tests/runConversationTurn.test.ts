import * as path from 'path';

jest.mock('../src/copilotRunner', () => ({
  runCopilotTurn: jest.fn(),
}));
jest.mock('../src/sessionHistory', () => ({
  getSessionCwd: jest.fn(),
  getSessionHistory: jest.fn(() => []),
  listSessions: jest.fn(() => []),
}));
jest.mock('../src/sessionMeta', () => ({
  ...jest.requireActual('../src/sessionMeta'),
  setTurnToolActivities: jest.fn(),
}));

import { runCopilotTurn } from '../src/copilotRunner';
import { getSessionCwd, getSessionHistory } from '../src/sessionHistory';
import { setTurnToolActivities } from '../src/sessionMeta';
import { config } from '../src/config';
import { runConversationTurn } from '../src/wsServer';

const mockedRunCopilotTurn = runCopilotTurn as jest.Mock;
const mockedGetSessionCwd = getSessionCwd as jest.Mock;
const mockedGetSessionHistory = getSessionHistory as jest.Mock;
const mockedSetTurnToolActivities = setTurnToolActivities as jest.Mock;

function uniqueId(label: string): string {
  return `${label}-${Math.random().toString(36).slice(2)}`;
}

describe('runConversationTurn', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockedRunCopilotTurn.mockResolvedValue({ finalText: 'ok', exitCode: 0, sessionId: 'x' });
    mockedGetSessionHistory.mockReturnValue([]);
  });

  it('persists completed tool activity against the CLI turn created by the run', async () => {
    const id = uniqueId('tool-history');
    mockedGetSessionCwd.mockReturnValue(null);
    mockedGetSessionHistory.mockReturnValue([
      { turnIndex: 7, userMessage: 'inspect it', assistantResponse: 'done', timestamp: '2026-07-24T00:00:00Z' },
    ]);
    mockedRunCopilotTurn.mockImplementationOnce(async (_id, _text, _onDelta, onToolEvent) => {
      onToolEvent({ status: 'start', toolCallId: 'call-1', name: 'view', summary: 'README.md', detail: '{"path":"README.md"}' });
      onToolEvent({ status: 'complete', toolCallId: 'call-1', name: 'view', success: true });
      return { finalText: 'done', exitCode: 0, sessionId: id };
    });

    await runConversationTurn(id, 'inspect it');

    expect(mockedSetTurnToolActivities).toHaveBeenCalledWith(id, 7, [
      { name: 'view', summary: 'README.md', detail: '{"path":"README.md"}', success: true },
    ]);
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
    // Default BROWSE_ROOTS (unset in tests) is now just config.workDir, not the home directory -
    // so the requested path needs to be a subfolder of workDir to be considered "allowed" here.
    const cwd = path.join(config.workDir, 'some-subfolder');
    await runConversationTurn(id, 'hello', { requestedCwd: cwd });
    expect(mockedRunCopilotTurn).toHaveBeenCalledWith(id, 'hello', expect.any(Function), expect.any(Function), undefined, cwd, undefined);
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
      undefined,
    );
  });

  it('uses the CLI-recorded cwd when resuming an existing session, ignoring any requestedCwd', async () => {
    const existingCwd = path.join(config.workDir, 'existing-session-folder');
    mockedGetSessionCwd.mockReturnValue(existingCwd);
    const id = uniqueId('resume');
    await runConversationTurn(id, 'hello', { requestedCwd: '/etc' });
    expect(mockedRunCopilotTurn).toHaveBeenCalledWith(
      id,
      'hello',
      expect.any(Function),
      expect.any(Function),
      undefined,
      existingCwd,
      undefined,
    );
  });

  it('passes a per-request model override to the CLI runner', async () => {
    mockedGetSessionCwd.mockReturnValue(null);
    const id = uniqueId('model-override');
    await runConversationTurn(id, 'hello', { model: 'gpt-5.4' });
    expect(mockedRunCopilotTurn).toHaveBeenCalledWith(
      id,
      'hello',
      expect.any(Function),
      expect.any(Function),
      undefined,
      config.workDir,
      'gpt-5.4',
    );
  });

  it('rejects resuming a session whose CLI-recorded cwd falls outside the allowed roots', async () => {
    // Simulates a session id that belongs to a completely unrelated tool on this same machine
    // (e.g. a different Copilot-based agent) - it must never be reachable here, even by id, just
    // because the CLI's own session-store.db happens to have a record of it.
    mockedGetSessionCwd.mockReturnValue('/some/other/tools/workspace');
    const id = uniqueId('out-of-scope');
    await expect(runConversationTurn(id, 'hello', {})).rejects.toThrow(/outside this server's configured BROWSE_ROOTS/);
    expect(mockedRunCopilotTurn).not.toHaveBeenCalled();
  });

  it('serializes two overlapping calls for the same conversationId', async () => {
    mockedGetSessionCwd.mockReturnValue(path.join(config.workDir, 'serial-folder'));
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
    mockedGetSessionCwd.mockReturnValue(path.join(config.workDir, 'reject-then-succeed-folder'));
    const id = uniqueId('reject-then-succeed');
    mockedRunCopilotTurn.mockRejectedValueOnce(new Error('boom'));
    await expect(runConversationTurn(id, 'first', {})).rejects.toThrow('boom');

    mockedRunCopilotTurn.mockResolvedValueOnce({ finalText: 'recovered', exitCode: 0, sessionId: 'x' });
    await expect(runConversationTurn(id, 'second', {})).resolves.toEqual(
      expect.objectContaining({ finalText: 'recovered' }),
    );
  });
});
