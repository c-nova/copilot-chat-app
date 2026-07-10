import { EventEmitter } from 'events';
import type { PeerServerConfig } from '../src/config';

/**
 * Minimal fake WebSocket (EventEmitter-based, matching the subset of the `ws` API peerClient.ts
 * actually uses: `on`, `send`, `terminate`) so these tests can drive open/message/error/close
 * without a real network connection or a real peer server running.
 */
class FakeWebSocket extends EventEmitter {
  sent: string[] = [];
  terminated = false;
  constructor(public url: string, public opts: any) {
    super();
  }
  send(data: string) {
    this.sent.push(data);
  }
  terminate() {
    this.terminated = true;
  }
}

let lastFakeSocket: FakeWebSocket | undefined;

jest.mock('ws', () => {
  return jest.fn().mockImplementation((url: string, opts: any) => {
    lastFakeSocket = new (require('events').EventEmitter)();
    Object.assign(lastFakeSocket, { url, opts, sent: [], terminated: false });
    (lastFakeSocket as any).send = (data: string) => (lastFakeSocket as any).sent.push(data);
    (lastFakeSocket as any).terminate = () => {
      (lastFakeSocket as any).terminated = true;
    };
    return lastFakeSocket;
  });
});

import { spawnOnPeer, findPeer, listChildrenOnPeer } from '../src/peerClient';

const peer: PeerServerConfig = { name: 'windows-pc', url: 'ws://192.168.1.50:5219', token: 'peer-token-123' };

describe('spawnOnPeer', () => {
  afterEach(() => {
    lastFakeSocket = undefined;
    jest.clearAllMocks();
  });

  it('sends a sessions:spawn message with a Bearer auth header once open, and resolves on a matching sessions:spawn-result', async () => {
    const promise = spawnOnPeer(peer, { parentSessionId: 'parent-1', message: 'do the thing' });

    // Let the mocked constructor run and the socket get wired up.
    await new Promise((r) => setImmediate(r));
    const sock = lastFakeSocket!;
    expect(sock.opts).toEqual({ headers: { Authorization: 'Bearer peer-token-123' } });

    sock.emit('open');
    expect(sock.sent).toHaveLength(1);
    const sent = JSON.parse(sock.sent[0]);
    expect(sent).toMatchObject({ type: 'sessions:spawn', parentSessionId: 'parent-1', message: 'do the thing' });

    sock.emit('message', Buffer.from(JSON.stringify({ type: 'sessions:spawn-result', requestId: sent.requestId, ok: true, sessionId: 'child-1', finalText: 'done!' })));

    await expect(promise).resolves.toEqual({ sessionId: 'child-1', finalText: 'done!' });
  });

  it('rejects when the peer responds with ok: false', async () => {
    const promise = spawnOnPeer(peer, { parentSessionId: 'parent-1', message: 'do the thing' });
    await new Promise((r) => setImmediate(r));
    const sock = lastFakeSocket!;
    sock.emit('open');
    const sent = JSON.parse(sock.sent[0]);

    sock.emit('message', Buffer.from(JSON.stringify({ type: 'sessions:spawn-result', requestId: sent.requestId, ok: false, error: 'busy' })));

    await expect(promise).rejects.toThrow('busy');
  });

  it('ignores messages for a different requestId', async () => {
    const promise = spawnOnPeer(peer, { parentSessionId: 'parent-1', message: 'hi' });
    await new Promise((r) => setImmediate(r));
    const sock = lastFakeSocket!;
    sock.emit('open');

    sock.emit('message', Buffer.from(JSON.stringify({ type: 'sessions:spawn-result', requestId: 'not-mine', ok: true, sessionId: 'wrong' })));
    const sent = JSON.parse(sock.sent[0]);
    sock.emit('message', Buffer.from(JSON.stringify({ type: 'sessions:spawn-result', requestId: sent.requestId, ok: true, sessionId: 'right' })));

    await expect(promise).resolves.toEqual({ sessionId: 'right', finalText: undefined });
  });

  it('rejects when the socket errors', async () => {
    const promise = spawnOnPeer(peer, { parentSessionId: 'parent-1', message: 'hi' });
    await new Promise((r) => setImmediate(r));
    const sock = lastFakeSocket!;
    sock.emit('error', new Error('ECONNREFUSED'));

    await expect(promise).rejects.toThrow(/Failed to reach peer server "windows-pc"/);
  });

  it('rejects if the connection closes before a result arrives', async () => {
    const promise = spawnOnPeer(peer, { parentSessionId: 'parent-1', message: 'hi' });
    await new Promise((r) => setImmediate(r));
    const sock = lastFakeSocket!;
    sock.emit('open');
    sock.emit('close');

    await expect(promise).rejects.toThrow(/closed the connection/);
  });

  it('rejects immediately (rather than hanging until the timeout) when the peer is running an older build that replies with a generic error instead of sessions:spawn-result', async () => {
    const promise = spawnOnPeer(peer, { parentSessionId: 'parent-1', message: 'hi' });
    await new Promise((r) => setImmediate(r));
    const sock = lastFakeSocket!;
    sock.emit('open');

    // Old wsServer.ts builds without sessions:spawn support fall through to this generic error,
    // with no requestId at all (see wsServer.ts's final fallback).
    sock.emit('message', Buffer.from(JSON.stringify({ type: 'error', message: 'Expected { type: "chat", conversationId, text }' })));

    await expect(promise).rejects.toThrow(/older build/);
  });
});

describe('findPeer', () => {
  it('finds a peer by name', () => {
    expect(findPeer([peer], 'windows-pc')).toBe(peer);
  });

  it('returns undefined for an unknown name', () => {
    expect(findPeer([peer], 'nonexistent')).toBeUndefined();
  });
});

describe('listChildrenOnPeer', () => {
  afterEach(() => {
    lastFakeSocket = undefined;
    jest.clearAllMocks();
  });

  it('sends a sessions:children message and resolves with the returned sessions array', async () => {
    const promise = listChildrenOnPeer(peer, 'parent-1');
    await new Promise((r) => setImmediate(r));
    const sock = lastFakeSocket!;
    sock.emit('open');

    expect(sock.sent).toHaveLength(1);
    const sent = JSON.parse(sock.sent[0]);
    expect(sent).toMatchObject({ type: 'sessions:children', parentSessionId: 'parent-1' });

    const children = [{ id: 'child-1', summary: 'hi' }];
    sock.emit('message', Buffer.from(JSON.stringify({ type: 'sessions:children-result', requestId: sent.requestId, ok: true, sessions: children })));

    await expect(promise).resolves.toEqual(children);
  });

  it('resolves with an empty array when the peer returns no sessions field', async () => {
    const promise = listChildrenOnPeer(peer, 'parent-1');
    await new Promise((r) => setImmediate(r));
    const sock = lastFakeSocket!;
    sock.emit('open');
    const sent = JSON.parse(sock.sent[0]);

    sock.emit('message', Buffer.from(JSON.stringify({ type: 'sessions:children-result', requestId: sent.requestId, ok: true })));

    await expect(promise).resolves.toEqual([]);
  });

  it('rejects when the peer responds with ok: false', async () => {
    const promise = listChildrenOnPeer(peer, 'parent-1');
    await new Promise((r) => setImmediate(r));
    const sock = lastFakeSocket!;
    sock.emit('open');
    const sent = JSON.parse(sock.sent[0]);

    sock.emit('message', Buffer.from(JSON.stringify({ type: 'sessions:children-result', requestId: sent.requestId, ok: false, error: 'nope' })));

    await expect(promise).rejects.toThrow('nope');
  });

  it('rejects when the socket errors', async () => {
    const promise = listChildrenOnPeer(peer, 'parent-1');
    await new Promise((r) => setImmediate(r));
    const sock = lastFakeSocket!;
    sock.emit('error', new Error('ECONNREFUSED'));

    await expect(promise).rejects.toThrow(/Failed to reach peer server "windows-pc"/);
  });
});
