import spawn from 'cross-spawn';
import * as crypto from 'crypto';
import * as fs from 'fs/promises';
import * as os from 'os';
import * as path from 'path';
import { config } from './config';

export type DeltaHandler = (text: string) => void;
export type ToolEventHandler = (event: ToolEvent) => void;

export interface ToolEvent {
  status: 'start' | 'complete';
  toolCallId: string;
  name: string;
  summary?: string;
  detail?: string;
  success?: boolean;
}

export interface TurnResult {
  finalText: string;
  exitCode: number | null;
  sessionId: string;
}

/** An inline attachment to write to a temp file and pass to the CLI via --attachment (PBI-019). */
export interface AttachmentInput {
  mimeType: string;
  /** Base64-encoded file bytes. */
  data: string;
  fileName?: string;
}

const MIME_EXTENSIONS: Record<string, string> = {
  'image/png': '.png',
  'image/jpeg': '.jpg',
  'image/jpg': '.jpg',
  'image/gif': '.gif',
  'image/webp': '.webp',
  'image/heic': '.heic',
  'application/pdf': '.pdf',
};

function extensionFor(att: AttachmentInput): string {
  if (att.fileName) {
    const ext = path.extname(att.fileName);
    if (ext) return ext;
  }
  return MIME_EXTENSIONS[att.mimeType] ?? '';
}

/**
 * Writes each attachment's base64 payload to a private temp file, since the CLI's --attachment
 * flag only accepts filesystem paths (there's no way to hand it in-memory bytes directly - see
 * PBI-019 design notes). Files land in the OS temp dir (already a per-user, non-world-readable
 * location) and are additionally chmod'd 0600 for defense in depth.
 */
async function writeTempAttachments(attachments: AttachmentInput[]): Promise<string[]> {
  const paths: string[] = [];
  for (const att of attachments) {
    const tmpPath = path.join(os.tmpdir(), `copilot-attach-${crypto.randomUUID()}${extensionFor(att)}`);
    const buffer = Buffer.from(att.data, 'base64');
    await fs.writeFile(tmpPath, buffer, { mode: 0o600 });
    paths.push(tmpPath);
  }
  return paths;
}

/** Best-effort cleanup - a leftover temp file isn't worth failing the turn over. */
async function cleanupTempFiles(paths: string[]): Promise<void> {
  await Promise.all(
    paths.map((p) =>
      fs.unlink(p).catch((err) => {
        console.warn(`[copilotRunner] failed to remove temp attachment ${p}:`, err?.message ?? err);
      }),
    ),
  );
}

/** Key names (case-insensitive) whose values look like secrets and shouldn't be shown in the UI, e.g. when a tool call carries an MCP server's auth header or API key in its arguments. */
const SENSITIVE_KEY_PATTERN = /token|password|passwd|secret|api[-_]?key|authorization|auth[-_]?header|cookie|credential/i;
const REDACTED = '***REDACTED***';

/** Recursively replaces values whose key name looks sensitive with a redaction marker. */
function redactSensitiveValues(value: any): any {
  if (Array.isArray(value)) {
    return value.map(redactSensitiveValues);
  }
  if (value && typeof value === 'object') {
    const result: Record<string, any> = {};
    for (const [key, v] of Object.entries(value)) {
      result[key] = SENSITIVE_KEY_PATTERN.test(key) ? REDACTED : redactSensitiveValues(v);
    }
    return result;
  }
  return value;
}

/** Picks a short, human-readable one-line summary out of a tool call's arguments object. */
export function summarizeToolArguments(args: any): string | undefined {
  if (!args || typeof args !== 'object') return undefined;

  const preferredKeys = ['description', 'command', 'question', 'query', 'path', 'url', 'text', 'message'];
  for (const key of preferredKeys) {
    const value = args[key];
    if (typeof value === 'string' && value.trim() && !SENSITIVE_KEY_PATTERN.test(key)) {
      return truncate(value);
    }
  }
  for (const [key, value] of Object.entries(args)) {
    if (typeof value === 'string' && value.trim() && !SENSITIVE_KEY_PATTERN.test(key)) {
      return truncate(value);
    }
  }
  return undefined;
}

export function truncate(text: string, max = 140): string {
  const oneLine = text.replace(/\s+/g, ' ').trim();
  return oneLine.length > max ? `${oneLine.slice(0, max - 1)}…` : oneLine;
}

/** Formats a tool call's full arguments as readable multi-line text for the "tap for detail" view. */
export function formatToolDetail(args: any, max = 4000): string | undefined {
  if (!args || typeof args !== 'object' || Object.keys(args).length === 0) return undefined;
  let text: string;
  try {
    text = JSON.stringify(redactSensitiveValues(args), null, 2);
  } catch {
    return undefined;
  }
  return text.length > max ? `${text.slice(0, max)}\n…(truncated)` : text;
}

/**
 * Runs one chat turn against the GitHub Copilot CLI in non-interactive mode, with full agent
 * capabilities enabled (file edits, shell commands, MCP tools - same as the interactive CLI).
 * Reusing the same `sessionId` across calls resumes the same conversation
 * (verified: CLI treats --session-id as "create if new, resume if existing").
 * The CLI operates inside `config.workDir` (via `-C`), and `--allow-all-tools/paths/urls`
 * pre-approves every action since there's no interactive prompt to confirm from in this mode.
 */
export async function runCopilotTurn(
  sessionId: string,
  message: string,
  onDelta: DeltaHandler,
  onToolEvent: ToolEventHandler,
  attachments?: AttachmentInput[],
  cwd?: string,
  model?: string,
): Promise<TurnResult> {
  const tempFiles = attachments && attachments.length > 0 ? await writeTempAttachments(attachments) : [];
  try {
    return await runCopilotTurnCore(sessionId, message, onDelta, onToolEvent, tempFiles, cwd, model);
  } finally {
    if (tempFiles.length > 0) {
      await cleanupTempFiles(tempFiles);
    }
  }
}

function runCopilotTurnCore(
  sessionId: string,
  message: string,
  onDelta: DeltaHandler,
  onToolEvent: ToolEventHandler,
  attachmentPaths: string[],
  cwd?: string,
  model?: string,
): Promise<TurnResult> {
  return new Promise((resolve, reject) => {
    const args = [
      '-p', message,
      '--allow-all-tools',
      '--allow-all-paths',
      '--allow-all-urls',
      '-C', cwd || config.workDir,
      '--output-format', 'json',
      '-s',
      `--session-id=${sessionId}`,
      '--no-color',
    ];
    for (const attachmentPath of attachmentPaths) {
      args.push('--attachment', attachmentPath);
    }
    const effectiveModel = model || config.model;
    if (effectiveModel) {
      args.push('--model', effectiveModel);
    }

    const child = spawn(config.copilotCommand, args, {
      windowsHide: true,
    });

    // PBI-028 follow-up: a user reported cross-server (peer) turns taking multiple minutes with no
    // way to tell whether the time went into the `copilot` CLI invocation itself (model latency,
    // tool execution, AV/EDR scanning the spawned process on a corporate-managed machine, etc.) or
    // somewhere else in the request chain (network, peerClient.ts, etc.). Logging how long this one
    // CLI invocation actually took - visible in the server's own log (e.g. CopilotChatServer.log on
    // Mac/Windows) - lets that be answered directly instead of guessing next time it happens.
    const startedAt = Date.now();

    let finalText = '';
    let stdoutBuffer = '';
    let stderrBuffer = '';
    const toolInfoByCallId = new Map<string, { name: string; detail?: string }>();

    child.stdout!.on('data', (chunk: Buffer) => {
      stdoutBuffer += chunk.toString('utf8');
      let newlineIndex: number;
      while ((newlineIndex = stdoutBuffer.indexOf('\n')) >= 0) {
        const line = stdoutBuffer.slice(0, newlineIndex).trim();
        stdoutBuffer = stdoutBuffer.slice(newlineIndex + 1);
        if (!line) continue;
        handleLine(line);
      }
    });

    function handleLine(line: string) {
      let event: any;
      try {
        event = JSON.parse(line);
      } catch {
        return; // ignore non-JSON noise
      }
      switch (event.type) {
        case 'assistant.message_delta':
          if (event.data?.deltaContent) {
            onDelta(event.data.deltaContent);
          }
          break;
        case 'assistant.message':
          if (typeof event.data?.content === 'string') {
            finalText = event.data.content;
          }
          break;
        case 'tool.execution_start': {
          const toolCallId = event.data?.toolCallId as string | undefined;
          const name = event.data?.toolName as string | undefined;
          const detail = formatToolDetail(event.data?.arguments);
          if (toolCallId && name) toolInfoByCallId.set(toolCallId, { name, detail });
          onToolEvent({
            status: 'start',
            toolCallId: toolCallId ?? '',
            name: name ?? 'tool',
            summary: summarizeToolArguments(event.data?.arguments),
            detail,
          });
          break;
        }
        case 'tool.execution_complete': {
          const toolCallId = event.data?.toolCallId as string | undefined;
          const info = toolCallId ? toolInfoByCallId.get(toolCallId) : undefined;
          onToolEvent({
            status: 'complete',
            toolCallId: toolCallId ?? '',
            name: info?.name ?? 'tool',
            detail: info?.detail,
            success: event.data?.success,
          });
          break;
        }
        default:
          break;
      }
    }

    child.stderr!.on('data', (chunk: Buffer) => {
      stderrBuffer += chunk.toString('utf8');
    });

    child.on('error', (err) => {
      reject(err);
    });

    child.on('close', (exitCode) => {
      console.log(`[copilotRunner] session ${sessionId} turn finished in ${Date.now() - startedAt}ms (exitCode=${exitCode})`);
      if (stdoutBuffer.trim()) {
        handleLine(stdoutBuffer.trim());
      }
      if (exitCode !== 0 && !finalText) {
        reject(new Error(stderrBuffer.trim() || `copilot CLI exited with code ${exitCode}`));
        return;
      }
      resolve({ finalText, exitCode, sessionId });
    });
  });
}
