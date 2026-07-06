import { execFileSync } from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { deriveRepoFolderName, gitClone, listDir } from '../src/fsBrowser';

function mkTempDir(prefix: string): string {
  return fs.mkdtempSync(path.join(os.tmpdir(), prefix));
}

describe('listDir', () => {
  let rootA: string;
  let rootB: string;

  beforeAll(() => {
    rootA = mkTempDir('fsBrowser-rootA-');
    rootB = mkTempDir('fsBrowser-rootB-');
    fs.mkdirSync(path.join(rootA, 'project-one'));
    fs.mkdirSync(path.join(rootA, 'project-two'));
    fs.mkdirSync(path.join(rootA, '.hidden'));
    fs.writeFileSync(path.join(rootA, 'not-a-dir.txt'), 'hi');
  });

  afterAll(() => {
    fs.rmSync(rootA, { recursive: true, force: true });
    fs.rmSync(rootB, { recursive: true, force: true });
  });

  it('returns the root list when multiple roots are configured and no path is given', () => {
    const result = listDir(undefined, [rootA, rootB]);
    expect(result.path).toBeNull();
    expect(result.parentPath).toBeNull();
    expect(result.roots).toEqual([rootA, rootB]);
    expect(result.entries.map((e) => e.path).sort()).toEqual([rootA, rootB].sort());
  });

  it('lists the single root directly when only one root is configured and no path is given', () => {
    const result = listDir(undefined, [rootA]);
    expect(result.path).toBe(path.resolve(rootA));
    expect(result.parentPath).toBeNull();
    expect(result.entries.map((e) => e.name).sort()).toEqual(['project-one', 'project-two']);
  });

  it('excludes dotfiles and non-directory entries', () => {
    const result = listDir(rootA, [rootA]);
    expect(result.entries.map((e) => e.name)).not.toContain('.hidden');
    expect(result.entries.map((e) => e.name)).not.toContain('not-a-dir.txt');
  });

  it('sets parentPath to the parent directory when browsing a subfolder', () => {
    const sub = path.join(rootA, 'project-one');
    const result = listDir(sub, [rootA]);
    expect(result.parentPath).toBe(path.resolve(rootA));
  });

  it('throws when the requested path is outside all allowed roots', () => {
    expect(() => listDir('/etc', [rootA])).toThrow(/outside the allowed browse roots/);
  });
});

describe('deriveRepoFolderName', () => {
  it('strips a .git suffix from an https URL', () => {
    expect(deriveRepoFolderName('https://github.com/example/my-repo.git')).toBe('my-repo');
  });

  it('handles URLs without a .git suffix', () => {
    expect(deriveRepoFolderName('https://github.com/example/my-repo')).toBe('my-repo');
  });

  it('handles SSH-style specs', () => {
    expect(deriveRepoFolderName('git@github.com:example/my-repo.git')).toBe('my-repo');
  });

  it('ignores a trailing slash', () => {
    expect(deriveRepoFolderName('https://github.com/example/my-repo/')).toBe('my-repo');
  });
});

describe('gitClone', () => {
  let parentDir: string;
  let sourceRepo: string;

  beforeAll(() => {
    parentDir = mkTempDir('fsBrowser-clone-parent-');
    sourceRepo = mkTempDir('fsBrowser-clone-source-');
    // Set up a tiny local git repo to clone from, so the test doesn't depend on network access.
    const git = (args: string[]) => execFileSync('git', args, { cwd: sourceRepo, stdio: 'ignore' });
    git(['init', '-q']);
    git(['config', 'user.email', 'test@example.com']);
    git(['config', 'user.name', 'Test']);
    fs.writeFileSync(path.join(sourceRepo, 'README.md'), '# test repo\n');
    git(['add', '.']);
    git(['commit', '-q', '-m', 'initial commit']);
  });

  afterAll(() => {
    fs.rmSync(parentDir, { recursive: true, force: true });
    fs.rmSync(sourceRepo, { recursive: true, force: true });
  });

  it('clones a repo into an auto-derived subfolder name', async () => {
    const destName = 'cloned-repo';
    const dest = await gitClone(parentDir, sourceRepo, destName, [parentDir]);
    expect(dest).toBe(path.join(path.resolve(parentDir), destName));
    expect(fs.existsSync(path.join(dest, 'README.md'))).toBe(true);
  });

  it('rejects a parentPath outside the allowed roots', async () => {
    await expect(gitClone('/etc', sourceRepo, 'x', [parentDir])).rejects.toThrow(/outside the allowed browse roots/);
  });

  it('rejects a repo URL that looks like a CLI flag', async () => {
    await expect(gitClone(parentDir, '--upload-pack=evil', undefined, [parentDir])).rejects.toThrow(/Invalid repository URL/);
  });

  it('rejects cloning into a destination that already exists', async () => {
    const existingName = 'already-here';
    fs.mkdirSync(path.join(parentDir, existingName));
    await expect(gitClone(parentDir, sourceRepo, existingName, [parentDir])).rejects.toThrow(/already exists/);
  });

  it('rejects a destination folder name containing a path separator', async () => {
    await expect(gitClone(parentDir, sourceRepo, 'nested/name', [parentDir])).rejects.toThrow(/Invalid destination folder name/);
  });
});
