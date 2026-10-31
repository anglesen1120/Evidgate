// The deny-by-default allow-list every `artifacts upload` file passes before a byte is
// sent: an artifact can carry personal data, so the real path must sit under a configured
// root, and the opened descriptor is `fstat`ed to close the check/open race.
import { constants as fsConstants, type Stats } from 'node:fs';
import { lstat, open, realpath, type FileHandle } from 'node:fs/promises';
import { extname, isAbsolute, join, relative, resolve, sep } from 'node:path';

import { CliError } from './errors.js';
import { O_NOFOLLOW, O_NONBLOCK } from './spool.js';

/** The artifact kinds the server accepts (openapi `createArtifactUploadUrl` enum). */
export const artifactKinds = [
  'trace',
  'video',
  'screenshot',
  'raw_report',
  'dom_snapshot',
  'a11y_snapshot',
  'patch',
  'ui_hierarchy',
] as const;

export type ArtifactKind = (typeof artifactKinds)[number];

/**
 * The static per-kind byte cap when the project has no policy row. `undefined` means the
 * project's row is the only cap, so the CLI cannot refuse locally.
 */
export const artifactKindMaxBytes: Readonly<Record<ArtifactKind, number | undefined>> = {
  trace: 104_857_600,
  video: 209_715_200,
  screenshot: 10_485_760,
  raw_report: 268_435_456,
  dom_snapshot: undefined,
  a11y_snapshot: undefined,
  patch: undefined,
  ui_hierarchy: undefined,
};

/**
 * The roots an upload may read from when no `--dir` is given: the two directories a
 * Playwright/pytest run writes by default, relative to the working directory. `--dir` roots
 * are added to these, never replace them.
 */
export const defaultUploadRoots: readonly string[] = ['test-results', 'playwright-report'];

/** A file the guard accepted, already open and `fstat`ed. */
export interface UploadFile {
  /** The absolute, normalised path the operator named. */
  readonly path: string;
  /** Its real path (symlinks resolved) — the one the allow-list check accepted. */
  readonly realPath: string;
  /** Bytes of the opened descriptor (`fstat`), the only size that is trusted. */
  readonly size: number;
  /** `dev` of the pinned descriptor: a re-open must land on the same object. */
  readonly dev: number;
  /** `ino` of the pinned descriptor: a re-open must land on the same object. */
  readonly ino: number;
  /**
   * The open descriptor that pins the inode the guard accepted. Keep it open until the
   * last pass has finished, then release it with {@link closeUploadFile}: while it is open
   * the inode cannot be recycled, so a re-open can be compared against this one.
   */
  readonly handle: FileHandle;
}

export interface OpenUploadFileInput {
  /** The `--file` value, exactly as the operator typed it. */
  readonly file: string;
  /** The `--kind` value: decides the local byte cap. */
  readonly kind: ArtifactKind;
  /** Every `--dir` root, in the order given; resolved against `cwd`. */
  readonly dirs?: readonly string[];
  /** The process's working directory (the default roots are relative to it). */
  readonly cwd: string;
}

/** Media types the static per-kind defaults accept (`artifact-kind-defaults.v1.json`). */
const mediaTypeByExtension: Readonly<Record<string, string>> = {
  '.zip': 'application/zip',
  '.mp4': 'video/mp4',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.xml': 'application/xml',
  '.json': 'application/json',
  '.html': 'text/html',
  '.htm': 'text/html',
};

/** The extensions each kind's static default can accept, in the same order. */
const kindExtensions: Readonly<Record<string, readonly string[]>> = {
  trace: ['.zip'],
  video: ['.mp4'],
  screenshot: ['.png', '.jpg', '.jpeg'],
  raw_report: ['.xml', '.json', '.html', '.htm', '.zip'],
};

/** A file name is echoed on stdout, so match the control/format refusal of `config.ts`. */
const forbiddenInFileName = /[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]/u;

/**
 * The media type a kind's static default accepts for this file name, or `undefined` when
 * the extension decides nothing (a kind with no static default, an unknown extension, or
 * an extension no policy for that kind names). `undefined` is answered by asking the
 * operator for `--media-type` — never by guessing a type the server would reject.
 */
export function mediaTypeFor(kind: ArtifactKind, file: string): string | undefined {
  const extension = extname(file).toLowerCase();
  const accepted = kindExtensions[kind];
  if (accepted === undefined || !accepted.includes(extension)) {
    return undefined;
  }
  return mediaTypeByExtension[extension];
}

/** `upload_path_refused`: the `--file` value cannot be uploaded, decided before any call. */
export function pathRefused(message: string, hint?: string): CliError {
  return new CliError(message, {
    code: 'upload_path_refused',
    ...(hint === undefined ? {} : { hint }),
  });
}

/**
 * Opens the resolved path `O_NOFOLLOW|O_NONBLOCK`: a symlink is refused with `ELOOP` instead
 * of being followed, and a planted FIFO, device or directory cannot block the open and is
 * then refused by the regular-file check.
 */
export async function openUploadFile(input: OpenUploadFileInput): Promise<UploadFile> {
  if (input.file.length === 0) {
    throw pathRefused('--file is empty');
  }
  if (forbiddenInFileName.test(input.file)) {
    throw pathRefused('--file contains a control character');
  }

  const path = isAbsolute(input.file) ? resolve(input.file) : resolve(input.cwd, input.file);
  // Roots are resolved and realpath'd first: a root that does not exist can contain no
  // file (realpath of the file itself would have failed), so it is skipped rather than
  // turned into an error. Every other errno is a real filesystem problem.
  const roots: Array<{ readonly shown: string; readonly real: string }> = [];
  for (const root of [
    ...defaultUploadRoots.map((name) => join(input.cwd, name)),
    ...(input.dirs ?? []).map((dir) => resolve(input.cwd, dir)),
  ]) {
    // Only the root's FINAL component is inspected: a root that is itself a symlink is
    // refused, because realpath would otherwise adopt its target wholesale (a repo-planted
    // `test-results -> $HOME` would authorise the entire home directory). Ancestor aliases
    // such as macOS `/tmp -> /private/tmp` still resolve normally.
    let link: Stats | null = null;
    try {
      link = await lstat(root);
    } catch (error) {
      const code = errnoName(error);
      if (code === 'ENOENT' || code === 'ENOTDIR') continue;
      throw pathRefused(`cannot inspect the upload root ${displayPath(root, input.cwd)}: ${code}`);
    }
    if (link.isSymbolicLink()) {
      throw pathRefused(
        `the upload root ${displayPath(root, input.cwd)} is a symlink`,
        'point the upload at the directory itself, or name its directory with --dir',
      );
    }
    let real: string;
    try {
      real = await realpath(root);
    } catch (error) {
      const code = errnoName(error);
      if (code === 'ENOENT' || code === 'ENOTDIR') continue;
      throw pathRefused(`cannot resolve the upload root ${displayPath(root, input.cwd)}: ${code}`);
    }
    if (!roots.some((entry) => entry.real === real)) {
      roots.push({ shown: displayPath(root, input.cwd), real });
    }
  }

  let realFile: string;
  try {
    realFile = await realpath(path);
  } catch (error) {
    throw pathRefused(`cannot read --file ${displayPath(path, input.cwd)}: ${errnoName(error)}`);
  }

  const root = roots.find((entry) => contains(entry.real, realFile));
  if (root === undefined) {
    // The list is shown relative and bounded, so a refusal never prints an absolute home
    // directory and never grows without limit.
    const allowed =
      roots.length === 0 ? 'none exists' : roots.map((entry) => entry.shown).join(', ');
    throw pathRefused(
      `--file ${displayPath(path, input.cwd)} is outside the allowed directories (${allowed})`,
      'upload a file from test-results/ or playwright-report/, or add its directory with --dir',
    );
  }

  let handle: FileHandle;
  try {
    handle = await open(realFile, fsConstants.O_RDONLY | O_NOFOLLOW | O_NONBLOCK);
  } catch (error) {
    const code = errnoName(error);
    throw pathRefused(
      code === 'ELOOP'
        ? `refusing to follow the symlink ${displayPath(path, input.cwd)}`
        : `cannot read --file ${displayPath(path, input.cwd)}: ${code}`,
    );
  }

  try {
    const stats = await handle.stat();
    if (!stats.isFile()) {
      throw pathRefused(`--file ${displayPath(path, input.cwd)} is not a regular file`);
    }
    const uid = process.platform === 'win32' ? undefined : process.getuid?.();
    if (uid !== undefined && stats.uid !== uid) {
      throw pathRefused(`--file ${displayPath(path, input.cwd)} is owned by another user`);
    }
    // The descriptor is the object that will be read; the path only decides the policy.
    // A swap between the realpath check and this open is refused, not followed.
    const named = await lstat(realFile);
    if (named.dev !== stats.dev || named.ino !== stats.ino) {
      throw pathRefused(`--file ${displayPath(path, input.cwd)} changed while it was opened`);
    }
    if (stats.size === 0) {
      throw pathRefused(`--file ${displayPath(path, input.cwd)} is empty`);
    }
    // Refused here, before the token exchange, so an over-cap file costs no request. Only the
    // kind's static default is known locally (`undefined` = the project row decides), so this
    // mirrors the server's cap and never replaces it.
    const cap = artifactKindMaxBytes[input.kind];
    if (cap !== undefined && stats.size > cap) {
      throw new CliError(
        `--file ${displayPath(path, input.cwd)} is ${stats.size} bytes, over the ${input.kind} cap of ${cap} bytes`,
        {
          code: 'upload_too_large',
          hint: 'the server refuses this kind above the cap; split the artifact or raise the kind policy in the project',
        },
      );
    }
    return { path, realPath: realFile, size: stats.size, dev: stats.dev, ino: stats.ino, handle };
  } catch (error) {
    await handle.close();
    throw error;
  }
}

/**
 * Re-opens the guarded file for one more read pass and proves it is still the same object:
 * `O_NOFOLLOW|O_NONBLOCK` on the resolved path, `fstat`ed, with its `dev`/`ino`/`size`
 * compared against the descriptor the guard pinned. A path swapped for a symlink
 * (`ELOOP`), a different inode (the file was replaced) or a different length is
 * `upload_path_refused` before a byte is read — that is the TOCTOU half of the two-pass
 * upload, and it is never retried because the same path would fail again.
 *
 * The caller owns the returned descriptor and must close it on every path.
 */
export async function reopenUploadFile(file: UploadFile): Promise<FileHandle> {
  let handle: FileHandle;
  try {
    handle = await open(file.realPath, fsConstants.O_RDONLY | O_NOFOLLOW | O_NONBLOCK);
  } catch (error) {
    const code = errnoName(error);
    throw pathRefused(
      code === 'ELOOP'
        ? `refusing to follow a symlink swapped in for --file ${displayPath(file.path, process.cwd())}`
        : `cannot re-open --file ${displayPath(file.path, process.cwd())}: ${code}`,
      'the file must not be replaced, renamed or linked over while it is uploaded',
    );
  }
  try {
    const stats = await handle.stat();
    if (!stats.isFile()) {
      throw pathRefused(`--file ${displayPath(file.path, process.cwd())} is not a regular file`);
    }
    if (stats.dev !== file.dev || stats.ino !== file.ino) {
      throw pathRefused(
        `--file ${displayPath(file.path, process.cwd())} was replaced while it was uploaded`,
        'the file was moved or rewritten under the upload; re-run the command on the finished file',
      );
    }
    if (stats.size !== file.size) {
      throw pathRefused(
        `--file ${displayPath(file.path, process.cwd())} changed size while it was uploaded`,
        'the file was still being written; re-run the command after it is complete',
      );
    }
    return handle;
  } catch (error) {
    await handle.close().catch(() => undefined);
    throw error;
  }
}

/** Closes the descriptor the guard opened; a close failure is not worth failing the run. */
export async function closeUploadFile(file: UploadFile): Promise<void> {
  await file.handle.close().catch(() => undefined);
}

/**
 * True when a `relative()` result leaves the base. `..foo` is a child named `..foo`, not a
 * traversal: only the exact `..` segment and `..` followed by a separator escape.
 */
function escapes(rel: string): boolean {
  return rel === '..' || rel.startsWith(`..${sep}`) || isAbsolute(rel);
}

/** True when `target` is `root` itself or sits under it (both already realpath'd). */
function contains(root: string, target: string): boolean {
  const rel = relative(root, target);
  return rel === '' || !escapes(rel);
}

/**
 * A path for a message: relative to the working directory when it is inside it, otherwise
 * its basename plus an ellipsis, so a refusal never prints a full home directory.
 */
function displayPath(path: string, cwd: string): string {
  const rel = relative(cwd, path);
  if (rel === '') {
    return '.';
  }
  return escapes(rel) ? `…/${path.split(/[\\/]/).pop() ?? path}` : rel;
}

/** The errno code of a filesystem failure, or `unknown` — never the message or a path. */
function errnoName(error: unknown): string {
  const code = (error as NodeJS.ErrnoException | undefined)?.code;
  return typeof code === 'string' && /^[A-Z0-9_]{1,32}$/.test(code) ? code : 'unknown';
}
