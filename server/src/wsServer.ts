import * as crypto from 'crypto';
import { IncomingMessage } from 'http';
import { WebSocket, WebSocketServer } from 'ws';
import { config } from './config';
import { runCopilotTurn } from './copilotRunner';
import { addMcpServer, listMcpServers, removeMcpServer } from './mcpManager';
import { ClientMessage, ServerMessage } from './protocol';
import { getSessionHistory, listWorkspaceSessions } from './sessionHistory';

function send(ws: WebSocket, msg: ServerMessage) {
  if (ws.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify(msg));
  }
}

/** Constant-time string comparison to avoid leaking token length/content via response-timing side channels. */
export function timingSafeEqualString(a: string, b: string): boolean {
  const bufA = Buffer.from(a);
  const bufB = Buffer.from(b);
  if (bufA.length !== bufB.length) {
    // Still run timingSafeEqual against a same-length buffer so the "different length" case
    // doesn't short-circuit noticeably faster than the "same length, different content" case.
    crypto.timingSafeEqual(bufA, bufA);
    return false;
  }
  return crypto.timingSafeEqual(bufA, bufB);
}

function isAuthorized(req: IncomingMessage): boolean {
  const header = req.headers['authorization'];
  if (typeof header !== 'string') {
    console.warn('[auth] rejected: no Authorization header sent');
    return false;
  }
  const match = header.match(/^Bearer\s+(.+)$/i);
  if (!match) {
    console.warn('[auth] rejected: Authorization header not in "Bearer <token>" format');
    return false;
  }
  const provided = match[1];
  const ok = timingSafeEqualString(provided, config.authToken);
  if (!ok) {
    console.warn('[auth] rejected: token mismatch');
  }
  return ok;
}

export function createChatServer(): WebSocketServer {
  const wss = new WebSocketServer({ port: config.port, verifyClient: (info, done) => {
    if (isAuthorized(info.req)) {
      done(true);
    } else {
      done(false, 401, 'Unauthorized');
    }
  }});

  // conversationId (client-chosen) -> copilot CLI session id (server-generated, stable per conversation)
  const conversationSessions = new Map<string, string>();
  // Serialize turns per conversation so we never run two overlapping CLI invocations for the same session id.
  const conversationLocks = new Map<string, Promise<void>>();

  wss.on('connection', (ws) => {
    // Tracks which conversationIds this socket has driven turns for, so we can clean up the
    // shared maps when the socket disconnects instead of letting them grow forever.
    const conversationIdsSeenOnThisSocket = new Set<string>();

    ws.on('close', () => {
      for (const id of conversationIdsSeenOnThisSocket) {
        conversationLocks.delete(id);
        conversationSessions.delete(id);
      }
    });

    ws.on('message', async (raw) => {
      let msg: ClientMessage;
      try {
        msg = JSON.parse(raw.toString());
      } catch {
        send(ws, { type: 'error', message: 'Invalid JSON message' });
        return;
      }

      if (msg.type === 'mcp:list') {
        try {
          const servers = await listMcpServers();
          send(ws, { type: 'mcp:result', requestId: msg.requestId, action: 'list', ok: true, servers });
        } catch (err: any) {
          send(ws, { type: 'mcp:result', requestId: msg.requestId, action: 'list', ok: false, error: err?.message ?? String(err) });
        }
        return;
      }

      if (msg.type === 'mcp:add') {
        try {
          const server = await addMcpServer({
            name: msg.name,
            transport: msg.transport,
            command: msg.command,
            args: msg.args,
            url: msg.url,
            env: msg.env,
            headers: msg.headers,
          });
          send(ws, { type: 'mcp:result', requestId: msg.requestId, action: 'add', ok: true, server });
        } catch (err: any) {
          send(ws, { type: 'mcp:result', requestId: msg.requestId, action: 'add', ok: false, error: err?.message ?? String(err) });
        }
        return;
      }

      if (msg.type === 'mcp:remove') {
        try {
          await removeMcpServer(msg.name);
          send(ws, { type: 'mcp:result', requestId: msg.requestId, action: 'remove', ok: true });
        } catch (err: any) {
          send(ws, { type: 'mcp:result', requestId: msg.requestId, action: 'remove', ok: false, error: err?.message ?? String(err) });
        }
        return;
      }

      if (msg.type === 'sessions:list') {
        try {
          const sessions = listWorkspaceSessions(config.workDir);
          send(ws, { type: 'sessions:list-result', requestId: msg.requestId, ok: true, sessions });
        } catch (err: any) {
          send(ws, { type: 'sessions:list-result', requestId: msg.requestId, ok: false, error: err?.message ?? String(err) });
        }
        return;
      }

      if (msg.type === 'sessions:history') {
        try {
          const turns = getSessionHistory(msg.sessionId);
          send(ws, { type: 'sessions:history-result', requestId: msg.requestId, ok: true, sessionId: msg.sessionId, turns });
        } catch (err: any) {
          send(ws, { type: 'sessions:history-result', requestId: msg.requestId, ok: false, error: err?.message ?? String(err) });
        }
        return;
      }

      if (msg.type !== 'chat' || !msg.conversationId || typeof msg.text !== 'string') {
        send(ws, { type: 'error', message: 'Expected { type: "chat", conversationId, text }' });
        return;
      }

      const { conversationId, text, attachments } = msg;
      conversationIdsSeenOnThisSocket.add(conversationId);
      const prior = conversationLocks.get(conversationId) ?? Promise.resolve();
      const turn = prior
        .catch(() => undefined)
        .then(async () => {
          // The client-provided conversationId doubles directly as the Copilot CLI --session-id:
          // the CLI creates a new session if it doesn't exist yet, or resumes it if it does. This
          // lets the client "resume" any past session just by reusing its id as the conversationId.
          let sessionId = conversationSessions.get(conversationId);
          if (!sessionId) {
            sessionId = conversationId;
            conversationSessions.set(conversationId, sessionId);
          }
          try {
            const result = await runCopilotTurn(
              sessionId,
              text,
              (delta) => {
                send(ws, { type: 'delta', conversationId, text: delta });
              },
              (toolEvent) => {
                send(ws, {
                  type: 'tool',
                  conversationId,
                  status: toolEvent.status,
                  name: toolEvent.name,
                  summary: toolEvent.summary,
                  detail: toolEvent.detail,
                  success: toolEvent.success,
                });
              },
              attachments,
            );
            send(ws, { type: 'final', conversationId, text: result.finalText });
          } catch (err: any) {
            send(ws, { type: 'error', conversationId, message: err?.message ?? String(err) });
          }
        });
      conversationLocks.set(conversationId, turn);
      await turn;
    });
  });

  wss.on('listening', () => {
    // eslint-disable-next-line no-console
    console.log(`Copilot chat server listening on ws://0.0.0.0:${config.port}`);
  });

  return wss;
}
