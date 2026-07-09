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
    description: `Sends a message to another existing session on this server and waits for its full reply, as if you had typed it there yourself. The target session must already exist (see list_sessions) - this can't create a brand-new one. Fails immediately (rather than silently waiting) if that session currently has a turn actively running, e.g. a human is chatting with it right now via the app - if that happens, tell the user it's busy and to try again shortly, rather than retrying in a loop. ${CROSS_SESSION_GUIDANCE}`,
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
    description: `Creates a new child session (or attaches an already-existing one) under this session for the user's Orchestrator screen, optionally dispatching it a first instruction. Use this when the user asks you to delegate/parallelize a sub-task to a worker session, or to coordinate with another session as a child of this one. ${CROSS_SESSION_GUIDANCE}`,
    inputSchema: {
      existingSessionId: z
        .string()
        .optional()
        .describe('Attach this already-existing session id (from list_sessions) as the child, instead of creating a brand-new one.'),
      cwd: z.string().optional().describe('Working directory for a brand-new child session. Ignored when existingSessionId is set.'),
      message: z
        .string()
        .optional()
        .describe('First instruction to send the child. Required when existingSessionId is omitted - a brand-new session needs at least one message to exist.'),
    },
  },
  async ({ existingSessionId, cwd, message }: { existingSessionId?: string; cwd?: string; message?: string }) => {
    const parentSessionId = getCallerSessionId();
    if (!parentSessionId) {
      throw new Error(
        'Could not determine this session\'s own id (the parent process lookup failed) - spawn_session is unavailable right now.',
      );
    }
    const result = await callInternalApi('POST', '/internal/spawn-session', { parentSessionId, existingSessionId, cwd, message });
    const summary = `Spawned child session ${result.sessionId}.` + (typeof result.finalText === 'string' && result.finalText ? `\nReply: ${result.finalText}` : '');
    return { content: [{ type: 'text' as const, text: summary }] };
  },
);

const transport = new StdioServerTransport();
server.connect(transport).catch((err) => {
  console.error('[session-control] failed to start:', err);
  process.exit(1);
});
