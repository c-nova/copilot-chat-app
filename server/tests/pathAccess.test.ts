import * as path from 'path';
import { isPathAllowed } from '../src/pathAccess';

describe('isPathAllowed', () => {
  const roots = ['/Users/example/repos', '/Volumes/data/projects'];

  it('allows a root itself', () => {
    expect(isPathAllowed('/Users/example/repos', roots)).toBe(true);
  });

  it('allows a descendant of a root', () => {
    expect(isPathAllowed('/Users/example/repos/my-app/src', roots)).toBe(true);
  });

  it('allows a descendant of a second root', () => {
    expect(isPathAllowed('/Volumes/data/projects/foo', roots)).toBe(true);
  });

  it('rejects a sibling directory that merely shares a prefix', () => {
    expect(isPathAllowed('/Users/example/repos-old', roots)).toBe(false);
  });

  it('rejects the parent directory of a root', () => {
    expect(isPathAllowed('/Users/example', roots)).toBe(false);
  });

  it('rejects a path outside all roots', () => {
    expect(isPathAllowed('/etc/passwd', roots)).toBe(false);
  });

  it('rejects path traversal attempts that escape back out of a root', () => {
    expect(isPathAllowed(path.join('/Users/example/repos', '..', '..', 'etc'), roots)).toBe(false);
  });

  it('returns false when no roots are configured', () => {
    expect(isPathAllowed('/Users/example/repos', [])).toBe(false);
  });
});
