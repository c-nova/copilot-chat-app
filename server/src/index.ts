import * as fs from 'fs';
import * as path from 'path';
import { config } from './config';
import { createInternalControlApi } from './internalControlApi';
import { addMcpServer, listMcpServers } from './mcpManager';
import { createChatServer } from './wsServer';

const SESSION_CONTROL_MCP_NAME = 'session-control';

/**
 * Registers the built-in session-control MCP server (Phase 3 "controller session" tools) if it
 * isn't already registered. Idempotent - safe to call on every startup. Reuses the same AUTH_TOKEN
 * the WebSocket server already requires as the internal control API's bearer token too, rather than
 * managing a second secret: the MCP server subprocess runs on this same trusted machine (spawned by
 * the user's own already-logged-in `copilot` CLI), the same trust boundary that already holds this
 * token in plaintext in server/.env.
 */
async function ensureSessionControlMcpRegistered(): Promise<void> {
  try {
    const existing = await listMcpServers();
    if (existing.some((s) => s.name === SESSION_CONTROL_MCP_NAME)) return;

    const scriptPath = path.join(__dirname, 'sessionControlMcpServer.js');
    await addMcpServer({
      name: SESSION_CONTROL_MCP_NAME,
      transport: 'stdio',
      command: process.execPath,
      args: [scriptPath],
      env: {
        INTERNAL_CONTROL_PORT: String(config.internalControlPort),
        INTERNAL_CONTROL_TOKEN: config.authToken,
      },
    });
    console.log(`Registered built-in '${SESSION_CONTROL_MCP_NAME}' MCP server.`);
  } catch (err: any) {
    // Best-effort: a controller session simply won't have these tools available if this fails
    // (e.g. `copilot` not on PATH yet, or `copilot mcp add` schema changes) - shouldn't block the
    // rest of the server from starting.
    console.warn(`[index] Failed to auto-register '${SESSION_CONTROL_MCP_NAME}' MCP server:`, err?.message ?? err);
  }
}

fs.mkdirSync(config.workDir, { recursive: true });
console.log(`Copilot agent working directory: ${config.workDir}`);

createChatServer();
createInternalControlApi();
void ensureSessionControlMcpRegistered();
