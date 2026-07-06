import * as path from 'path';

/**
 * Returns true if `target` is equal to, or a descendant of, at least one of `roots`.
 *
 * Uses `path.relative` rather than a plain string-prefix check so that a root like
 * "/Users/example/repos" doesn't accidentally match an unrelated sibling directory like
 * "/Users/example/repos-old" (a naive `target.startsWith(root)` check would allow that).
 */
export function isPathAllowed(target: string, roots: string[]): boolean {
  const resolvedTarget = path.resolve(target);
  return roots.some((root) => {
    const resolvedRoot = path.resolve(root);
    if (resolvedTarget === resolvedRoot) return true;
    const rel = path.relative(resolvedRoot, resolvedTarget);
    return rel !== '' && rel !== '..' && !rel.startsWith(`..${path.sep}`) && !path.isAbsolute(rel);
  });
}
