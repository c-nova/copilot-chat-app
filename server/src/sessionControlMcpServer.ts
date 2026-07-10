#!/usr/bin/env node
/**
 * The "session-control" MCP server (Phase 3 - controller/meta sessions). Runs as its own stdio
 * subprocess, spawned by the `copilot` CLI itself once registered via `copilot mcp add` (see
 * index.ts's auto-registration at startup) - it does NOT share memory with the main wsServer.ts
 * process. Every tool call here is a thin adapter over the internal control API
 * (internalControlApi.ts), which is what actually holds the conversationLocks/conversationSessions
 * state and enforces serialization against a human using the same session via the app.
 *
 * Reads its target (port + auth token) from INTERNAL_CONTROL_PORT / INTERNAL_CONTROL_TOKEN env
 * vars, set by index.ts when it registers this server.
 *
 * Note on scope: any session that has this MCP server registered can call these tools - MCP server
 * registration via `copilot mcp add` is global to the CLI, not scoped to a specific session. So
 * "controller session" is a soft/UX convention (a session the user talks to expecting it to
 * coordinate others), not a hard technical boundary enforced per-session. The tool descriptions
 * below tell the model to only use these when the user explicitly asks for cross-session
 * coordination, but that's guidance, not an enforced restriction.
 */
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import * as http from 'http';
import { z } from 'zod';
import { getCallerSessionId } from './callerSessionId';

const CONTROL_PORT = Number(process.env.INTERNAL_CONTROL_PORT ?? '5220');
const CONTROL_TOKEN = process.env.INTERNAL_CONTROL_TOKEN ?? '';

interface InternalApiResponse {
  ok: boolean;
  error?: string;
  [key: string]: unknown;
}

function callInternalApi(method: 'GET' | 'POST', path: string, body?: unknown): Promise<InternalApiResponse> {
  return new Promise((resolve, reject) => {
    const payload = body !== undefined ? JSON.stringify(body) : undefined;
    const req = http.request(
      {
        hostname: '127.0.0.1',
        port: CONTROL_PORT,
        path,
        method,
        headers: {
          Authorization: `Bearer ${CONTROL_TOKEN}`,
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
          let parsed: InternalApiResponse;
          try {
            parsed = raw ? JSON.parse(raw) : { ok: false, error: 'Empty response' };
          } catch {
            reject(new Error('Invalid response from internal control API'));
            return;
          }
          if ((res.statusCode ?? 500) >= 400 || !parsed.ok) {
            reject(new Error(parsed.error ?? `Request failed with status ${res.statusCode}`));
            return;
          }
          resolve(parsed);
        });
      },
    );
    req.on('error', reject);
    if (payload) req.write(payload);
    req.end();
  });
}

const server = new McpServer({ name: 'session-control', version: '1.0.0' });

const CROSS_SESSION_GUIDANCE =
  'Only use this when the user has explicitly asked you to check on, coordinate with, or dispatch work to another session - never as a routine part of normal conversation.';

server.registerTool(
  'list_sessions',
  {
    title: 'List sessions on this server',
    description: `Lists other Copilot chat sessions running on this same server/machine (id, working directory, label, last activity, turn count). ${CROSS_SESSION_GUIDANCE}`,
    inputSchema: {},
  },
  async () => {
    const result = await callInternalApi('GET', '/internal/sessions');
    return { content: [{ type: 'text' as const, text: JSON.stringify(result.sessions ?? [], null, 2) }] };
  },
);

server.registerTool(
  'get_session_summary',
  {
    title: 'Get a session\'s recent history',
    description: `Gets the most recent turns (user message + assistant reply) of another session on this server, by session id (see list_sessions). ${CROSS_SESSION_GUIDANCE}`,
    inputSchema: {
      sessionId: z.string().describe('The target session id, from list_sessions'),
    },
  },
  async ({ sessionId }: { sessionId: string }) => {
    const result = await callInternalApi('GET', `/internal/sessions/${encodeURIComponent(sessionId)}`);
    const allTurns = Array.isArray(result.turns) ? result.turns : [];
    const recentTurns = allTurns.slice(-10);
    return { content: [{ type: 'text' as const, text: JSON.stringify(recentTurns, null, 2) }] };
  },
);

server.registerTool(
  'run_turn_on_session',
  {
    title: 'Send a message to another session',
    description: `Sends a message to another existing session on this SAME machine and waits for its full reply, as if you had typed it there yourself. The target session must already exist (see list_sessions/list_my_children) - this can't create a brand-new one, and it cannot reach a session on a different machine (see spawn_session's targetServer for that case - pass existingSessionId there instead). Fails immediately (rather than silently waiting) if that session currently has a turn actively running, e.g. a human is chatting with it right now via the app - if that happens, tell the user it's busy and to try again shortly, rather than retrying in a loop. ${CROSS_SESSION_GUIDANCE}`,
    inputSchema: {
      sessionId: z.string().describe('The target session id, from list_sessions'),
      message: z.string().describe('The message to send to that session'),
    },
  },
  async ({ sessionId, message }: { sessionId: string; message: string }) => {
    const result = await callInternalApi('POST', '/internal/run-turn', { sessionId, message });
    return { content: [{ type: 'text' as const, text: typeof result.finalText === 'string' ? result.finalText : '' }] };
  },
);

/**
 * PBI-026: lists the peer servers configured via PEER_SERVERS (see config.ts/peerClient.ts) that
 * spawn_session can dispatch to via its optional targetServer parameter - names only, no
 * URLs/tokens. An empty list just means no peers are configured (spawn_session stays local-only).
 */
server.registerTool(
  'list_servers',
  {
    title: 'List other servers this one can dispatch to',
    description: `Lists the names of other Copilot chat servers (e.g. a different machine/OS) that spawn_session can create a child session on via its targetServer parameter, in addition to this same machine. ${CROSS_SESSION_GUIDANCE}`,
    inputSchema: {},
  },
  async () => {
    const result = await callInternalApi('GET', '/internal/peers');
    const peers = Array.isArray(result.peers) ? result.peers : [];
    const names = peers.map((p: any) => p.name).filter((n: unknown): n is string => typeof n === 'string');
    const text = names.length > 0
      ? `Available target servers (pass one as spawn_session's targetServer to run there instead of this machine):\n${names.map((n) => `- ${n}`).join('\n')}\n\nOmit targetServer to spawn on this same machine.`
      : 'No other servers are configured - spawn_session will always run on this same machine.';
    return { content: [{ type: 'text' as const, text }] };
  },
);

/**
 * PBI-028: lets the model discover sessions it has *already* spawned as children of itself
 * (locally or on any configured peer server - see internalControlApi.ts's /internal/children),
 * so it can reuse one via spawn_session's existingSessionId instead of accidentally creating a
 * brand-new child every time the user asks for another follow-up on "the same" worker. Before this
 * tool existed, the model had no way to know a suitable child already existed and would spawn a
 * fresh one for every message - this is the fix for that.
 */
server.registerTool(
  'list_my_children',
  {
    title: 'List sessions already spawned under this one',
    description: `Lists the sessions you (this session) have already spawned as children via spawn_session, whether they're on this machine or a different one (see list_servers). Call this BEFORE spawn_session if the user's request might continue work you already delegated - if a suitable child already exists here, pass its id as spawn_session's existingSessionId (plus its targetServer, if it's not on this machine) to continue that same conversation instead of creating a redundant new child. ${CROSS_SESSION_GUIDANCE}`,
    inputSchema: {},
  },
  async () => {
    const parentSessionId = getCallerSessionId();
    if (!parentSessionId) {
      throw new Error(
        'Could not determine this session\'s own id (the parent process lookup failed) - list_my_children is unavailable right now.',
      );
    }
    const result = await callInternalApi('GET', `/internal/children?parentSessionId=${encodeURIComponent(parentSessionId)}`);
    const children = Array.isArray(result.sessions) ? result.sessions : [];
    const text = children.length > 0
      ? JSON.stringify(children, null, 2)
      : 'No child sessions found - nothing has been spawned under this session yet.';
    return { content: [{ type: 'text' as const, text }] };
  },
);

/**
 * PBI-025: spawns a child session under *this* session in the Orchestrator screen, as either a
 * brand-new session or an already-existing one attached for visibility. Deliberately has no
 * "parentSessionId"/"which session am I" parameter for the model to fill in - getCallerSessionId()
 * determines that deterministically by inspecting the parent `copilot` OS process's own
 * `--session-id=<uuid>` argv (see callerSessionId.ts's doc comment for why this is far more
 * reliable than asking the model to remember/report its own session id).
 */
server.registerTool(
  'spawn_session',
  {
    title: 'Spawn a child session',
    description: `Creates a new child session (or continues/attaches an already-existing one, via existingSessionId) under this session for the user's Orchestrator screen, optionally dispatching it an instruction. IMPORTANT: before creating a brand-new child, call list_my_children - if the user's request is a follow-up on something you already delegated, pass that existing child's id as existingSessionId (with message) to continue that same conversation instead of spawning a redundant new one. Use this when the user asks you to delegate/parallelize a sub-task to a worker session, or to coordinate with another session as a child of this one. Can target a different machine via targetServer (see list_servers) for cross-platform work (e.g. running a Windows-only command) - omit it to stay on this same machine. ${CROSS_SESSION_GUIDANCE}`,
    inputSchema: {
      existingSessionId: z
        .string()
        .optional()
        .describe('Continue this already-existing child session (from list_my_children) instead of creating a brand-new one - e.g. to send it a follow-up instruction.'),
      cwd: z.string().optional().describe('Working directory for a brand-new child session. Ignored when existingSessionId is set.'),
      message: z
        .string()
        .optional()
        .describe('Instruction to send the child. Required when existingSessionId is omitted - a brand-new session needs at least one message to exist.'),
      targetServer: z
        .string()
        .optional()
        .describe('Name of a peer server (from list_servers) to spawn/continue the child on instead of this same machine - e.g. for cross-platform work. Omit to use this same machine.'),
    },
  },
  async ({ existingSessionId, cwd, message, targetServer }: { existingSessionId?: string; cwd?: string; message?: string; targetServer?: string }) => {
    const parentSessionId = getCallerSessionId();
    if (!parentSessionId) {
      throw new Error(
        'Could not determine this session\'s own id (the parent process lookup failed) - spawn_session is unavailable right now.',
      );
    }
    const result = await callInternalApi('POST', '/internal/spawn-session', { parentSessionId, existingSessionId, cwd, message, targetPeer: targetServer });
    const summary = `${existingSessionId ? 'Continued' : 'Spawned'} child session ${result.sessionId}${targetServer ? ` on server "${targetServer}"` : ''}.` + (typeof result.finalText === 'string' && result.finalText ? `\nReply: ${result.finalText}` : '');
    return { content: [{ type: 'text' as const, text: summary }] };
  },
);

const transport = new StdioServerTransport();
server.connect(transport).catch((err) => {
  console.error('[session-control] failed to start:', err);
  process.exit(1);
});
