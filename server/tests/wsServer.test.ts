jest.mock('../src/copilotRunner', () => ({
  runCopilotTurn: jest.fn(),
}));

import { ConversationBusyError, runConversationTurn, timingSafeEqualString } from '../src/wsServer';
import { runCopilotTurn } from '../src/copilotRunner';

const mockRunCopilotTurn = runCopilotTurn as jest.Mock;

describe('timingSafeEqualString', () => {
  it('returns true for identical strings', () => {
    expect(timingSafeEqualString('super-secret-token', 'super-secret-token')).toBe(true);
  });

  it('returns false for different strings of the same length', () => {
    expect(timingSafeEqualString('super-secret-token', 'super-secret-tokeX')).toBe(false);
  });

  it('returns false for strings of different lengths', () => {
    expect(timingSafeEqualString('short', 'much-longer-token')).toBe(false);
  });

  it('returns false when compared against an empty string', () => {
    expect(timingSafeEqualString('non-empty', '')).toBe(false);
  });

  it('returns true for two empty strings', () => {
    expect(timingSafeEqualString('', '')).toBe(true);
  });
});

describe('runConversationTurn rejectIfBusy', () => {
  beforeEach(() => {
    mockRunCopilotTurn.mockReset();
  });

  it('rejects immediately with ConversationBusyError instead of queueing when a turn is already running', async () => {
    let resolveFirst!: (value: unknown) => void;
    mockRunCopilotTurn.mockImplementationOnce(
      () => new Promise((resolve) => { resolveFirst = resolve; }),
    );

    const conversationId = `busy-test-${Date.now()}`;
    const firstCall = runConversationTurn(conversationId, 'first message');

    // Let the first call's microtasks run so it actually marks itself active before we fire the
    // second one - otherwise there'd be a race on whether activeConversationTurns is set yet.
    await new Promise((r) => setImmediate(r));

    await expect(
      runConversationTurn(conversationId, 'second message', { rejectIfBusy: true }),
    ).rejects.toThrow(ConversationBusyError);

    resolveFirst({ finalText: 'first done' });
    await expect(firstCall).resolves.toEqual({ finalText: 'first done' });
  });

  it('does not reject a rejectIfBusy call once the prior turn for that conversation has finished', async () => {
    mockRunCopilotTurn.mockResolvedValueOnce({ finalText: 'done 1' });
    mockRunCopilotTurn.mockResolvedValueOnce({ finalText: 'done 2' });

    const conversationId = `busy-test-2-${Date.now()}`;
    await runConversationTurn(conversationId, 'first message');

    await expect(
      runConversationTurn(conversationId, 'second message', { rejectIfBusy: true }),
    ).resolves.toEqual({ finalText: 'done 2' });
  });

  it('still queues normally (no rejection) when rejectIfBusy is not set, even while a turn is active', async () => {
    let resolveFirst!: (value: unknown) => void;
    mockRunCopilotTurn.mockImplementationOnce(
      () => new Promise((resolve) => { resolveFirst = resolve; }),
    );
    mockRunCopilotTurn.mockResolvedValueOnce({ finalText: 'second done' });

    const conversationId = `busy-test-3-${Date.now()}`;
    const firstCall = runConversationTurn(conversationId, 'first message');
    await new Promise((r) => setImmediate(r));

    const secondCall = runConversationTurn(conversationId, 'second message');
    resolveFirst({ finalText: 'first done' });

    await expect(firstCall).resolves.toEqual({ finalText: 'first done' });
    await expect(secondCall).resolves.toEqual({ finalText: 'second done' });
  });
});
