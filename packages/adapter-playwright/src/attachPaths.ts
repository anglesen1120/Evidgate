// 04.3.3-T2.1: the allow-listed directory guard every attachment path passes before an
// upload (AC6; plan §8 "04 §5 traversal row" → `paths.test.ts`).
//
// Why this exists: an attachment path arrives from the test process, and a trace or a
// screenshot can carry personal data (DECISIONS §7). The rule is deny-by-default and the same
// one `packages/cli/src/uploadPaths.ts` applies to `--file`: the file's REAL path must sit
// under one of the roots — the Playwright output directories plus `test-results` plus any
// explicitly allowed directory — with `realpath` taken on both sides, so a symlink planted
// inside a root can never be used to reach a file outside it, and `..` traversal is resolved
// away before the comparison. The two guards must agree on the edge cases, so the containment
// test is the CLI's: `..foo` is a legal child name, only the exact `..` segment and `..`
// followed by a separator leave the root.
//
// This module never reads a byte and never opens the file: it resolves, checks the type and
// reports the size, and the upload streams from the same real path (04.2.4-T1). A path that
// does not exist yet is `attachment_unreadable`; a directory, FIFO, device or a symlink that
// appeared after the resolution is refused, because a FIFO would park the reporter
// indefinitely and a device is never an attachment.
//
// Messages are filtered reasons plus the file's BASE name — never the full path, never the
// root list, never an errno message (which on some systems echoes the path).
import { type Stats } from 'node:fs';
import { lstat, realpath } from 'node:fs/promises';
import { isAbsolute, join, relative, resolve, sep } from 'node:path';

/** The most UTF-16 units of a base name a refusal message carries. */
const NAME_MAX = 120;

/** Control and format characters, escaped rather than removed (`status.ts` `printable`). */
const UNSAFE_CHARS = /[\p{Cc}\p{Cf}]/gu;

/** The escapes a reader can undo; every other unsafe character becomes `\u{<hex>}`. */
const NAMED_ESCAPES: Readonly<Record<string, string>> = {
  '\n': '\\n',
  '\r': '\\r',
  '\t': '\\t',
};

/** `C:\x` / `c:/x`: a Windows drive-letter form, refused on every other platform. */
const WINDOWS_DRIVE = /^[A-Za-z]:[\\/]/u;

/** `\\host\share`: a Windows UNC form, refused on every other platform. */
const WINDOWS_UNC = /^\\\\/u;

export interface PathGuardOptions {
  /** The directories an attachment may be read from; relative entries resolve against `cwd`. */
  readonly roots: readonly string[];
  /** The process's working directory: relative attachment paths and relative roots use it. */
  readonly cwd: string;
  /** The platform whose path separators are refused; defaults to `process.platform`. */
  readonly platform?: NodeJS.Platform | undefined;
}

/**
 * The guard's verdict. `realPath` is the resolved path the upload must read (the nominal
 * path is never trusted), and `size` is the regular file's size for the per-kind cap.
 */
export type PathVerdict =
  | { readonly ok: true; readonly realPath: string; readonly size: number }
  | {
      readonly ok: false;
      readonly code: 'attachment_path_refused' | 'attachment_unreadable';
      readonly message: string;
    };

/** The guard `attachPaths.ts` exposes: one async check per attachment path. */
export interface PathGuard {
  check(rawPath: string): Promise<PathVerdict>;
}

/**
 * Builds the guard. `check` resolves the path, refuses a Windows drive/UNC form on a
 * non-Windows platform, refuses a path outside every root, and refuses anything that is not a
 * regular file. Roots that do not exist are ignored rather than failing every check: the
 * Playwright output directory is created by the run, and a configured directory that the run
 * never wrote must not break the run.
 */
export function createPathGuard(options: PathGuardOptions): PathGuard {
  const cwd = resolve(options.cwd);
  const platform = options.platform ?? process.platform;

  /** The real paths of the roots that exist right now; resolved per check, never cached. */
  async function realRoots(): Promise<string[]> {
    const candidates = options.roots.map((root) => resolve(cwd, root));
    const resolvedRoots = await Promise.all(
      candidates.map(async (candidate) => {
        try {
          return await realpath(candidate);
        } catch {
          return undefined;
        }
      }),
    );
    return resolvedRoots.filter((root): root is string => root !== undefined);
  }

  return {
    async check(rawPath: string): Promise<PathVerdict> {
      const text = typeof rawPath === 'string' ? rawPath : '';
      const name = displayName(text);
      if (text.trim().length === 0) {
        return refused(name, 'the path is empty');
      }
      if (text.includes('\0')) {
        return refused(name, 'the path contains a NUL character');
      }
      if (platform !== 'win32') {
        if (WINDOWS_DRIVE.test(text)) {
          return refused(name, 'a Windows drive-letter path is not allowed on this platform');
        }
        if (WINDOWS_UNC.test(text)) {
          return refused(name, 'a Windows UNC path is not allowed on this platform');
        }
      }
      const absolute = isAbsolute(text) ? text : resolve(cwd, text);
      let realPath: string;
      try {
        realPath = await realpath(absolute);
      } catch (error) {
        return unreadable(name, `the file cannot be read (${errnoName(error)})`);
      }
      const roots = await realRoots();
      if (!roots.some((root) => contains(root, realPath))) {
        return refused(name, 'the path is outside the allowed directories');
      }
      // `lstat`, not `stat`: `realpath` has resolved every component, so the only symlink a
      // `lstat` can still see is one created in between — and that is exactly the one that must
      // be refused rather than followed (the `sidecar.ts` precedent; the upload re-checks the
      // descriptor it actually opened, 04.2.4-T1).
      let stats: Stats;
      try {
        stats = await lstat(realPath);
      } catch (error) {
        return unreadable(name, `the file cannot be inspected (${errnoName(error)})`);
      }
      if (!stats.isFile()) {
        return refused(name, 'not a regular file');
      }
      return { ok: true, realPath, size: stats.size };
    },
  };
}

/**
 * The roots an attachment may come from (AC6): every Playwright `outputDir`, the default
 * `<rootDir>/test-results`, and every explicitly allowed directory. Each entry is resolved
 * against `rootDir` — Playwright resolves config paths against it, and the reporter's own
 * `dirs` option is documented the same way — absolute entries are kept as they are, and the
 * result is de-duplicated in that order. It is absolute by construction.
 */
export function defaultAttachmentRoots(input: {
  readonly outputDirs: readonly string[];
  readonly rootDir: string;
  readonly extraDirs: readonly string[];
}): string[] {
  const rootDir = resolve(input.rootDir);
  const roots: string[] = [];
  const seen = new Set<string>();
  for (const dir of [...input.outputDirs, join(rootDir, 'test-results'), ...input.extraDirs]) {
    if (typeof dir !== 'string' || dir.trim().length === 0) continue;
    const absolute = resolve(rootDir, dir);
    if (seen.has(absolute)) continue;
    seen.add(absolute);
    roots.push(absolute);
  }
  return roots;
}

/** A refusal before the file is read: the path itself is not allowed (AC6). */
function refused(name: string, reason: string): PathVerdict {
  return { ok: false, code: 'attachment_path_refused', message: `attachment '${name}': ${reason}` };
}

/** The path is allowed but the file cannot be used; the errno code is safe to echo. */
function unreadable(name: string, reason: string): PathVerdict {
  return { ok: false, code: 'attachment_unreadable', message: `attachment '${name}': ${reason}` };
}

/**
 * True when a `relative()` result leaves the base — the same rule as
 * `packages/cli/src/uploadPaths.ts`: `..foo` is a child named `..foo`, not a traversal.
 */
function escapes(rel: string): boolean {
  return rel === '..' || rel.startsWith(`..${sep}`) || isAbsolute(rel);
}

/** True when `target` is `root` itself or sits under it (both already realpath'd). */
function contains(root: string, target: string): boolean {
  const rel = relative(root, target);
  return rel === '' || !escapes(rel);
}

/** The file's base name only, escaped and capped: a message never prints a directory. */
function displayName(rawPath: string): string {
  const parts = rawPath.split(/[\\/]/u);
  const shown = escapeControls(parts[parts.length - 1] ?? '');
  return cut(shown.length > 0 ? shown : 'unnamed', NAME_MAX);
}

/**
 * Every control or format character as visible text: the named escapes first, `\u{<hex>}`
 * otherwise, so a newline in a file name cannot split a log line.
 */
function escapeControls(value: string): string {
  return value.replace(
    UNSAFE_CHARS,
    (char: string) => NAMED_ESCAPES[char] ?? `\\u{${(char.codePointAt(0) ?? 0).toString(16)}}`,
  );
}

/** At most `max` UTF-16 units, never ending on a lone high surrogate. */
function cut(value: string, max: number): string {
  if (value.length <= max) return value;
  const last = value.charCodeAt(max - 1);
  return value.slice(0, last >= 0xd800 && last <= 0xdbff ? max - 1 : max);
}

/** The errno code of a filesystem failure, or `unknown` — never the message or a path. */
function errnoName(error: unknown): string {
  const code = (error as NodeJS.ErrnoException | undefined)?.code;
  return typeof code === 'string' && /^[A-Z0-9_]{1,32}$/u.test(code) ? code : 'unknown';
}
