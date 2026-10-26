// 04.3.3-T2.1: the allow-listed directory guard of attachment paths (plan §2 AC6, §8 "04 §5
// traversal row", §9 `paths.test.ts`). Real temporary directories: the guard resolves real
// paths and inspects types, so a mocked file system would prove nothing.
//
// The cases are the escapes the plan names — `..` traversal, an absolute path outside, a
// symlink planted inside a root that points outside — plus the boundary the CLI's own guard
// already had to get right (`..foo` is a child, not a traversal) and the file types a FIFO or
// a directory must never be mistaken for (a FIFO would park the reporter forever).
import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, realpathSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, isAbsolute, join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import { createPathGuard, defaultAttachmentRoots, type PathVerdict } from './attachPaths.js';

const tempDirs: string[] = [];

function tempDir(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-attach-paths-'));
  tempDirs.push(dir);
  return dir;
}

/** Creates a file (and its directories) and returns its absolute path. */
function file(path: string, content = 'x'): string {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, content, 'utf8');
  return path;
}

/** The refusal of a verdict; fails the test when the guard accepted the path. */
function refusal(verdict: PathVerdict): { code: string; message: string } {
  if (verdict.ok) throw new Error(`expected a refusal, got ${verdict.realPath}`);
  return { code: verdict.code, message: verdict.message };
}

/** True when `mkfifo` exists, so the FIFO case is skipped on a platform without it. */
function mkfifoAvailable(): boolean {
  try {
    execFileSync('sh', ['-c', 'command -v mkfifo'], { stdio: 'ignore' });
    return true;
  } catch {
    return false;
  }
}

const MKFIFO = mkfifoAvailable();

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

describe('createPathGuard: allowed paths', () => {
  it('accepts a regular file inside a root and reports its real path and size', async () => {
    const dir = tempDir();
    const out = join(dir, 'test-results');
    const target = file(join(out, 'trace.zip'), 'abc');
    const guard = createPathGuard({ roots: [out], cwd: dir });

    expect(await guard.check(target)).toEqual({
      ok: true,
      realPath: realpathSync(target),
      size: 3,
    });
  });

  it('resolves a relative path against cwd', async () => {
    const dir = tempDir();
    const out = join(dir, 'test-results');
    const target = file(join(out, 'nested', 'shot.png'));
    const guard = createPathGuard({ roots: [out], cwd: out });

    expect(await guard.check(join('nested', 'shot.png'))).toEqual({
      ok: true,
      realPath: realpathSync(target),
      size: 1,
    });
  });

  it('accepts a child literally named "..foo"', async () => {
    const dir = tempDir();
    const out = join(dir, 'test-results');
    const target = file(join(out, '..foo'));
    const guard = createPathGuard({ roots: [out], cwd: out });

    expect(await guard.check('..foo')).toEqual({
      ok: true,
      realPath: realpathSync(target),
      size: 1,
    });
  });

  it('accepts a symlink inside a root that points to a file inside the same root', async () => {
    const dir = tempDir();
    const out = join(dir, 'test-results');
    const target = file(join(out, 'real', 'video.webm'));
    const link = join(out, 'alias.webm');
    symlinkSync(target, link);
    const guard = createPathGuard({ roots: [out], cwd: out });

    expect(await guard.check(link)).toEqual({
      ok: true,
      realPath: realpathSync(target),
      size: 1,
    });
  });

  it('ignores roots that do not exist instead of failing every check', async () => {
    const dir = tempDir();
    const out = join(dir, 'test-results');
    const target = file(join(out, 'trace.zip'));
    const guard = createPathGuard({ roots: [join(dir, 'never-written'), out], cwd: dir });

    expect(await guard.check(target)).toMatchObject({ ok: true, realPath: realpathSync(target) });
  });
});

describe('createPathGuard: refused paths', () => {
  it('refuses a .. traversal out of the root', async () => {
    const dir = tempDir();
    const out = join(dir, 'test-results');
    mkdirSync(out, { recursive: true });
    file(join(dir, 'outside.zip'));
    const guard = createPathGuard({ roots: [out], cwd: out });

    const { code, message } = refusal(await guard.check('../outside.zip'));
    expect(code).toBe('attachment_path_refused');
    expect(message).toContain('outside.zip');
    expect(message).not.toContain(dir);
  });

  it('refuses an absolute path outside every root', async () => {
    const dir = tempDir();
    const out = join(dir, 'test-results');
    mkdirSync(out, { recursive: true });
    const outside = file(join(dir, 'outside.zip'));
    const guard = createPathGuard({ roots: [out], cwd: out });

    const { code, message } = refusal(await guard.check(outside));
    expect(code).toBe('attachment_path_refused');
    expect(message).not.toContain(outside);
  });

  it('refuses a symlink inside a root that points outside it', async () => {
    const dir = tempDir();
    const out = join(dir, 'test-results');
    const outside = file(join(dir, 'outside', 'secret.txt'));
    const link = join(out, 'escape.zip');
    mkdirSync(out, { recursive: true });
    symlinkSync(outside, link);
    const guard = createPathGuard({ roots: [out], cwd: out });

    const { code, message } = refusal(await guard.check(link));
    expect(code).toBe('attachment_path_refused');
    expect(message).toContain('escape.zip');
    expect(message).not.toContain(outside);
  });

  it('refuses a directory, including a root itself', async () => {
    const dir = tempDir();
    const out = join(dir, 'test-results');
    mkdirSync(join(out, 'nested'), { recursive: true });
    const guard = createPathGuard({ roots: [out], cwd: out });

    expect(refusal(await guard.check(join(out, 'nested'))).code).toBe('attachment_path_refused');
    expect(refusal(await guard.check(out)).code).toBe('attachment_path_refused');
  });

  it.skipIf(!MKFIFO)('refuses a FIFO', async () => {
    const dir = tempDir();
    const out = join(dir, 'test-results');
    mkdirSync(out, { recursive: true });
    const fifo = join(out, 'pipe.zip');
    execFileSync('mkfifo', [fifo]);
    const guard = createPathGuard({ roots: [out], cwd: out });

    const { code, message } = refusal(await guard.check(fifo));
    expect(code).toBe('attachment_path_refused');
    expect(message).toContain('pipe.zip');
  });

  it('refuses a Windows drive-letter path on posix', async () => {
    const dir = tempDir();
    const out = join(dir, 'test-results');
    mkdirSync(out, { recursive: true });
    const guard = createPathGuard({ roots: [out], cwd: out, platform: 'linux' });

    expect(refusal(await guard.check('C:\\x\\trace.zip')).code).toBe('attachment_path_refused');
    expect(refusal(await guard.check('c:/x/trace.zip')).code).toBe('attachment_path_refused');
  });

  it('refuses a Windows UNC path on posix', async () => {
    const dir = tempDir();
    const out = join(dir, 'test-results');
    mkdirSync(out, { recursive: true });
    const guard = createPathGuard({ roots: [out], cwd: out, platform: 'linux' });

    const { code, message } = refusal(await guard.check('\\\\host\\share\\trace.zip'));
    expect(code).toBe('attachment_path_refused');
    expect(message).not.toContain('host');
  });

  it('does not apply the Windows form check on win32', async () => {
    const dir = tempDir();
    const out = join(dir, 'test-results');
    mkdirSync(out, { recursive: true });
    const guard = createPathGuard({ roots: [out], cwd: out, platform: 'win32' });

    // The form is not refused by the platform rule; on a posix file system the path then
    // simply does not resolve, which is an unreadable file rather than a refusal.
    const { code, message } = refusal(await guard.check('C:\\x\\trace.zip'));
    expect(code).toBe('attachment_unreadable');
    expect(message).toContain('ENOENT');
  });

  it('refuses an empty or blank path', async () => {
    const dir = tempDir();
    const out = join(dir, 'test-results');
    mkdirSync(out, { recursive: true });
    const guard = createPathGuard({ roots: [out], cwd: out });

    expect(refusal(await guard.check('')).code).toBe('attachment_path_refused');
    expect(refusal(await guard.check('   ')).code).toBe('attachment_path_refused');
  });

  it('refuses a path containing a NUL character and escapes it in the message', async () => {
    const dir = tempDir();
    const out = join(dir, 'test-results');
    mkdirSync(out, { recursive: true });
    const guard = createPathGuard({ roots: [out], cwd: out });

    const { code, message } = refusal(await guard.check('trace\0.zip'));
    expect(code).toBe('attachment_path_refused');
    expect(message).toContain('\\u{0}');
    expect(message).not.toContain('\0');
  });

  it('refuses every path when no root exists', async () => {
    const dir = tempDir();
    const guard = createPathGuard({ roots: [join(dir, 'never-written')], cwd: dir });

    expect(refusal(await guard.check(file(join(dir, 'trace.zip')))).code).toBe(
      'attachment_path_refused',
    );
  });
});

describe('createPathGuard: unreadable paths', () => {
  it('reports a missing file as attachment_unreadable', async () => {
    const dir = tempDir();
    const out = join(dir, 'test-results');
    mkdirSync(out, { recursive: true });
    const guard = createPathGuard({ roots: [out], cwd: out });

    const { code, message } = refusal(await guard.check('missing.zip'));
    expect(code).toBe('attachment_unreadable');
    expect(message).toContain('ENOENT');
    expect(message).toContain('missing.zip');
    expect(message).not.toContain(out);
  });
});

describe('defaultAttachmentRoots', () => {
  it('lists outputDirs, test-results and extra dirs as absolute, de-duplicated paths', () => {
    const dir = tempDir();
    const out = join(dir, 'out');

    const roots = defaultAttachmentRoots({
      outputDirs: [out],
      rootDir: dir,
      extraDirs: ['out', 'extra', join(dir, 'extra'), '', '   '],
    });

    expect(roots).toEqual([out, join(dir, 'test-results'), join(dir, 'extra')]);
    expect(roots.every((root) => isAbsolute(root))).toBe(true);
  });

  it('keeps test-results when no output directory is reported', () => {
    const dir = tempDir();
    expect(defaultAttachmentRoots({ outputDirs: [], rootDir: dir, extraDirs: [] })).toEqual([
      join(dir, 'test-results'),
    ]);
  });
});
