import * as crypto from 'crypto';
import * as path from 'path';
import { IncomingMessage } from 'http';
import { WebSocket, WebSocketServer } from 'ws';
import { config } from './config';
import { AttachmentInput, DeltaHandler, runCopilotTurn, ToolEventHandler, TurnResult } from './copilotRunner';
import { gitClone, listDir } from './fsBrowser';
import { addMcpServer, listMcpServers, removeMcpServer } from './mcpManager';
import { isPathAllowed } from './pathAccess';
import { notifyReplyReady } from './notify';
import { ClientMessage, ServerMessage } from './protocol';
import { getSessionCwd, getSessionHistory, listSessions, searchSessions } from './sessionHistory';
import { getSessionMeta, setSessionArchived, setSessionLabel } from './sessionMeta';
import { getServerInfo } from './serverInfo';

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

// conversationId (== Copilot CLI session id) -> cwd it's bound to. Module-scoped (rather than
// local to createChatServer()) so both the WebSocket handler below and the internal control API
// (internalControlApi.ts, used by the session-control MCP server - see design notes) share the
// exact same state and, more importantly, the exact same serialization point in conversationLocks.
// That's what guarantees a controller session can never run a second, overlapping `copilot`
// process against a session a human is already actively chatting with via the app.
const conversationSessions = new Map<string, { sessionId: string; cwd: string }>();
const conversationLocks = new Map<string, Promise<void>>();

export interface RunConversationTurnOptions {
  attachments?: AttachmentInput[];
  /** Working directory to create a brand-new session in; ignored when resuming an existing one. */
  requestedCwd?: string;
  /**
   * When true, throws instead of silently creating a brand-new session if `conversationId` doesn't
   * already correspond to one. Used by the internal control API so a controller session can only
   * ever dispatch work to a session that already exists, never spin up an arbitrary new one by id.
   */
  requireExistingSession?: boolean;
  onDelta?: DeltaHandler;
  onToolEvent?: ToolEventHandler;
}

/**
 * Runs one chat turn for `conversationId`, serialized against any other in-flight turn for the
 * same id via `conversationLocks` - shared by the WebSocket chat handler and the internal control
 * API, so a human using the app and a controller session's `run_turn_on_session` tool can never
 * both drive the same underlying Copilot CLI session concurrently.
 */
export function runConversationTurn(
  conversationId: string,
  text: string,
  options: RunConversationTurnOptions = {},
): Promise<TurnResult> {
  const prior = conversationLocks.get(conversationId) ?? Promise.resolve();
  const turn = prior.catch(() => undefined).then(() => runConversationTurnCore(conversationId, text, options));
  // The lock map only needs to know "when is the prior turn settled", never why - a rejected turn
  // shouldn't jam the queue for whatever tries to use this conversationId next.
  conversationLocks.set(conversationId, turn.then(() => undefined, () => undefined));
  return turn;
}

async function runConversationTurnCore(
  conversationId: string,
  text: string,
  options: RunConversationTurnOptions,
): Promise<TurnResult> {
  // The client-provided conversationId doubles directly as the Copilot CLI --session-id: the CLI
  // creates a new session if it doesn't exist yet, or resumes it if it does. This lets a caller
  // "resume" any past session just by reusing its id as the conversationId.
  let state = conversationSessions.get(conversationId);
  if (!state) {
    const sessionId = conversationId;
    const allowedRoots = [...config.browseRoots, config.workDir];
    // Prefer the cwd the CLI itself already recorded for this session id (e.g. resuming one picked
    // from the Sessions list, possibly after a server restart) over anything the caller sends -
    // this guarantees a resume can never silently run in a different folder than the session's own
    // history.
    const existingCwd = getSessionCwd(sessionId);

    if (existingCwd) {
      // Without this check, any caller who already knew a session id outside our configured scope
      // (e.g. one belonging to a completely unrelated Copilot-based tool on this same machine, like
      // an internal agent) could still resume/drive it here even though it would never show up via
      // sessions:list or the session-control MCP's list_sessions - the CLI's own db has no concept
      // of "belongs to this app". This closes that gap uniformly for every caller (WebSocket chat
      // AND the internal control API used by the session-control MCP), since both go through here.
      if (!isPathAllowed(existingCwd, allowedRoots)) {
        throw new Error(`Session ${sessionId} is outside this server's configured BROWSE_ROOTS and can't be accessed here.`);
      }
    } else if (options.requireExistingSession) {
      throw new Error(`No existing session found for id: ${sessionId}`);
    }

    const requestedCwd = options.requestedCwd ? path.resolve(options.requestedCwd) : undefined;
    let cwd: string;
    if (existingCwd) {
      cwd = existingCwd;
    } else if (requestedCwd && isPathAllowed(requestedCwd, allowedRoots)) {
      cwd = requestedCwd;
    } else {
      if (requestedCwd) {
        console.warn(`[wsServer] Rejected cwd outside allowed roots for new session ${sessionId}: ${requestedCwd}`);
      }
      cwd = config.workDir;
    }
    state = { sessionId, cwd };
    conversationSessions.set(conversationId, state);
  }

  return runCopilotTurn(
    state.sessionId,
    text,
    options.onDelta ?? (() => {}),
    options.onToolEvent ?? (() => {}),
    options.attachments,
    state.cwd,
  );
}

export function createChatServer(): WebSocketServer {
  const wss = new WebSocketServer({ port: config.port, verifyClient: (info, done) => {
    if (isAuthorized(info.req)) {
      done(true);
    } else {
      done(false, 401, 'Unauthorized');
    }
  }});

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
          const sessions = listSessions([...config.browseRoots, config.workDir]).map((s) => {
            const meta = getSessionMeta(s.id);
            return { ...s, label: meta?.label, archived: meta?.archived ?? false };
          });
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

      if (msg.type === 'sessions:search') {
        try {
          const sessions = searchSessions(msg.query, [...config.browseRoots, config.workDir]).map((s) => {
            const meta = getSessionMeta(s.id);
            return { ...s, label: meta?.label, archived: meta?.archived ?? false };
          });
          send(ws, { type: 'sessions:search-result', requestId: msg.requestId, ok: true, sessions });
        } catch (err: any) {
          send(ws, { type: 'sessions:search-result', requestId: msg.requestId, ok: false, error: err?.message ?? String(err) });
        }
        return;
      }

      if (msg.type === 'fs:list-dir') {
        try {
          const result = listDir(msg.path, config.browseRoots);
          send(ws, {
            type: 'fs:list-dir-result',
            requestId: msg.requestId,
            ok: true,
            path: result.path,
            parentPath: result.parentPath,
            entries: result.entries,
            roots: result.roots,
          });
        } catch (err: any) {
          send(ws, { type: 'fs:list-dir-result', requestId: msg.requestId, ok: false, error: err?.message ?? String(err) });
        }
        return;
      }

      if (msg.type === 'fs:git-clone') {
        try {
          const clonedPath = await gitClone(msg.parentPath, msg.repoUrl, msg.destName, config.browseRoots);
          send(ws, { type: 'fs:git-clone-result', requestId: msg.requestId, ok: true, path: clonedPath });
        } catch (err: any) {
          send(ws, { type: 'fs:git-clone-result', requestId: msg.requestId, ok: false, error: err?.message ?? String(err) });
        }
        return;
      }

      if (msg.type === 'server:info') {
        try {
          const info = await getServerInfo();
          send(ws, { type: 'server:info-result', requestId: msg.requestId, ok: true, info });
        } catch (err: any) {
          send(ws, { type: 'server:info-result', requestId: msg.requestId, ok: false, error: err?.message ?? String(err) });
        }
        return;
      }

      if (msg.type === 'sessions:update-meta') {
        try {
          let meta;
          if (msg.label !== undefined) meta = setSessionLabel(msg.sessionId, msg.label);
          if (msg.archived !== undefined) meta = setSessionArchived(msg.sessionId, msg.archived);
          send(ws, {
            type: 'sessions:update-meta-result',
            requestId: msg.requestId,
            ok: true,
            label: meta?.label,
            archived: meta?.archived,
          });
        } catch (err: any) {
          send(ws, { type: 'sessions:update-meta-result', requestId: msg.requestId, ok: false, error: err?.message ?? String(err) });
        }
        return;
      }

      if (msg.type !== 'chat' || !msg.conversationId || typeof msg.text !== 'string') {
        send(ws, { type: 'error', message: 'Expected { type: "chat", conversationId, text }' });
        return;
      }

      const { conversationId, text, attachments } = msg;
      conversationIdsSeenOnThisSocket.add(conversationId);
      try {
        const result = await runConversationTurn(conversationId, text, {
          attachments,
          requestedCwd: msg.cwd,
          onDelta: (delta) => send(ws, { type: 'delta', conversationId, text: delta }),
          onToolEvent: (toolEvent) =>
            send(ws, {
              type: 'tool',
              conversationId,
              status: toolEvent.status,
              name: toolEvent.name,
              summary: toolEvent.summary,
              detail: toolEvent.detail,
              success: toolEvent.success,
            }),
        });
        send(ws, { type: 'final', conversationId, text: result.finalText });
        // Best-effort/fire-and-forget: never let a notification failure delay or affect the
        // response the client already received above.
        void notifyReplyReady(result.finalText);
      } catch (err: any) {
        send(ws, { type: 'error', conversationId, message: err?.message ?? String(err) });
      }
    });
  });

  wss.on('listening', () => {
    // eslint-disable-next-line no-console
    console.log(`Copilot chat server listening on ws://0.0.0.0:${config.port}`);
  });

  return wss;
}
