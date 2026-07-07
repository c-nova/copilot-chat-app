// notify.ts reads config.ntfyTopic/config.ntfyServer at call time (via the shared config module),
// so tests control behavior purely by resetting modules and re-requiring config with different
// NTFY_TOPIC/NTFY_SERVER env vars set - same pattern as config.test.ts.
jest.mock('dotenv', () => ({ config: jest.fn() }));

describe('notifyReplyReady', () => {
  const originalNtfyTopic = process.env.NTFY_TOPIC;
  const originalNtfyServer = process.env.NTFY_SERVER;
  let fetchMock: jest.Mock;

  beforeEach(() => {
    fetchMock = jest.fn().mockResolvedValue({ ok: true, status: 200, statusText: 'OK' });
    (global as any).fetch = fetchMock;
  });

  afterEach(() => {
    process.env.NTFY_TOPIC = originalNtfyTopic;
    process.env.NTFY_SERVER = originalNtfyServer;
    jest.resetModules();
    jest.restoreAllMocks();
  });

  it('does nothing (no fetch call) when NTFY_TOPIC is unset', async () => {
    delete process.env.NTFY_TOPIC;
    jest.resetModules();
    const { notifyReplyReady } = require('../src/notify');

    await notifyReplyReady('Hello from Copilot');

    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('POSTs the reply text to the configured ntfy topic on the default server', async () => {
    process.env.NTFY_TOPIC = 'my-secret-topic';
    delete process.env.NTFY_SERVER;
    jest.resetModules();
    const { notifyReplyReady } = require('../src/notify');

    await notifyReplyReady('Hello from Copilot');

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, options] = fetchMock.mock.calls[0];
    expect(url).toBe('https://ntfy.sh/my-secret-topic');
    expect(options.method).toBe('POST');
    expect(options.body).toBe('Hello from Copilot');
    expect(options.headers.Title).toBe('Copilot replied');
  });

  it('publishes to a self-hosted NTFY_SERVER when configured', async () => {
    process.env.NTFY_TOPIC = 'my-topic';
    process.env.NTFY_SERVER = 'https://ntfy.example.com/';
    jest.resetModules();
    const { notifyReplyReady } = require('../src/notify');

    await notifyReplyReady('hi');

    const [url] = fetchMock.mock.calls[0];
    expect(url).toBe('https://ntfy.example.com/my-topic');
  });

  it('collapses whitespace and truncates long replies', async () => {
    process.env.NTFY_TOPIC = 'my-topic';
    jest.resetModules();
    const { notifyReplyReady } = require('../src/notify');

    await notifyReplyReady('line one\n\n   line two   \n' + 'x'.repeat(500));

    const [, options] = fetchMock.mock.calls[0];
    expect(options.body.length).toBeLessThanOrEqual(400);
    expect(options.body.startsWith('line one line two')).toBe(true);
  });

  it('falls back to a placeholder for an empty/whitespace-only reply', async () => {
    process.env.NTFY_TOPIC = 'my-topic';
    jest.resetModules();
    const { notifyReplyReady } = require('../src/notify');

    await notifyReplyReady('   ');

    const [, options] = fetchMock.mock.calls[0];
    expect(options.body).toBe('(empty reply)');
  });

  it('swallows a fetch rejection instead of throwing', async () => {
    process.env.NTFY_TOPIC = 'my-topic';
    jest.resetModules();
    fetchMock.mockRejectedValue(new Error('network down'));
    (global as any).fetch = fetchMock;
    const { notifyReplyReady } = require('../src/notify');

    await expect(notifyReplyReady('hi')).resolves.toBeUndefined();
  });

  it('logs but does not throw when ntfy responds with a non-OK status', async () => {
    process.env.NTFY_TOPIC = 'my-topic';
    jest.resetModules();
    fetchMock.mockResolvedValue({ ok: false, status: 403, statusText: 'Forbidden' });
    (global as any).fetch = fetchMock;
    const { notifyReplyReady } = require('../src/notify');

    await expect(notifyReplyReady('hi')).resolves.toBeUndefined();
  });
});
