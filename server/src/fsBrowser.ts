import spawn from 'cross-spawn';
import * as fs from 'fs';
import * as path from 'path';
import { isPathAllowed } from './pathAccess';

export interface FsEntry {
  name: string;
  path: string;
  isDir: boolean;
}

export interface ListDirResult {
  /** Absolute path being listed, or null when showing the root-selection level (multiple BROWSE_ROOTS, no path chosen yet). */
  path: string | null;
  /** Directory to navigate up to via another listDir call, or null if there's nowhere to go up to. */
  parentPath: string | null;
  entries: FsEntry[];
  /** The full configured allow-list, always included so the client can offer a "back to roots" shortcut whenever there's more than one. */
  roots: string[];
}

/**
 * Lists subdirectories of `requestedPath` (folders only, dotfiles excluded), for the new-session
 * folder-picker flow. `requestedPath` must fall within one of `allowedRoots` (BROWSE_ROOTS) or this
 * throws. Omitting `requestedPath` starts at the top: the root list itself if there's more than one
 * configured root, or straight into that root's contents if there's only one.
 */
export function listDir(requestedPath: string | undefined, allowedRoots: string[]): ListDirResult {
  if (!requestedPath) {
    if (allowedRoots.length > 1) {
      return {
        path: null,
        parentPath: null,
        roots: allowedRoots,
        entries: allowedRoots
          .map((r) => ({ name: path.basename(r) || r, path: r, isDir: true }))
          .sort((a, b) => a.name.localeCompare(b.name)),
      };
    }
    requestedPath = allowedRoots[0];
  }

  const resolved = path.resolve(requestedPath);
  if (!isPathAllowed(resolved, allowedRoots)) {
    throw new Error(`Path is outside the allowed browse roots: ${requestedPath}`);
  }

  const dirents = fs.readdirSync(resolved, { withFileTypes: true });
  const entries: FsEntry[] = dirents
    .filter((d) => d.isDirectory() && !d.name.startsWith('.'))
    .map((d) => ({ name: d.name, path: path.join(resolved, d.name), isDir: true }))
    .sort((a, b) => a.name.localeCompare(b.name));

  const isAtARoot = allowedRoots.some((r) => path.resolve(r) === resolved);
  const parentPath = isAtARoot ? null : path.dirname(resolved);

  return { path: resolved, parentPath, entries, roots: allowedRoots };
}

/** Derives a destination folder name from a repo URL/SSH spec, e.g. "https://.../foo.git" -> "foo". */
export function deriveRepoFolderName(repoUrl: string): string {
  const trimmed = repoUrl.trim().replace(/\/+$/, '');
  const last = trimmed.split(/[\\/]/).pop() ?? 'repo';
  const withoutGitSuffix = last.replace(/\.git$/i, '');
  return withoutGitSuffix || 'repo';
}

const INVALID_FOLDER_NAME_CHARS = /[\\/]/;

/**
 * Clones `repoUrl` into a new subfolder of `parentPath` (auto-named from the repo unless `destName`
 * is given), returning the resulting absolute path. `parentPath` must fall within one of
 * `allowedRoots`; the destination folder must not already exist.
 */
export async function gitClone(
  parentPath: string,
  repoUrl: string,
  destName: string | undefined,
  allowedRoots: string[],
): Promise<string> {
  const trimmedUrl = repoUrl.trim();
  if (!trimmedUrl) {
    throw new Error('Repository URL is required.');
  }
  // git parses argv itself; a URL starting with "-" could otherwise be misread as a git option
  // (argument injection) even though spawn() below passes args as an array, not through a shell.
  if (trimmedUrl.startsWith('-')) {
    throw new Error('Invalid repository URL.');
  }

  const resolvedParent = path.resolve(parentPath);
  if (!isPathAllowed(resolvedParent, allowedRoots)) {
    throw new Error(`Destination folder is outside the allowed browse roots: ${parentPath}`);
  }

  const folderName = (destName && destName.trim()) || deriveRepoFolderName(trimmedUrl);
  if (!folderName || folderName === '.' || folderName === '..' || INVALID_FOLDER_NAME_CHARS.test(folderName)) {
    throw new Error(`Invalid destination folder name: ${folderName}`);
  }

  const dest = path.join(resolvedParent, folderName);
  if (!isPathAllowed(dest, allowedRoots)) {
    throw new Error(`Destination folder is outside the allowed browse roots: ${dest}`);
  }
  if (fs.existsSync(dest)) {
    throw new Error(`Destination already exists: ${dest}`);
  }

  await new Promise<void>((resolve, reject) => {
    const child = spawn('git', ['clone', trimmedUrl, dest], { windowsHide: true });
    let stderr = '';
    child.stderr?.on('data', (chunk: Buffer) => {
      stderr += chunk.toString('utf8');
    });
    child.on('error', reject);
    child.on('close', (code) => {
      if (code === 0) resolve();
      else reject(new Error(stderr.trim() || `git clone exited with code ${code}`));
    });
  });

  return dest;
}
