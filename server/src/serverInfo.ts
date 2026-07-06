import spawn from 'cross-spawn';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { config } from './config';

export interface ServerInfo {
  /** Node's os.platform() value, e.g. "darwin" | "win32" | "linux". */
  os: string;
  hostname: string;
  /** server/package.json version. */
  appVersion: string;
  /** Output of `<copilotCommand> --version`, or "unknown" if it couldn't be determined. */
  copilotCliVersion: string;
  nodeVersion: string;
  /** Configured model override, or "(default)" when the CLI is left to decide. */
  model: string;
  workDir: string;
  browseRoots: string[];
}

function readAppVersion(): string {
  try {
    // From either src/ (ts-node/jest) or dist/ (compiled), package.json is one directory up.
    const pkgPath = path.join(__dirname, '..', 'package.json');
    const pkg = JSON.parse(fs.readFileSync(pkgPath, 'utf8'));
    return typeof pkg.version === 'string' ? pkg.version : 'unknown';
  } catch {
    return 'unknown';
  }
}

const APP_VERSION = readAppVersion();

let cachedCliVersion: string | null = null;

/**
 * Runs `<copilotCommand> --version` once and caches the result for the lifetime of the process
 * (the installed CLI version can't change while this server is running). Resolves to "unknown"
 * rather than rejecting if the command can't be found or exits abnormally - this is informational
 * metadata, not something that should ever block a turn or a Sessions list from loading.
 */
function getCopilotCliVersion(): Promise<string> {
  if (cachedCliVersion !== null) return Promise.resolve(cachedCliVersion);
  return new Promise((resolve) => {
    const child = spawn(config.copilotCommand, ['--version'], { windowsHide: true });
    let stdout = '';
    child.stdout?.on('data', (chunk: Buffer) => {
      stdout += chunk.toString('utf8');
    });
    child.on('error', () => {
      cachedCliVersion = 'unknown';
      resolve(cachedCliVersion);
    });
    child.on('close', () => {
      cachedCliVersion = stdout.trim() || 'unknown';
      resolve(cachedCliVersion);
    });
  });
}

/**
 * Collects this server's environment/version metadata - the OS/CLI/model context a controller
 * session (Phase 3) needs to reason safely about a session before dispatching work to it (e.g. not
 * assuming a Windows-flavored shell command will work against a session running elsewhere), and
 * what the Sessions list UI can show as a per-server badge (Phase 1/4).
 */
export async function getServerInfo(): Promise<ServerInfo> {
  return {
    os: os.platform(),
    hostname: os.hostname(),
    appVersion: APP_VERSION,
    copilotCliVersion: await getCopilotCliVersion(),
    nodeVersion: process.version,
    model: config.model || '(default)',
    workDir: config.workDir,
    browseRoots: config.browseRoots,
  };
}
