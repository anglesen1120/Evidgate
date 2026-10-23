// 04.2.4-T1.3: the allowed-directory guard (04.2.4 §8 "allowed directories", area plan 04
// §6; task §9 `allowed_directory_refusal`). Every case below is a refusal the operator sees
// instead of a request: outside every root, a symlink escape, `..` traversal, a directory,
// a FIFO, an empty file and an over-cap file. Nothing here touches the network.
import { execFileSync } from 'node:child_process';
import { mkdir, mkdtemp, open, realpath, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import { CliError } from './errors.js';
import {
  artifactKindMaxBytes,
  artifactKinds,
  closeUploadFile,
  defaultUploadRoots,
  mediaTypeFor,
  openUploadFile,
} from './uploadPaths.js';

/** Reads the code of a rejection without letting a passing case hide the reason. */
async function refusal(promise: Promise<unknown>): Promise<CliError> {
  try {
    await promise;
  } catch (error) {
    if (error instanceof CliError) {
      return error;
    }
    throw error;
  }
  throw new Error('expected a refusal, but the guard accepted the file');
}

describe('upload path guard', () => {
  const dirs: string[] = [];

  afterEach(async () => {
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  async function tempDir(prefix = 'evidgate-upload-'): Promise<string> {
    const dir = await mkdtemp(join(tmpdir(), prefix));
    dirs.push(dir);
    return dir;
  }

  /** A working directory holding one test root, the way a CI job lays it out. */
  async function workspace(): Promise<{
    readonly cwd: string;
    readonly root: string;
    readonly file: (name: string, text?: string) => Promise<string>;
  }> {
    const cwd = await tempDir();
    const root = join(cwd, defaultUploadRoots[0] as string);
    await mkdir(root, { recursive: true });
    return {
      cwd,
      root,
      file: async (name, text = 'trace-bytes') => {
        const path = join(root, name);
        await mkdir(join(path, '..'), { recursive: true });
        await writeFile(path, text, 'utf8');
        return path;
      },
    };
  }

  it('accepts_a_file_inside_the_default_root', async () => {
    const { cwd, file } = await workspace();
    const path = await file('trace.zip', 'zip-bytes');

    const opened = await openUploadFile({ file: path, kind: 'trace', cwd });
    try {
      expect(opened.path).toBe(path);
      expect(opened.size).toBe(Buffer.byteLength('zip-bytes'));
      expect(opened.handle.fd).toBeGreaterThan(0);
    } finally {
      await closeUploadFile(opened);
    }
  });

  it('refuses_a_relative_traversal_out_of_the_root', async () => {
    const { cwd, root } = await workspace();
    const outside = join(cwd, 'secret.txt');
    await writeFile(outside, 'not evidence', 'utf8');

    // `test-results/../secret.txt` resolves outside the root, so it is refused even though
    // the string starts with the allowed directory name.
    const error = await refusal(
      openUploadFile({ file: join(root, '..', 'secret.txt'), kind: 'raw_report', cwd }),
    );
    expect(error.code).toBe('upload_path_refused');
    expect(error.message).toContain('outside the allowed directories');
    // The message shows the bounded relative form, never an absolute temp path.
    expect(error.message).not.toContain(cwd);
  });

  it('refuses_a_file_outside_every_root', async () => {
    const { cwd } = await workspace();
    const other = await tempDir('evidgate-outside-');
    const outside = join(other, 'report.json');
    await writeFile(outside, '{}', 'utf8');

    const error = await refusal(openUploadFile({ file: outside, kind: 'raw_report', cwd }));
    expect(error.code).toBe('upload_path_refused');
    expect(error.hint).toContain('--dir');
  });

  it('accepts_an_explicit_dir_root', async () => {
    const { cwd } = await workspace();
    const extra = await tempDir('evidgate-extra-');
    const path = join(extra, 'video.mp4');
    await writeFile(path, 'mp4', 'utf8');

    const opened = await openUploadFile({ file: path, kind: 'video', dirs: [extra], cwd });
    await closeUploadFile(opened);
    expect(opened.size).toBe(3);
  });

  it('refuses_a_symlink_escape', async () => {
    const { cwd, root } = await workspace();
    const outside = join(await tempDir('evidgate-outside-'), 'passwd');
    await writeFile(outside, 'root:x:0:0', 'utf8');
    const link = join(root, 'link.zip');
    await symlink(outside, link);

    // Realpath resolves the link outside the root: the nominal path is not trusted.
    const error = await refusal(openUploadFile({ file: link, kind: 'trace', cwd }));
    expect(error.code).toBe('upload_path_refused');
  });

  it('refuses_a_symlinked_root', async () => {
    const { cwd } = await workspace();
    const real = await tempDir('evidgate-real-');
    const path = join(real, 'report.json');
    await writeFile(path, '{}', 'utf8');
    const alias = join(cwd, 'alias-root');
    await symlink(real, alias);

    // The root's FINAL component is a symlink, so realpath would adopt its target
    // wholesale: a repo-planted `alias-root -> $HOME` would authorise the whole home
    // directory. The alias is refused before any file is inspected, whatever it points at.
    const error = await refusal(
      openUploadFile({ file: path, kind: 'raw_report', dirs: [alias], cwd }),
    );
    expect(error.code).toBe('upload_path_refused');
    expect(error.message).toBe('the upload root alias-root is a symlink');

    // Naming the directory itself is accepted: the rule is about the alias, not the file.
    const opened = await openUploadFile({ file: path, kind: 'raw_report', dirs: [real], cwd });
    await closeUploadFile(opened);
    // macOS resolves /var through /private/var, so compare with the real path of the file.
    expect(opened.realPath).toBe(await realpath(path));
    expect(opened.path).toBe(path);
  });

  it('accepts_an_ancestor_alias', async () => {
    // Only the root's final component is inspected: macOS resolves every `/var` path
    // through `/private/var`, so refusing an aliased ancestor would refuse the default
    // tmpdir workspace. The same shape is built explicitly, so the case does not depend on
    // the platform.
    const parent = await tempDir('evidgate-parent-');
    const parentAlias = `${parent}-alias`;
    await symlink(parent, parentAlias);
    dirs.push(parentAlias);
    const root = join(parentAlias, 'artifacts');
    await mkdir(root, { recursive: true });
    const path = join(root, 'report.json');
    await writeFile(path, '{}', 'utf8');

    const opened = await openUploadFile({
      file: path,
      kind: 'raw_report',
      dirs: [root],
      cwd: root,
    });
    await closeUploadFile(opened);
    expect(opened.realPath).toBe(await realpath(join(parent, 'artifacts', 'report.json')));
  });

  it('refuses_a_directory', async () => {
    const { cwd, root } = await workspace();
    const path = join(root, 'nested');
    await mkdir(path);

    const error = await refusal(openUploadFile({ file: path, kind: 'raw_report', cwd }));
    expect(error.code).toBe('upload_path_refused');
    expect(error.message).toContain('not a regular file');
  });

  it('refuses_an_empty_file', async () => {
    const { cwd, file } = await workspace();
    const path = await file('empty.json', '');

    const error = await refusal(openUploadFile({ file: path, kind: 'raw_report', cwd }));
    expect(error.code).toBe('upload_path_refused');
    expect(error.message).toContain('is empty');
  });

  it('refuses_a_file_over_the_kind_cap', async () => {
    const { cwd, root } = await workspace();
    const path = join(root, 'huge.png');
    const cap = artifactKindMaxBytes.screenshot as number;
    // A sparse file of cap+1 bytes: the guard refuses on `fstat` size, no bytes are read.
    const handle = await open(path, 'w');
    await handle.truncate(cap + 1);
    await handle.close();

    const error = await refusal(openUploadFile({ file: path, kind: 'screenshot', cwd }));
    expect(error.code).toBe('upload_too_large');
    expect(error.message).toContain(String(cap));
    expect(error.message).toContain('screenshot');
  });

  it('refuses_a_fifo', async () => {
    if (process.platform === 'win32') {
      return;
    }
    const { cwd, root } = await workspace();
    const path = join(root, 'fifo.zip');
    execFileSync('mkfifo', [path]);

    const error = await refusal(openUploadFile({ file: path, kind: 'trace', cwd }));
    expect(error.code).toBe('upload_path_refused');
    expect(error.message).toContain('not a regular file');
  });

  it('refuses_a_missing_file', async () => {
    const { cwd, root } = await workspace();

    const error = await refusal(
      openUploadFile({ file: join(root, 'absent.zip'), kind: 'trace', cwd }),
    );
    expect(error.code).toBe('upload_path_refused');
    expect(error.message).toContain('ENOENT');
  });

  it('refuses_a_control_character_in_the_nominal_path', async () => {
    const { cwd } = await workspace();
    const error = await refusal(
      openUploadFile({ file: 'test-results/a\u0007.zip', kind: 'trace', cwd }),
    );
    expect(error.code).toBe('upload_path_refused');
  });

  it('has_no_cap_for_a_kind_the_policy_owns', () => {
    // 06.1.2-T3: a kind with no static default has no client-side cap, so the server's 413
    // is the surface; only the kinds with a static row can be refused locally.
    expect(artifactKindMaxBytes.dom_snapshot).toBeUndefined();
    expect(artifactKindMaxBytes.patch).toBeUndefined();
    expect(artifactKindMaxBytes.trace).toBeGreaterThan(0);
  });

  it('derives_the_media_type_only_from_the_static_defaults', () => {
    expect(mediaTypeFor('trace', 'a.zip')).toBe('application/zip');
    expect(mediaTypeFor('video', 'a.MP4')).toBe('video/mp4');
    expect(mediaTypeFor('screenshot', 'a.jpeg')).toBe('image/jpeg');
    expect(mediaTypeFor('raw_report', 'a.json')).toBe('application/json');
    // The static table has no default for a `.xml` trace or a `.png` report, and no row at
    // all for the policy kinds: `--media-type` is required there.
    expect(mediaTypeFor('trace', 'a.xml')).toBeUndefined();
    expect(mediaTypeFor('raw_report', 'a.png')).toBeUndefined();
    expect(mediaTypeFor('screenshot', 'a.zip')).toBeUndefined();
    expect(mediaTypeFor('dom_snapshot', 'a.html')).toBeUndefined();
    expect(artifactKinds).toHaveLength(8);
  });
});
