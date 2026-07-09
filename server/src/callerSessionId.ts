import { execFileSync } from 'child_process';

/**
 * Determines the Copilot CLI session id of whichever session is *calling* this process, by
 * inspecting the command line of the parent OS process rather than trusting anything the model
 * says about itself.
 *
 * Why this exists (PBI-025): the session-control MCP server (sessionControlMcpServer.ts) runs as
 * a stdio subprocess spawned by the `copilot` CLI itself for a specific session. The MCP protocol
 * gives a tool handler no built-in way to know which session is calling it - the original design
 * for spawn_session considered asking the client to inject a one-time "your session id is X"
 * reminder message so the model could pass it back as a tool argument, but that's fragile (long
 * conversations can lose track of it, the model might misremember/omit it, etc.) and is really a
 * workaround for something the OS already knows for free.
 *
 * Every copilot CLI invocation is spawned fresh per turn with `--session-id=<uuid>` literally in
 * its own argv (see copilotRunner.ts) - and this MCP server subprocess's *direct parent process*
 * (process.ppid) is exactly that `copilot` process, confirmed live by inspecting `ps` output while
 * a real session called an MCP tool (a sibling `sessionControlMcpServer.js` process's ppid pointed
 * straight at a `copilot ... --session-id=<uuid> ...` process). So we can read the parent's own
 * command line and extract the session id deterministically - no model cooperation required at all.
 *
 * Returns null (never throws) if anything about this lookup fails - e.g. running on an OS/shell
 * without the expected tools, the process tree looking different than expected, or the parent
 * simply not being a `copilot` invocation (defensive: this should never block a tool call from
 * working, it should just mean spawn_session can't auto-attribute a parent).
 */
export function getCallerSessionId(): string | null {
  try {
    const commandLine = getParentCommandLine(process.ppid);
    if (!commandLine) return null;
    const match = commandLine.match(/--session-id[= ]([0-9a-fA-F-]{36})/);
    return match ? match[1] : null;
  } catch {
    return null;
  }
}

function getParentCommandLine(ppid: number): string | null {
  if (process.platform === 'win32') {
    // Same Get-CimInstance approach already used by scripts/install-server-startup-windows.ps1,
    // for consistency - avoids depending on the older/deprecated `wmic`.
    const output = execFileSync(
      'powershell.exe',
      [
        '-NoProfile',
        '-Command',
        `(Get-CimInstance Win32_Process -Filter "ProcessId=${ppid}").CommandLine`,
      ],
      { encoding: 'utf8', timeout: 5000 },
    );
    return output.trim() || null;
  }

  // macOS/Linux: `ps -p <ppid> -o command=` prints just that one process's full command line
  // (the trailing `=` on the -o field suppresses the usual header row).
  const output = execFileSync('ps', ['-p', String(ppid), '-o', 'command='], { encoding: 'utf8', timeout: 5000 });
  return output.trim() || null;
}
