// `evidgate quickstart --write` plus the containment helper it relies on: the template is
// created exclusively (mode 0644) inside the current working directory, and every path
// shape that could escape the directory or overwrite an existing file is refused first.
import { existsSync } from 'node:fs';
import { chmod, mkdir, mkdtemp, readFile, rm, stat, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { main } from '../main.js';

import { capture } from './cliSpec.testing.js';
import { isInside, writeTemplate } from './quickstart.js';
import { quickstartEnv, sampleProject } from './quickstart.testing.js';

describe('quickstart --write', () => {
  const dirs: string[] = [];
  let originalCwd = '';

  beforeEach(async () => {
    originalCwd = process.cwd();
    const dir = await mkdtemp(join(tmpdir(), 'evidgate-quickstart-'));
    dirs.push(dir);
    process.chdir(dir);
  });

  afterEach(async () => {
    process.chdir(originalCwd);
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  // `--write` creates the file (mode 0644) and any missing parent directories.
  it('writes the rendered template, creating parent directories', async () => {
    const printed = capture();
    await expect(main(['quickstart'], quickstartEnv(sampleProject), printed.io)).resolves.toBe(0);
    const expected = printed.stdout.join('');

    const { io, stdout, stderr } = capture();
    await expect(
      main(['quickstart', '--write', 'ci/evidgate.yml'], quickstartEnv(sampleProject), io),
    ).resolves.toBe(0);
    expect(stdout.join('')).toBe('wrote ci/evidgate.yml\n');
    expect(stderr.join('')).toBe('');

    const target = join(process.cwd(), 'ci/evidgate.yml');
    expect(await readFile(target, 'utf8')).toBe(expected);
    expect((await stat(target)).mode & 0o777).toBe(0o644);
  });

  // The exclusive `wx` flag never overwrites an existing file.
  it('refuses to overwrite an existing file', async () => {
    await writeFile(join(process.cwd(), 'out.yml'), 'keep me', 'utf8');
    const { io, stdout, stderr } = capture();
    await expect(
      main(['quickstart', '--write', 'out.yml'], quickstartEnv(sampleProject), io),
    ).resolves.toBe(3);
    expect(stderr.join('')).toContain('file exists');
    expect(stdout.join('')).toBe('');
    expect(await readFile(join(process.cwd(), 'out.yml'), 'utf8')).toBe('keep me');
  });

  // A `..` escape is refused and nothing is created outside the cwd.
  it('refuses a path that escapes the working directory', async () => {
    const outside = join(process.cwd(), '..', 'quickstart-outside.yml');
    const { io, stdout, stderr } = capture();
    await expect(
      main(
        ['quickstart', '--write', '../quickstart-outside.yml'],
        quickstartEnv(sampleProject),
        io,
      ),
    ).resolves.toBe(3);
    expect(stderr.join('')).toContain('inside the current working directory');
    expect(stdout.join('')).toBe('');
    expect(existsSync(outside)).toBe(false);
  });

  // A symlinked parent that points outside the cwd is refused by the realpath check.
  it('refuses a symlinked parent that points outside the working directory', async () => {
    const outside = await mkdtemp(join(tmpdir(), 'evidgate-quickstart-outside-'));
    dirs.push(outside);
    await symlink(outside, join(process.cwd(), 'link'), 'dir');
    const { io, stdout, stderr } = capture();
    await expect(
      main(['quickstart', '--write', 'link/out.yml'], quickstartEnv(sampleProject), io),
    ).resolves.toBe(3);
    expect(stderr.join('')).toContain('inside the current working directory');
    expect(stdout.join('')).toBe('');
    expect(existsSync(join(outside, 'out.yml'))).toBe(false);
  });

  // A dangling-symlink parent makes `realpath` fail with ENOENT; it is a `usage` error
  // whose fixed message never carries an absolute path.
  it('refuses a dangling-symlink parent with a fixed usage message', async () => {
    await symlink(join(process.cwd(), 'missing-dir'), join(process.cwd(), 'dangling'), 'dir');
    const { io, stdout, stderr } = capture();
    await expect(
      main(['quickstart', '--write', 'dangling/out.yml'], quickstartEnv(sampleProject), io),
    ).resolves.toBe(3);
    expect(stderr.join('')).toContain('usage');
    expect(stderr.join('')).not.toContain('/');
    expect(stdout.join('')).toBe('');
  });

  // A regular file used as a parent directory is a path-shape problem, so `usage`.
  it('refuses a file used as a parent directory', async () => {
    await writeFile(join(process.cwd(), 'blocker'), 'not a directory', 'utf8');
    const { io, stdout, stderr } = capture();
    await expect(
      main(['quickstart', '--write', 'blocker/out.yml'], quickstartEnv(sampleProject), io),
    ).resolves.toBe(3);
    expect(stderr.join('')).toContain('usage');
    expect(stdout.join('')).toBe('');
    expect(await readFile(join(process.cwd(), 'blocker'), 'utf8')).toBe('not a directory');
  });

  // An unwritable directory is not a path-shape problem, so `write_failed`, and nothing
  // is created. Skipped as root, where 0555 is still writable.
  it.skipIf(typeof process.getuid === 'function' && process.getuid() === 0)(
    'reports write_failed for an unwritable directory and creates nothing',
    async () => {
      const readOnly = join(process.cwd(), 'ro');
      await mkdir(readOnly);
      await chmod(readOnly, 0o555);
      const { io, stdout, stderr } = capture();
      await expect(
        main(['quickstart', '--write', 'ro/out.yml'], quickstartEnv(sampleProject), io),
      ).resolves.toBe(3);
      expect(stderr.join('')).toContain('write_failed');
      expect(stderr.join('')).not.toContain('/');
      expect(stdout.join('')).toBe('');
      expect(existsSync(join(readOnly, 'out.yml'))).toBe(false);
    },
  );

  // A write that fails after the exclusive create removes the file it created, so a
  // retry does not hit `file exists`.
  it('removes the created file when the write fails, so a retry succeeds', async () => {
    const target = join(process.cwd(), 'out.yml');
    await expect(
      writeTemplate('out.yml', 'text', {
        writeFile: () => Promise.reject(Object.assign(new Error('disk full'), { code: 'ENOSPC' })),
      }),
    ).rejects.toMatchObject({ code: 'write_failed' });
    expect(existsSync(target)).toBe(false);

    await expect(writeTemplate('out.yml', 'text')).resolves.toBe('out.yml');
    expect(await readFile(target, 'utf8')).toBe('text');
  });

  // The echoed path is escaped, so a control character in the filename cannot forge a
  // log line.
  it('escapes control characters in the printed wrote path', async () => {
    const { io, stdout, stderr } = capture();
    await expect(
      main(['quickstart', '--write', 'out\u0007.yml'], quickstartEnv(sampleProject), io),
    ).resolves.toBe(0);
    expect(stdout.join('')).toBe('wrote out\\u0007.yml\n');
    expect(stdout.join('')).not.toContain('\u0007');
    expect(stderr.join('')).toBe('');
  });
});

// The containment helper is decided with `path.relative`, so it stays correct when the
// base is the filesystem root: the old `startsWith(base + sep)` test compared against
// `//` and rejected every path under `/`.
describe('isInside', () => {
  it('treats the filesystem root as containing every absolute path', () => {
    expect(isInside('/', '/')).toBe(true);
    expect(isInside('/', '/tmp')).toBe(true);
    expect(isInside('/', '/private/tmp')).toBe(true);
  });

  it('contains the directory itself and its descendants, not siblings or parents', () => {
    expect(isInside('/a', '/a')).toBe(true);
    expect(isInside('/a', '/a/b')).toBe(true);
    expect(isInside('/a', '/ab')).toBe(false);
    expect(isInside('/a', '/')).toBe(false);
    expect(isInside('/a/b', '/a')).toBe(false);
  });
});
