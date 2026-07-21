import * as crypto from 'crypto';
import * as path from 'path';
import { IncomingMessage } from 'http';
import { WebSocket, WebSocketServer } from 'ws';
import { config } from './config';
import { AttachmentInput, DeltaHandler, runCopilotTurn, ToolEventHandler, TurnResult } from './copilotRunner';
import { gitClone, listDir } from './fsBrowser';
import { addMcpServer, listMcpServers, removeMcpServer } from './mcpManager';
import { listAvailableModels } from './modelCatalog';
import { isPathAllowed } from './pathAccess';
import { notifyReplyReady } from './notify';
import { ClientMessage, ServerMessage } from './protocol';
import { deleteSessionHard, getSessionCwd, getSessionHistory, getSessionSummary, listSessions, searchSessions } from './sessionHistory';
import { deleteSessionMeta, getAllSessionMeta, getChildSessionIds, getSessionMeta, markSessionControlTurn, setSessionArchived, setSessionLabel, setSessionOrchestratorMain, setSessionParent } from './sessionMeta';
import { getServerInfo } from './serverInfo';

/**
 * PBI-027 (fixed after live testing turned up cross-server gaps): exposes each session's raw
 * parentSessionId (and the trivially-derived isOrchestratorChild) from a single already-loaded
 * sessionMeta snapshot - reads the sidecar store once per request (via getAllSessionMeta) rather
 * than once per session. Deliberately does NOT also compute "is this session a parent" here: that
 * would only ever see children recorded in *this same server's* sessionMeta.json, silently missing
 * any child spawned on a different server (PBI-026) - the client already aggregates every
 * configured profile's sessions (see HomePage), so it can correctly compute the "has children
 * anywhere" badge itself from every session's parentSessionId, once, after that aggregation.
 */
function buildOrchestratorFields(allMeta: ReturnType<typeof getAllSessionMeta>) {
  return (sessionId: string) => {
    const parentSessionId = allMeta[sessionId]?.parentSessionId;
    return { parentSessionId, isOrchestratorChild: Boolean(parentSessionId) };
  };
}

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
// conversationIds with a turn *actually running right now* (as opposed to merely queued behind
// one via conversationLocks). Used only to power the rejectIfBusy option below - the WebSocket
// chat handler never sets that option, so a human's own back-to-back messages still queue and
// wait exactly as before; only the internal control API opts into failing fast instead.
const activeConversationTurns = new Set<string>();

/**
 * Thrown by runConversationTurn when `rejectIfBusy` is set and the target conversation already has
 * a turn actively running. Distinguished by `name` (rather than relying on `instanceof`, which can
 * be unreliable across jest.mock module boundaries) so callers like internalControlApi.ts can map
 * it to a specific HTTP status instead of a generic 500.
 */
export class ConversationBusyError extends Error {
  constructor(conversationId: string) {
    super(`Session ${conversationId} is currently busy with an in-progress turn - try again in a moment.`);
    this.name = 'ConversationBusyError';
  }
}

export interface RunConversationTurnOptions {
  attachments?: AttachmentInput[];
  /** SDK model id for this turn. Omit to use the server's COPILOT_MODEL/CLI default. */
  model?: string;
  /** Working directory to create a brand-new session in; ignored when resuming an existing one. */
  requestedCwd?: string;
  /**
   * When true, throws instead of silently creating a brand-new session if `conversationId` doesn't
   * already correspond to one. Used by the internal control API so a controller session can only
   * ever dispatch work to a session that already exists, never spin up an arbitrary new one by id.
   */
  requireExistingSession?: boolean;
  /**
   * When true, throws ConversationBusyError immediately instead of queueing behind an
   * already-in-progress turn for this conversationId. Used only by the internal control API
   * (session-control MCP's run_turn_on_session): without this, a cross-session dispatch would
   * silently wait for a human's in-flight turn to finish and then run right after it with no
   * warning to either side - safe from corruption (conversationLocks still serializes), but a
   * surprising, silent interjection into a conversation a human is actively having. The WebSocket
   * chat handler never sets this, so a human's own client queueing multiple sends still works
   * exactly as before.
   */
  rejectIfBusy?: boolean;
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
  if (options.rejectIfBusy && activeConversationTurns.has(conversationId)) {
    return Promise.reject(new ConversationBusyError(conversationId));
  }
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

  activeConversationTurns.add(conversationId);
  try {
    return await runCopilotTurn(
      state.sessionId,
      text,
      options.onDelta ?? (() => {}),
      options.onToolEvent ?? (() => {}),
      options.attachments,
      state.cwd,
      options.model,
    );
  } finally {
    activeConversationTurns.delete(conversationId);
  }
}

export interface SpawnChildSessionOptions {
  parentSessionId: string;
  /** Attach an already-existing session as the child instead of creating a brand-new one. */
  existingSessionId?: string;
  /** Working directory for a brand-new child session; ignored when existingSessionId is set. */
  cwd?: string;
  /** First instruction to send to the child. Required when existingSessionId is omitted (a
   * brand-new session has no content until it gets one) - optional when attaching an existing
   * session (you can attach one purely for visibility, with no immediate instruction). */
  message?: string;
}

export interface SpawnChildSessionResult {
  sessionId: string;
  finalText?: string;
}

/**
 * Core logic behind PBI-025's Spawn feature - shared by the WebSocket handler (sessions:spawn,
 * used by the Orchestrator screen's manual "+ add child" UI) and the internal control API's
 * /internal/spawn-session (used by session-control's spawn_session MCP tool, for the AI-decided
 * path), so both entry points behave identically rather than maintaining two copies of this logic.
 */
export async function spawnChildSession(options: SpawnChildSessionOptions): Promise<SpawnChildSessionResult> {
  const childSessionId = options.existingSessionId ?? crypto.randomUUID();

  if (options.existingSessionId && !options.message) {
    // Just attaching an already-existing session for visibility - no turn to run, so nothing for
    // run_turn_on_session-style rejectIfBusy to guard against here.
    setSessionParent(childSessionId, options.parentSessionId);
    return { sessionId: childSessionId };
  }

  if (!options.message) {
    throw new Error('message is required when spawning a brand-new child session.');
  }

  // Same fail-fast-instead-of-queueing behavior as run_turn_on_session - see this file's other
  // rejectIfBusy usages.
  const result = await runConversationTurn(childSessionId, options.message, {
    requireExistingSession: Boolean(options.existingSessionId),
    rejectIfBusy: true,
    requestedCwd: options.cwd,
  });
  setSessionParent(childSessionId, options.parentSessionId);
  const turnsAfter = getSessionHistory(childSessionId);
  const lastTurn = turnsAfter[turnsAfter.length - 1];
  if (lastTurn) {
    markSessionControlTurn(childSessionId, lastTurn.turnIndex);
  }
  return { sessionId: childSessionId, finalText: result.finalText };
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

      if (msg.type === 'models:list') {
        try {
          const models = await listAvailableModels();
          send(ws, { type: 'models:list-result', requestId: msg.requestId, ok: true, models });
        } catch (err: any) {
          send(ws, { type: 'models:list-result', requestId: msg.requestId, ok: false, error: err?.message ?? String(err) });
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
          const allMeta = getAllSessionMeta();
          const orchestratorFields = buildOrchestratorFields(allMeta);
          const sessions = listSessions([...config.browseRoots, config.workDir]).map((s) => {
            const meta = allMeta[s.id];
            return {
              ...s,
              label: meta?.label,
              archived: meta?.archived ?? false,
              busy: activeConversationTurns.has(s.id),
              orchestratorMain: meta?.orchestratorMain ?? false,
              ...orchestratorFields(s.id),
            };
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
          const meta = getSessionMeta(msg.sessionId);
          const sessionControlTurnIndexes = new Set(meta?.sessionControlTurnIndexes ?? []);
          const dtoTurns = turns.map((t) => ({
            ...t,
            ...(sessionControlTurnIndexes.has(t.turnIndex) ? { fromOtherSession: true } : {}),
          }));
          send(ws, { type: 'sessions:history-result', requestId: msg.requestId, ok: true, sessionId: msg.sessionId, turns: dtoTurns });
        } catch (err: any) {
          send(ws, { type: 'sessions:history-result', requestId: msg.requestId, ok: false, error: err?.message ?? String(err) });
        }
        return;
      }

      if (msg.type === 'sessions:search') {
        try {
          const allMeta = getAllSessionMeta();
          const orchestratorFields = buildOrchestratorFields(allMeta);
          const sessions = searchSessions(msg.query, [...config.browseRoots, config.workDir]).map((s) => {
            const meta = allMeta[s.id];
            return {
              ...s,
              label: meta?.label,
              archived: meta?.archived ?? false,
              busy: activeConversationTurns.has(s.id),
              orchestratorMain: meta?.orchestratorMain ?? false,
              ...orchestratorFields(s.id),
            };
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
          if (msg.orchestratorMain) {
            setSessionOrchestratorMain(msg.sessionId);
            meta = getSessionMeta(msg.sessionId);
          }
          send(ws, {
            type: 'sessions:update-meta-result',
            requestId: msg.requestId,
            ok: true,
            label: meta?.label,
            archived: meta?.archived,
            orchestratorMain: meta?.orchestratorMain,
          });
        } catch (err: any) {
          send(ws, { type: 'sessions:update-meta-result', requestId: msg.requestId, ok: false, error: err?.message ?? String(err) });
        }
        return;
      }

      if (msg.type === 'sessions:delete') {
        try {
          if (msg.mode === 'hard') {
            // Refuse while the Copilot CLI process for this session is actively mid-turn (see
            // activeConversationTurns above) - deleting its rows out from under it while it's
            // reading/writing the same session-store.db is exactly the accident PBI-021 calls out.
            if (activeConversationTurns.has(msg.sessionId)) {
              throw new Error('Cannot hard-delete a session with an in-progress turn - wait for it to finish and try again.');
            }
            deleteSessionHard(msg.sessionId);
            deleteSessionMeta(msg.sessionId);
          } else {
            setSessionArchived(msg.sessionId, true);
          }
          send(ws, { type: 'sessions:delete-result', requestId: msg.requestId, ok: true });
        } catch (err: any) {
          send(ws, { type: 'sessions:delete-result', requestId: msg.requestId, ok: false, error: err?.message ?? String(err) });
        }
        return;
      }

      if (msg.type === 'sessions:spawn') {
        try {
          const result = await spawnChildSession({
            parentSessionId: msg.parentSessionId,
            existingSessionId: msg.existingSessionId,
            cwd: msg.cwd,
            message: msg.message,
          });
          send(ws, {
            type: 'sessions:spawn-result',
            requestId: msg.requestId,
            ok: true,
            sessionId: result.sessionId,
            finalText: result.finalText,
          });
        } catch (err: any) {
          send(ws, { type: 'sessions:spawn-result', requestId: msg.requestId, ok: false, error: err?.message ?? String(err) });
        }
        return;
      }

      if (msg.type === 'sessions:children') {
        try {
          const allMeta = getAllSessionMeta();
          const orchestratorFields = buildOrchestratorFields(allMeta);
          const sessions = getChildSessionIds(msg.parentSessionId)
            .map((id) => {
              const summary = getSessionSummary(id);
              if (!summary) return null;
              const meta = allMeta[id];
              return {
                ...summary,
                label: meta?.label,
                archived: meta?.archived ?? false,
                busy: activeConversationTurns.has(id),
                orchestratorMain: meta?.orchestratorMain ?? false,
                ...orchestratorFields(id),
              };
            })
            .filter((s): s is NonNullable<typeof s> => s !== null);
          send(ws, { type: 'sessions:children-result', requestId: msg.requestId, ok: true, sessions });
        } catch (err: any) {
          send(ws, { type: 'sessions:children-result', requestId: msg.requestId, ok: false, error: err?.message ?? String(err) });
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
          model: msg.model,
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
