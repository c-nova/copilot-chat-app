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
// PBI-028 perf fix: this process's ppid (and that parent's own argv) never changes for the
// lifetime of this MCP subprocess, so the expensive OS lookup below only ever needs to run once -
// but list_my_children (added in PBI-028) now calls getCallerSessionId() *in addition to*
// spawn_session's own call, doubling how often it ran per turn. On Windows this lookup shells out
// to powershell.exe + Get-CimInstance, which is slow on its own (process startup + a WMI query)
// and can be much slower still on a corporate-managed machine with AV/EDR scanning every spawned
// process - doubling it was directly responsible for a user-visible multi-minute slowdown on
// cross-server (Windows peer) turns. `undefined` = not yet computed; `null` is a valid cached
// "lookup failed" result that should NOT be retried (retrying wouldn't succeed differently anyway,
// since the parent process and its argv are fixed for this subprocess's whole lifetime).
let cachedCallerSessionId: string | null | undefined;

export function getCallerSessionId(): string | null {
  if (cachedCallerSessionId !== undefined) {
    return cachedCallerSessionId;
  }
  try {
    const commandLine = getParentCommandLine(process.ppid);
    const match = commandLine?.match(/--session-id[= ]([0-9a-fA-F-]{36})/);
    cachedCallerSessionId = match ? match[1] : null;
  } catch {
    cachedCallerSessionId = null;
  }
  return cachedCallerSessionId;
}

/** Test-only: clears the memoized result so each test case can simulate a fresh subprocess. */
export function __resetCallerSessionIdCacheForTests(): void {
  cachedCallerSessionId = undefined;
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
