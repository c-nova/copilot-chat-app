import spawn from 'cross-spawn';
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

/** Picks a short, human-readable one-line summary out of a tool call's arguments object. */
function summarizeToolArguments(args: any): string | undefined {
  if (!args || typeof args !== 'object') return undefined;

  const preferredKeys = ['description', 'command', 'question', 'query', 'path', 'url', 'text', 'message'];
  for (const key of preferredKeys) {
    const value = args[key];
    if (typeof value === 'string' && value.trim()) {
      return truncate(value);
    }
  }
  for (const value of Object.values(args)) {
    if (typeof value === 'string' && value.trim()) {
      return truncate(value);
    }
  }
  return undefined;
}

function truncate(text: string, max = 140): string {
  const oneLine = text.replace(/\s+/g, ' ').trim();
  return oneLine.length > max ? `${oneLine.slice(0, max - 1)}…` : oneLine;
}

/** Formats a tool call's full arguments as readable multi-line text for the "tap for detail" view. */
function formatToolDetail(args: any, max = 4000): string | undefined {
  if (!args || typeof args !== 'object' || Object.keys(args).length === 0) return undefined;
  let text: string;
  try {
    text = JSON.stringify(args, null, 2);
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
export function runCopilotTurn(
  sessionId: string,
  message: string,
  onDelta: DeltaHandler,
  onToolEvent: ToolEventHandler,
): Promise<TurnResult> {
  return new Promise((resolve, reject) => {
    const args = [
      '-p', message,
      '--allow-all-tools',
      '--allow-all-paths',
      '--allow-all-urls',
      '-C', config.workDir,
      '--output-format', 'json',
      '-s',
      `--session-id=${sessionId}`,
      '--no-color',
    ];
    if (config.model) {
      args.push('--model', config.model);
    }

    const child = spawn(config.copilotCommand, args, {
      windowsHide: true,
    });

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
