// 04.2.1-T1.2: the per-CI-job `ci_producer` token cache (04.2.1 §5
// `auth/token-cache.ts`, AC3). One file per (api_url, project) under `cacheDir`, mode
// `0600` in a `0700` directory owned by the current user, written atomically (temp file
// + rename). A file that is not a regular file, is group/world-readable, is owned by
// another user, or does not parse is treated as absent — never trusted, never thrown on
// (04.2.1 §8 T5). The file is opened `O_NOFOLLOW` so a symlink is never followed (S4),
// and a cache directory that is a symlink or is owned by another user is refused (D1).
import { createHash, randomUUID } from 'node:crypto';
import { constants } from 'node:fs';
import { chmod, lstat, mkdir, open, rename, rm, unlink, type FileHandle } from 'node:fs/promises';
import { join } from 'node:path';

import type { ResolvedConfig } from '../config.js';
import { CliError } from '../errors.js';
import { registerSecret } from '../redact.js';

/** Refresh the token when it expires within this margin (04.2.1 §5, AC3). */
export const refreshMarginMs = 60_000;

/** The longest a server token can live (60 min) plus 5 min of clock skew (S3). */
export const maxTokenLifetimeMs = 65 * 60_000;

// `O_NOFOLLOW` is POSIX-only; win32 falls back to the `lstat` check in `readCachedToken`.
const noFollowFlag = constants.O_NOFOLLOW ?? 0;

export interface CachedToken {
  readonly token: string;
  readonly expiresAt: string;
}

/** The fields that key the cache file; `ResolvedConfig` satisfies it structurally. */
export type TokenCacheConfig = Pick<ResolvedConfig, 'apiUrl' | 'project' | 'cacheDir'>;

/** `ci-token.<sha256 hex of apiUrl + "\n" + project>.json` in `cacheDir` (04.2.1 §5). */
export function tokenCachePath(config: TokenCacheConfig): string {
  const key = createHash('sha256').update(`${config.apiUrl}\n${config.project}`).digest('hex');
  return join(config.cacheDir, `ci-token.${key}.json`);
}

/**
 * Reads the cached token, or `undefined` when there is none or it cannot be trusted.
 * Never throws: a missing file, a non-regular file, a symlink, a group/world-readable
 * or foreign-owned file (POSIX), a cache directory owned by another user (D1) and
 * unparseable content all read as absent. The file is opened `O_NOFOLLOW` and inspected
 * through the one handle (S4). A trusted token is registered for redaction (T78).
 */
export async function readCachedToken(config: TokenCacheConfig): Promise<CachedToken | undefined> {
  const path = tokenCachePath(config);
  let handle: FileHandle | undefined;
  try {
    // D1: a cache directory owned by another user is not trusted.
    if (process.platform !== 'win32' && typeof process.getuid === 'function') {
      const dirStats = await lstat(config.cacheDir);
      if (dirStats.uid !== process.getuid()) {
        return undefined;
      }
    }
    // S4: win32 has no `O_NOFOLLOW`, so reject a symlink with `lstat` before opening.
    if (process.platform === 'win32') {
      const stats = await lstat(path);
      if (!stats.isFile()) {
        return undefined;
      }
    }
    handle = await open(path, constants.O_RDONLY | noFollowFlag);
    const stats = await handle.stat();
    if (!stats.isFile()) {
      return undefined;
    }
    if (process.platform !== 'win32') {
      if ((stats.mode & 0o077) !== 0) {
        return undefined;
      }
      if (typeof process.getuid === 'function' && stats.uid !== process.getuid()) {
        return undefined;
      }
    }
    const value = parseCachedToken(JSON.parse(await handle.readFile('utf8')));
    if (value === undefined) {
      return undefined;
    }
    registerSecret(value.token);
    return value;
  } catch {
    return undefined;
  } finally {
    if (handle !== undefined) {
      await handle.close().catch(() => undefined);
    }
  }
}

/**
 * Writes the cache file atomically: a `0600` temp file in the same directory (opened
 * `wx`, then `chmod`ed so the umask cannot widen it) is renamed over the target, which
 * replaces a symlink rather than following it. The directory must be a real directory
 * owned by the current user; a symlink or foreign-owned directory is refused and a
 * loose mode is tightened to `0700` (D1). Any failure is `token_cache_error` and the
 * temp file is removed best-effort; the message never carries a token value.
 */
export async function writeCachedToken(
  config: TokenCacheConfig,
  value: CachedToken,
): Promise<void> {
  const path = tokenCachePath(config);
  const temp = join(config.cacheDir, `.ci-token.${randomUUID()}.tmp`);
  try {
    await mkdir(config.cacheDir, { recursive: true, mode: 0o700 });
    // D1: refuse a symlinked or foreign-owned directory, and tighten a loose mode.
    if (process.platform !== 'win32') {
      const dirStats = await lstat(config.cacheDir);
      if (
        !dirStats.isDirectory() ||
        (typeof process.getuid === 'function' && dirStats.uid !== process.getuid())
      ) {
        throw new CliError('could not write the token cache', { code: 'token_cache_error' });
      }
      if ((dirStats.mode & 0o077) !== 0) {
        await chmod(config.cacheDir, 0o700);
      }
    }
    const handle = await open(temp, 'wx', 0o600);
    try {
      await handle.chmod(0o600);
      await handle.writeFile(JSON.stringify({ token: value.token, expires_at: value.expiresAt }));
    } finally {
      await handle.close();
    }
    await rename(temp, path);
  } catch {
    await unlink(temp).catch(() => undefined);
    throw new CliError('could not write the token cache', { code: 'token_cache_error' });
  }
}

/** Deletes the cache file; an absent file is not an error (04.2.1 §8 retention). */
export async function deleteCachedToken(config: TokenCacheConfig): Promise<void> {
  try {
    await rm(tokenCachePath(config), { force: true });
  } catch {
    throw new CliError('could not delete the token cache', { code: 'token_cache_error' });
  }
}

/**
 * True while the token is more than `refreshMarginMs` from expiry and no further than
 * `maxTokenLifetimeMs` away (04.2.1 §5, AC3). A far-future `expires_at` is untrusted:
 * the server never issues a token that outlives `maxTokenLifetimeMs` (S3).
 */
export function isFresh(value: CachedToken, nowMs: number): boolean {
  const remainingMs = Date.parse(value.expiresAt) - nowMs;
  return remainingMs > refreshMarginMs && remainingMs <= maxTokenLifetimeMs;
}

function parseCachedToken(parsed: unknown): CachedToken | undefined {
  if (typeof parsed !== 'object' || parsed === null) {
    return undefined;
  }
  const record = parsed as Record<string, unknown>;
  const token = record['token'];
  const expiresAt = record['expires_at'];
  if (typeof token !== 'string' || token.length === 0 || /\s/.test(token)) {
    return undefined;
  }
  if (typeof expiresAt !== 'string' || !Number.isFinite(Date.parse(expiresAt))) {
    return undefined;
  }
  return { token, expiresAt };
}
