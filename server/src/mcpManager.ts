import spawn from 'cross-spawn';
import { config } from './config';

export interface McpServerInfo {
  name: string;
  type: 'local' | 'http' | 'sse' | string;
  command?: string;
  args?: string[];
  url?: string;
  tools?: string[];
  source?: string;
}

export interface AddMcpServerOptions {
  name: string;
  transport: 'stdio' | 'http' | 'sse';
  /** For stdio servers: command + args, e.g. command="npx", args=["-y", "@upstash/context7-mcp"] */
  command?: string;
  args?: string[];
  /** For http/sse servers */
  url?: string;
  env?: Record<string, string>;
  headers?: Record<string, string>;
}

function runCopilot(args: string[]): Promise<{ stdout: string; stderr: string; code: number | null }> {
  return new Promise((resolve, reject) => {
    const child = spawn(config.copilotCommand, args, { windowsHide: true });
    let stdout = '';
    let stderr = '';
    child.stdout?.on('data', (chunk: Buffer) => (stdout += chunk.toString('utf8')));
    child.stderr?.on('data', (chunk: Buffer) => (stderr += chunk.toString('utf8')));
    child.on('error', reject);
    child.on('close', (code) => resolve({ stdout, stderr, code }));
  });
}

export async function listMcpServers(): Promise<McpServerInfo[]> {
  const { stdout, stderr, code } = await runCopilot(['mcp', 'list', '--json']);
  if (code !== 0) {
    throw new Error(stderr.trim() || `copilot mcp list exited with code ${code}`);
  }
  const parsed = JSON.parse(stdout);
  const servers = parsed.mcpServers ?? {};
  return Object.entries(servers).map(([name, def]: [string, any]) => ({
    name,
    type: def.type,
    command: def.command,
    args: def.args,
    url: def.url,
    tools: def.tools,
    source: def.source,
  }));
}

export async function addMcpServer(opts: AddMcpServerOptions): Promise<McpServerInfo> {
  const args = ['mcp', 'add', '--json'];
  if (opts.transport !== 'stdio') {
    args.push('--transport', opts.transport);
  }
  for (const [key, value] of Object.entries(opts.env ?? {})) {
    args.push('--env', `${key}=${value}`);
  }
  for (const [key, value] of Object.entries(opts.headers ?? {})) {
    args.push('--header', `${key}: ${value}`);
  }
  args.push(opts.name);

  if (opts.transport === 'stdio') {
    if (!opts.command) throw new Error('command is required for stdio MCP servers');
    args.push('--', opts.command, ...(opts.args ?? []));
  } else {
    if (!opts.url) throw new Error('url is required for http/sse MCP servers');
    args.push(opts.url);
  }

  const { stdout, stderr, code } = await runCopilot(args);
  if (code !== 0) {
    throw new Error(stderr.trim() || `copilot mcp add exited with code ${code}`);
  }
  const parsed = JSON.parse(stdout);
  const [name, def] = Object.entries(parsed)[0] as [string, any];
  return {
    name,
    type: def.type,
    command: def.command,
    args: def.args,
    url: def.url,
    tools: def.tools,
  };
}

export async function removeMcpServer(name: string): Promise<void> {
  const { stderr, code } = await runCopilot(['mcp', 'remove', name]);
  if (code !== 0) {
    throw new Error(stderr.trim() || `copilot mcp remove exited with code ${code}`);
  }
}
