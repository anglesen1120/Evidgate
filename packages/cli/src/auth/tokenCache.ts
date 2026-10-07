// 04.2.1-T1.2: the per-CI-job `ci_producer` token cache (04.2.1 §5
// `auth/token-cache.ts`, AC3). One file per (api_url, project) under `cacheDir`, mode
// `0600` in a `0700` directory, written atomically (temp file + rename). A file that is
// not a regular file, is group/world-readable, is owned by another user, or does not
// parse is treated as absent — never trusted, never thrown on (04.2.1 §8 T5).
import { createHash, randomUUID } from 'node:crypto';
import { lstat, mkdir, open, readFile, rename, rm, unlink } from 'node:fs/promises';
import { join } from 'node:path';

import type { ResolvedConfig } from '../config.js';
import { CliError } from '../errors.js';
import { registerSecret } from '../redact.js';

/** Refresh the token when it expires within this margin (04.2.1 §5, AC3). */
export const refreshMarginMs = 60_000;

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
 * Never throws: a missing file, a non-regular file, a group/world-readable or
 * foreign-owned file (POSIX) and unparseable content all read as absent. A trusted
 * token is registered for redaction before it is returned (T78).
 */
export async function readCachedToken(config: TokenCacheConfig): Promise<CachedToken | undefined> {
  const path = tokenCachePath(config);
  try {
    const stats = await lstat(path);
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
    const value = parseCachedToken(JSON.parse(await readFile(path, 'utf8')));
    if (value === undefined) {
      return undefined;
    }
    registerSecret(value.token);
    return value;
  } catch {
    return undefined;
  }
}

/**
 * Writes the cache file atomically: a `0600` temp file in the same directory (opened
 * `wx`, then `chmod`ed so the umask cannot widen it) is renamed over the target, which
 * replaces a symlink rather than following it. Any failure is `token_cache_error` and
 * the temp file is removed best-effort; the message never carries a token value.
 */
export async function writeCachedToken(
  config: TokenCacheConfig,
  value: CachedToken,
): Promise<void> {
  const path = tokenCachePath(config);
  const temp = join(config.cacheDir, `.ci-token.${randomUUID()}.tmp`);
  try {
    await mkdir(config.cacheDir, { recursive: true, mode: 0o700 });
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

/** True while the token is more than `refreshMarginMs` from expiry (04.2.1 §5, AC3). */
export function isFresh(value: CachedToken, nowMs: number): boolean {
  return Date.parse(value.expiresAt) - nowMs > refreshMarginMs;
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
