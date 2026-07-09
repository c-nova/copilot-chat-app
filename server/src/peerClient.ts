import WebSocket from 'ws';
import type { PeerServerConfig } from './config';

export interface PeerSpawnOptions {
  parentSessionId: string;
  existingSessionId?: string;
  cwd?: string;
  message?: string;
}

export interface PeerSpawnResult {
  sessionId: string;
  finalText?: string;
}

/** How long to wait for the initial WebSocket handshake with the peer before giving up. */
const CONNECT_TIMEOUT_MS = 10_000;
/** How long to wait for the peer's sessions:spawn-result overall (a brand-new child's first turn can take a while - same generous budget as the client's own SpawnChildSessionAsync). */
const OPERATION_TIMEOUT_MS = 5 * 60 * 1000;

/**
 * PBI-026: dispatches a sessions:spawn request to a peer server by opening a short-lived
 * WebSocket connection to it and reusing the exact same client protocol
 * (protocol.ts's ClientSessionsSpawnMessage/ServerSessionsSpawnResultMessage) any MAUI client
 * already speaks - this server just also acts as a client of its peer for this one call, rather
 * than needing a whole separate cross-server API/protocol. Peers are configured via the
 * PEER_SERVERS env var (see config.ts) - a small, fixed set of machines the user personally owns
 * and trusts, not general internet peer discovery (see PBI.md's PBI-026 design notes for why a
 * P2P framework was considered and rejected as overkill for this).
 */
export function spawnOnPeer(peer: PeerServerConfig, options: PeerSpawnOptions): Promise<PeerSpawnResult> {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(peer.url, { headers: { Authorization: `Bearer ${peer.token}` } });
    const requestId = `peer-${Date.now()}-${Math.random().toString(36).slice(2)}`;
    let settled = false;

    const finish = (fn: () => void) => {
      if (settled) return;
      settled = true;
      clearTimeout(connectTimeout);
      clearTimeout(operationTimeout);
      try {
        ws.terminate();
      } catch {
        // already closed - fine
      }
      fn();
    };

    const connectTimeout = setTimeout(() => {
      finish(() => reject(new Error(`Timed out connecting to peer server "${peer.name}" (${peer.url}).`)));
    }, CONNECT_TIMEOUT_MS);

    const operationTimeout = setTimeout(() => {
      finish(() => reject(new Error(`Timed out waiting for peer server "${peer.name}" to respond to the spawn request.`)));
    }, OPERATION_TIMEOUT_MS);

    ws.on('open', () => {
      clearTimeout(connectTimeout);
      ws.send(
        JSON.stringify({
          type: 'sessions:spawn',
          requestId,
          parentSessionId: options.parentSessionId,
          existingSessionId: options.existingSessionId,
          cwd: options.cwd,
          message: options.message,
        }),
      );
    });

    ws.on('message', (data: WebSocket.RawData) => {
      let msg: any;
      try {
        msg = JSON.parse(data.toString());
      } catch {
        return;
      }
      if (msg?.type === 'sessions:spawn-result' && msg.requestId === requestId) {
        if (msg.ok) {
          finish(() => resolve({ sessionId: msg.sessionId, finalText: msg.finalText }));
        } else {
          finish(() => reject(new Error(msg.error ?? `Peer server "${peer.name}" refused the spawn request.`)));
        }
      }
    });

    ws.on('error', (err: Error) => {
      finish(() => reject(new Error(`Failed to reach peer server "${peer.name}" (${peer.url}): ${err.message}`)));
    });

    ws.on('close', () => {
      finish(() => reject(new Error(`Peer server "${peer.name}" closed the connection before responding.`)));
    });
  });
}

/** Looks up a configured peer by name, for the internal control API's /internal/spawn-session and /internal/peers. */
export function findPeer(peers: PeerServerConfig[], name: string): PeerServerConfig | undefined {
  return peers.find((p) => p.name === name);
}
