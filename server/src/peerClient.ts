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
    // PBI-028 follow-up: times the whole round-trip (connect + the peer's own copilot CLI turn) so
    // it can be compared against that peer's own `[copilotRunner] ... turn finished in Xms` log
    // line - separates "the network/peer overhead is slow" from "the peer's copilot CLI itself is
    // slow" (e.g. AV/EDR scanning every spawned process on a corporate-managed machine) without
    // needing to guess next time a cross-server turn feels slow.
    const startedAt = Date.now();
    let settled = false;

    const finish = (fn: () => void) => {
      if (settled) return;
      settled = true;
      console.log(`[peerClient] spawnOnPeer to "${peer.name}" settled in ${Date.now() - startedAt}ms`);
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
      // A peer running an older build that doesn't understand sessions:spawn yet falls through
      // to wsServer.ts's generic "Expected { type: chat, ... }" error response instead of a
      // sessions:spawn-result - without this check we'd silently ignore that (it doesn't match
      // the type/requestId check below) and hang until the operation timeout instead of
      // surfacing the real reason immediately.
      if (msg?.type === 'error' && (msg.requestId === undefined || msg.requestId === requestId)) {
        finish(() =>
          reject(
            new Error(
              `Peer server "${peer.name}" rejected the request: ${msg.message ?? 'unknown error'} (is it running an older build that doesn't support sessions:spawn yet?)`,
            ),
          ),
        );
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

/** How long to wait for a peer to respond to a read-only query like sessions:children - much shorter than a spawn, since this never waits on an actual AI turn. */
const QUERY_TIMEOUT_MS = 10_000;

/**
 * PBI-028: asks a peer server which sessions it has recorded as children of `parentSessionId` -
 * same short-lived-connection/existing-protocol-reuse approach as spawnOnPeer, but for the
 * read-only sessions:children query instead. Used by internalControlApi.ts's /internal/children so
 * the session-control MCP's list_my_children tool can see children spawned cross-server (PBI-026),
 * not just ones on this same machine.
 */
export function listChildrenOnPeer(peer: PeerServerConfig, parentSessionId: string): Promise<any[]> {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(peer.url, { headers: { Authorization: `Bearer ${peer.token}` } });
    const requestId = `peer-children-${Date.now()}-${Math.random().toString(36).slice(2)}`;
    let settled = false;

    const finish = (fn: () => void) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      try {
        ws.terminate();
      } catch {
        // already closed - fine
      }
      fn();
    };

    const timeout = setTimeout(() => {
      finish(() => reject(new Error(`Timed out asking peer server "${peer.name}" for its children.`)));
    }, QUERY_TIMEOUT_MS);

    ws.on('open', () => {
      ws.send(JSON.stringify({ type: 'sessions:children', requestId, parentSessionId }));
    });

    ws.on('message', (data: WebSocket.RawData) => {
      let msg: any;
      try {
        msg = JSON.parse(data.toString());
      } catch {
        return;
      }
      if (msg?.type === 'error' && (msg.requestId === undefined || msg.requestId === requestId)) {
        finish(() => reject(new Error(`Peer server "${peer.name}" rejected the request: ${msg.message ?? 'unknown error'}`)));
        return;
      }
      if (msg?.type === 'sessions:children-result' && msg.requestId === requestId) {
        if (msg.ok) {
          finish(() => resolve(Array.isArray(msg.sessions) ? msg.sessions : []));
        } else {
          finish(() => reject(new Error(msg.error ?? `Peer server "${peer.name}" refused the children request.`)));
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
