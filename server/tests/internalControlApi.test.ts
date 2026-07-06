import * as http from 'http';

process.env.INTERNAL_CONTROL_PORT = '0';

jest.mock('../src/wsServer', () => {
  const actual = jest.requireActual('../src/wsServer');
  return { ...actual, runConversationTurn: jest.fn() };
});
jest.mock('../src/sessionHistory', () => ({
  listSessions: jest.fn(() => [
    { id: 's1', cwd: '/tmp', summary: 'hi', createdAt: 'a', updatedAt: 'b', turnCount: 1 },
  ]),
  getSessionHistory: jest.fn(() => [
    { turnIndex: 0, userMessage: 'hi', assistantResponse: 'yo', timestamp: 't' },
  ]),
}));
jest.mock('../src/sessionMeta', () => ({
  getSessionMeta: jest.fn(() => undefined),
}));

import { config } from '../src/config';
import { createInternalControlApi } from '../src/internalControlApi';
import { runConversationTurn } from '../src/wsServer';

const mockedRunConversationTurn = runConversationTurn as jest.Mock;

describe('internal control API', () => {
  let server: ReturnType<typeof createInternalControlApi>;
  let port: number;

  beforeAll((done) => {
    server = createInternalControlApi();
    server.on('listening', () => {
      const addr = server.address();
      port = typeof addr === 'object' && addr ? addr.port : 0;
      done();
    });
  });

  afterAll((done) => {
    server.close(() => done());
  });

  beforeEach(() => {
    jest.clearAllMocks();
  });

  function request(
    method: string,
    reqPath: string,
    body?: unknown,
    token = config.authToken,
  ): Promise<{ status: number; json: any }> {
    return new Promise((resolve, reject) => {
      const payload = body !== undefined ? JSON.stringify(body) : undefined;
      const req = http.request(
        {
          hostname: '127.0.0.1',
          port,
          path: reqPath,
          method,
          headers: {
            Authorization: `Bearer ${token}`,
            'Content-Type': 'application/json',
            ...(payload ? { 'Content-Length': Buffer.byteLength(payload) } : {}),
          },
        },
        (res) => {
          let raw = '';
          res.on('data', (chunk: Buffer) => {
            raw += chunk.toString('utf8');
          });
          res.on('end', () => {
            resolve({ status: res.statusCode ?? 0, json: raw ? JSON.parse(raw) : undefined });
          });
        },
      );
      req.on('error', reject);
      if (payload) req.write(payload);
      req.end();
    });
  }

  it('rejects requests without a valid auth token', async () => {
    const res = await request('GET', '/internal/sessions', undefined, 'wrong-token');
    expect(res.status).toBe(401);
  });

  it('lists sessions with sidecar meta merged in', async () => {
    const res = await request('GET', '/internal/sessions');
    expect(res.status).toBe(200);
    expect(res.json.ok).toBe(true);
    expect(res.json.sessions[0].id).toBe('s1');
    expect(res.json.sessions[0].archived).toBe(false);
  });

  it('returns session history for a specific session id', async () => {
    const res = await request('GET', '/internal/sessions/s1');
    expect(res.status).toBe(200);
    expect(res.json.turns).toHaveLength(1);
  });

  it('runs a turn via runConversationTurn (requiring an existing session) and returns finalText', async () => {
    mockedRunConversationTurn.mockResolvedValue({ finalText: 'done', exitCode: 0, sessionId: 's1' });
    const res = await request('POST', '/internal/run-turn', { sessionId: 's1', message: 'hi' });
    expect(res.status).toBe(200);
    expect(res.json.finalText).toBe('done');
    expect(mockedRunConversationTurn).toHaveBeenCalledWith('s1', 'hi', { requireExistingSession: true });
  });

  it('returns 400 when sessionId/message are missing from run-turn', async () => {
    const res = await request('POST', '/internal/run-turn', { sessionId: 's1' });
    expect(res.status).toBe(400);
  });

  it('returns 404 for unknown routes', async () => {
    const res = await request('GET', '/nope');
    expect(res.status).toBe(404);
  });

  it('propagates an error from runConversationTurn as a 500', async () => {
    mockedRunConversationTurn.mockRejectedValue(new Error('kaboom'));
    const res = await request('POST', '/internal/run-turn', { sessionId: 's1', message: 'hi' });
    expect(res.status).toBe(500);
    expect(res.json.error).toBe('kaboom');
  });
});
