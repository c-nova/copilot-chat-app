import * as http from 'http';
import { config } from './config';
import { findPeer, listChildrenOnPeer, spawnOnPeer } from './peerClient';
import { getSessionHistory, getSessionSummary, listSessions } from './sessionHistory';
import { getChildSessionIds, getSessionMeta, markSessionControlTurn } from './sessionMeta';
import { ConversationBusyError, runConversationTurn, spawnChildSession, timingSafeEqualString } from './wsServer';

const MAX_BODY_BYTES = 1_000_000;

function isLoopbackAddress(addr: string | undefined): boolean {
  return addr === '127.0.0.1' || addr === '::1' || addr === '::ffff:127.0.0.1';
}

function readJsonBody(req: http.IncomingMessage): Promise<any> {
  return new Promise((resolve, reject) => {
    let raw = '';
    let tooLarge = false;
    req.on('data', (chunk: Buffer) => {
      raw += chunk.toString('utf8');
      if (raw.length > MAX_BODY_BYTES) {
        tooLarge = true;
        req.destroy();
      }
    });
    req.on('end', () => {
      if (tooLarge) {
        reject(new Error('Request body too large'));
        return;
      }
      if (!raw) {
        resolve({});
        return;
      }
      try {
        resolve(JSON.parse(raw));
      } catch {
        reject(new Error('Invalid JSON body'));
      }
    });
    req.on('error', reject);
  });
}

function sendJson(res: http.ServerResponse, status: number, body: unknown): void {
  const payload = JSON.stringify(body);
  res.writeHead(status, { 'Content-Type': 'application/json' });
  res.end(payload);
}

function isAuthorized(req: http.IncomingMessage): boolean {
  const header = req.headers['authorization'];
  if (typeof header !== 'string') return false;
  const match = header.match(/^Bearer\s+(.+)$/i);
  if (!match) return false;
  return timingSafeEqualString(match[1], config.authToken);
}

/**
 * A same-machine-only control plane for the session-control MCP server (Phase 3 "controller
 * session"): the MCP server runs as a separate stdio process (per the `copilot mcp add` model) and
 * shares no memory with this server, so it reaches back into the *same* conversationLocks/
 * conversationSessions state via this HTTP API instead of spawning its own `copilot` process
 * directly. That's what prevents a controller session and a human both driving the same
 * underlying session's CLI process at the same time.
 *
 * Bound to 127.0.0.1 only - never reachable from the network, even if PORT/the main WebSocket
 * server is exposed beyond localhost. Also checks the remote address defensively on every request
 * (in case this ever runs behind a misconfigured proxy) and requires the same Bearer auth token as
 * the main WebSocket server.
 */
export function createInternalControlApi(): http.Server {
  const server = http.createServer(async (req, res) => {
    try {
      if (!isLoopbackAddress(req.socket.remoteAddress)) {
        sendJson(res, 403, { ok: false, error: 'Forbidden (loopback only)' });
        return;
      }
      if (!isAuthorized(req)) {
        sendJson(res, 401, { ok: false, error: 'Unauthorized' });
        return;
      }

      if (req.method === 'GET' && req.url === '/internal/sessions') {
        const sessions = listSessions([...config.browseRoots, config.workDir]).map((s) => {
          const meta = getSessionMeta(s.id);
          return { ...s, label: meta?.label, archived: meta?.archived ?? false };
        });
        sendJson(res, 200, { ok: true, sessions });
        return;
      }

      if (req.method === 'GET' && req.url?.startsWith('/internal/sessions/')) {
        const sessionId = decodeURIComponent(req.url.slice('/internal/sessions/'.length));
        if (!sessionId) {
          sendJson(res, 400, { ok: false, error: 'sessionId is required' });
          return;
        }
        const turns = getSessionHistory(sessionId);
        const meta = getSessionMeta(sessionId);
        const sessionControlTurnIndexes = new Set(meta?.sessionControlTurnIndexes ?? []);
        const dtoTurns = turns.map((t) => ({
          ...t,
          ...(sessionControlTurnIndexes.has(t.turnIndex) ? { fromOtherSession: true } : {}),
          ...(meta?.toolActivitiesByTurnIndex?.[String(t.turnIndex)]
            ? { toolActivities: meta.toolActivitiesByTurnIndex[String(t.turnIndex)] }
            : {}),
        }));
        sendJson(res, 200, { ok: true, sessionId, turns: dtoTurns });
        return;
      }

      if (req.method === 'POST' && req.url === '/internal/run-turn') {
        const body = await readJsonBody(req);
        const sessionId = body?.sessionId;
        const message = body?.message;
        if (typeof sessionId !== 'string' || !sessionId || typeof message !== 'string' || !message) {
          sendJson(res, 400, { ok: false, error: 'sessionId and message are required' });
          return;
        }
        try {
          // rejectIfBusy: true - a session-control dispatch must never silently queue behind a
          // turn a human is actively running via the app (see wsServer.ts design notes); fail fast
          // with a clear 409 instead so the calling session can tell its user to try again later.
          const result = await runConversationTurn(sessionId, message, {
            requireExistingSession: true,
            rejectIfBusy: true,
          });
          // Mark the turn we just created so the client can show "message from another session"
          // instead of it looking like this session's own human user typed it. The turn we just ran
          // is always the last one in history at this point - nothing else can have appended to
          // this same conversationId between our runConversationTurn call resolving and this read,
          // since rejectIfBusy guarantees no other session-control dispatch could have been racing.
          const turnsAfter = getSessionHistory(sessionId);
          const lastTurn = turnsAfter[turnsAfter.length - 1];
          if (lastTurn) {
            markSessionControlTurn(sessionId, lastTurn.turnIndex);
          }
          sendJson(res, 200, { ok: true, finalText: result.finalText });
        } catch (err: any) {
          if (err?.name === 'ConversationBusyError' || err instanceof ConversationBusyError) {
            sendJson(res, 409, { ok: false, error: err.message });
            return;
          }
          throw err;
        }
        return;
      }

      if (req.method === 'POST' && req.url === '/internal/spawn-session') {
        const body = await readJsonBody(req);
        const parentSessionId = body?.parentSessionId;
        if (typeof parentSessionId !== 'string' || !parentSessionId) {
          sendJson(res, 400, { ok: false, error: 'parentSessionId is required' });
          return;
        }
        const targetPeer = typeof body?.targetPeer === 'string' && body.targetPeer ? body.targetPeer : undefined;
        try {
          const spawnOptions = {
            existingSessionId: typeof body?.existingSessionId === 'string' ? body.existingSessionId : undefined,
            cwd: typeof body?.cwd === 'string' ? body.cwd : undefined,
            message: typeof body?.message === 'string' ? body.message : undefined,
          };
          let result: { sessionId: string; finalText?: string };
          if (targetPeer) {
            // PBI-026: dispatch to a configured peer server (cross-machine) instead of spawning
            // locally - see peerClient.ts's design notes.
            const peer = findPeer(config.peers, targetPeer);
            if (!peer) {
              sendJson(res, 400, { ok: false, error: `Unknown peer server "${targetPeer}" - check PEER_SERVERS config.` });
              return;
            }
            result = await spawnOnPeer(peer, { parentSessionId, ...spawnOptions });
          } else {
            result = await spawnChildSession({ parentSessionId, ...spawnOptions });
          }
          sendJson(res, 200, { ok: true, sessionId: result.sessionId, finalText: result.finalText });
        } catch (err: any) {
          if (err?.name === 'ConversationBusyError' || err instanceof ConversationBusyError) {
            sendJson(res, 409, { ok: false, error: err.message });
            return;
          }
          throw err;
        }
        return;
      }

      if (req.method === 'GET' && req.url === '/internal/peers') {
        // Names only - never tokens - since this is what the session-control MCP subprocess reads
        // to build the list_servers tool's response, and that response can end up in front of the
        // model (see sessionControlMcpServer.ts).
        sendJson(res, 200, { ok: true, peers: config.peers.map((p) => ({ name: p.name })) });
        return;
      }

      if (req.method === 'GET' && req.url?.startsWith('/internal/children')) {
        // PBI-028: lets the session-control MCP's list_my_children tool discover sessions already
        // spawned under the caller, so it can reuse one (via spawn_session's existingSessionId)
        // instead of creating a brand-new child for every follow-up message - without this, the
        // model has no way to know a suitable child already exists and ends up spawning a fresh one
        // each time. Aggregates this server's own children (sessionMeta.ts) with every configured
        // peer's (PBI-026) - a child spawned cross-server only ever has its parentSessionId recorded
        // on whichever server it actually runs on, never on the parent's own server.
        const url = new URL(req.url, 'http://localhost');
        const parentSessionId = url.searchParams.get('parentSessionId');
        if (!parentSessionId) {
          sendJson(res, 400, { ok: false, error: 'parentSessionId query parameter is required' });
          return;
        }
        const localChildren = getChildSessionIds(parentSessionId)
          .map((id) => {
            const summary = getSessionSummary(id);
            if (!summary) return null;
            const meta = getSessionMeta(id);
            return { ...summary, label: meta?.label, archived: meta?.archived ?? false };
          })
          .filter((s): s is NonNullable<typeof s> => s !== null);

        const peerResults = await Promise.allSettled(config.peers.map((peer) => listChildrenOnPeer(peer, parentSessionId)));
        const peerChildren = peerResults.flatMap((r) => (r.status === 'fulfilled' ? r.value : []));

        sendJson(res, 200, { ok: true, sessions: [...localChildren, ...peerChildren] });
        return;
      }

      sendJson(res, 404, { ok: false, error: 'Not found' });
    } catch (err: any) {
      sendJson(res, 500, { ok: false, error: err?.message ?? String(err) });
    }
  });

  server.listen(config.internalControlPort, '127.0.0.1', () => {
    console.log(`Internal control API listening on http://127.0.0.1:${config.internalControlPort} (loopback only)`);
  });

  return server;
}
