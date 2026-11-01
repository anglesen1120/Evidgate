// The per-CI-job `ci_producer` token cache: one file per (api_url, project, source credential)
// under `cacheDir`, mode `0600` in a `0700` directory owned by the current user, written
// atomically (temp file + rename, which replaces a planted symlink instead of following it).
// The file name carries a digest of the destination and of the credential the exchange presents,
// so a rotated credential cannot hand back a token minted from the old one and a purge without
// a credential can still find every entry for the project. The credential itself never leaves
// memory. The read path distrusts an untrusted file — a foreign owner, a group/world-readable
// mode, a non-regular file or unparseable content reads as absent, never as an error — and
// opens it `O_NOFOLLOW`; both paths refuse a symlinked, foreign-owned or group/world-writable
// cache directory, and a write drops the destination's entries bound to another credential.
// A write that cannot complete — an unwritable directory or filesystem, or a directory refused
// as unsafe — warns once through the installed writer and leaves the token in memory; it never
// fails the command, because the cache only saves an exchange (contracts/cli.md §7).
// Those owner, mode and `O_NOFOLLOW` guards are POSIX only; on win32 only a regular-file check
// applies.
import { createHash, randomUUID } from 'node:crypto';
import { constants, type Stats } from 'node:fs';
import {
  chmod,
  lstat,
  mkdir,
  open,
  readdir,
  rename,
  rm,
  unlink,
  type FileHandle,
} from 'node:fs/promises';
import { join, resolve } from 'node:path';

import { resolveTokenSource, type ResolvedConfig } from '../config.js';
import { CliError, escapeControlCharacters } from '../errors.js';
import { registerSecret } from '../redact.js';

/** Refresh the token when it expires within this margin, so a request that starts now does
 * not begin already expired. */
export const refreshMarginMs = 60_000;

/** The longest a server token can live (60 min) plus 5 min of clock skew. */
export const maxTokenLifetimeMs = 65 * 60_000;

// `O_NOFOLLOW` is POSIX-only; win32 falls back to the `lstat` check in `readCachedToken`.
// `O_NONBLOCK` keeps a FIFO planted at the cache path from blocking the open.
const openReadFlags =
  constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0) | (constants.O_NONBLOCK ?? 0);

export interface CachedToken {
  readonly token: string;
  readonly expiresAt: string;
}

/**
 * The fields that key the cache file; `ResolvedConfig` satisfies it structurally. The source
 * fields are optional so a destination-only caller still works, but a config with a credential
 * always keys that credential's own entry.
 */
export type TokenCacheConfig = Pick<ResolvedConfig, 'apiUrl' | 'project' | 'cacheDir'> & {
  readonly oidcToken?: string | undefined;
  readonly actionsIdTokenRequestUrl?: string | undefined;
  readonly actionsIdTokenRequestToken?: string | undefined;
  readonly ingestToken?: string | undefined;
  readonly devToken?: string | undefined;
};

/**
 * `ci-token.<destination digest>.<credential digest>.json` in `cacheDir`. The destination half
 * lets a purge that has no credential (`auth logout` after the source is gone) find every entry
 * for the project by name alone, without parsing or trusting file contents.
 */
export function tokenCachePath(config: TokenCacheConfig): string {
  const binding = credentialBinding(config);
  const credentialKey = hashKey([cacheKeyVersion, binding.kind, binding.credential]);
  return join(config.cacheDir, `ci-token.${destinationKey(config)}.${credentialKey}.json`);
}

// `v2` names the binding scheme. A `v1` file has no credential in its name, so a `v2` read
// opens a different path and can never trust it; the value is only ever fed to the digest.
const cacheKeyVersion = 'v2';

function destinationKey(config: TokenCacheConfig): string {
  return hashKey([cacheKeyVersion, config.apiUrl, config.project]);
}

function hashKey(fields: readonly string[]): string {
  // JSON frames the fields unambiguously: a credential containing a separator cannot collide
  // with another field, and only the digest leaves this function — never the credential.
  return createHash('sha256').update(JSON.stringify(fields)).digest('hex');
}

/** The destination-only name the previous scheme wrote; never read, only purged on a delete. */
function legacyTokenCachePath(config: TokenCacheConfig): string {
  const key = createHash('sha256').update(`${config.apiUrl}\n${config.project}`).digest('hex');
  return join(config.cacheDir, `ci-token.${key}.json`);
}

/** The binding half of a cache file name, after its destination prefix. */
const boundNameSuffix = /^[0-9a-f]{64}\.json$/;
/**
 * A temp file `writeCachedToken` left behind. It is not bound to a destination in its name, so
 * only the exact shape that writer mints is matched, and it goes on any purge because a crashed
 * write may still be holding a whole token at `0600` for a project that is being signed out.
 */
const staleTempName = /^\.ci-token\.[0-9a-f-]{36}\.tmp$/;

/** The exchange source a config resolves to, and the credential value that exchange presents. */
interface CredentialBinding {
  readonly kind: string;
  readonly credential: string;
}

/**
 * Binds the entry to the credential the exchange presents. A dev token keys the ingest kind, so
 * a purge with the raw dev config deletes what the read wrote; an actions ID token is fetched
 * per run, so that source binds to its request URL (stable within a job, distinct per run), and
 * its per-run request token is deliberately not part of the key.
 */
function credentialBinding(config: TokenCacheConfig): CredentialBinding {
  const source = resolveTokenSource({
    oidcToken: config.oidcToken,
    actionsIdTokenRequestUrl: config.actionsIdTokenRequestUrl,
    actionsIdTokenRequestToken: config.actionsIdTokenRequestToken,
    ingestToken: config.ingestToken,
    devToken: config.devToken,
  });
  switch (source) {
    case 'oidc':
      return { kind: 'oidc', credential: config.oidcToken ?? '' };
    case 'actions-id-token':
      return { kind: 'actions-id-token', credential: config.actionsIdTokenRequestUrl ?? '' };
    case 'ingest':
      return { kind: 'ingest', credential: config.ingestToken ?? '' };
    case 'dev':
      return { kind: 'ingest', credential: config.devToken ?? '' };
    default:
      return { kind: 'none', credential: '' };
  }
}

/**
 * Reads the cached token, or `undefined` when there is none or it cannot be trusted. Only the
 * file bound to this config's credential is opened: an entry written under another credential,
 * or under the destination-only name of the previous scheme, is not there for this read.
 * Never throws: a missing file, a non-regular file, a symlink, a group/world-readable or
 * foreign-owned file, a cache directory owned by another user and unparseable content all
 * read as absent. The file is inspected through the one handle it is opened with, and a
 * trusted token is registered for redaction.
 */
export async function readCachedToken(config: TokenCacheConfig): Promise<CachedToken | undefined> {
  const path = tokenCachePath(config);
  let handle: FileHandle | undefined;
  try {
    if (!(await cacheDirTrusted(config.cacheDir))) {
      return undefined;
    }
    // win32 has no `O_NOFOLLOW`, so reject a symlink with `lstat` before opening.
    if (process.platform === 'win32') {
      const stats = await lstat(path);
      if (!stats.isFile()) {
        return undefined;
      }
    }
    handle = await open(path, openReadFlags);
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

/** A real directory, owned by this user and not group/world-writable; POSIX only. */
function dirIsSafe(stats: Stats): boolean {
  if (!stats.isDirectory() || (stats.mode & 0o022) !== 0) {
    return false;
  }
  return typeof process.getuid !== 'function' || stats.uid === process.getuid();
}

// `resolve` drops a trailing separator, which would otherwise make `lstat` follow a symlink.
async function cacheDirTrusted(cacheDir: string): Promise<boolean> {
  if (process.platform === 'win32') {
    return true;
  }
  return dirIsSafe(await lstat(resolve(cacheDir)));
}

function isBoundEntry(destination: string, name: string): boolean {
  const prefix = `ci-token.${destination}.`;
  return name.startsWith(prefix) && boundNameSuffix.test(name.slice(prefix.length));
}

/**
 * After a successful write, drops this destination's entries minted under another credential.
 * Only names of the bound-file shape for this destination are considered, `rm` unlinks a
 * symlink without following it, and a failure never fails the write that just succeeded.
 * Two jobs sharing one cache dir and destination under different credentials evict each
 * other's entry; that costs an extra exchange, so give such jobs separate cache dirs.
 */
async function purgeRotatedEntries(config: TokenCacheConfig, keep: string): Promise<void> {
  const destination = destinationKey(config);
  let names: string[];
  try {
    names = await readdir(config.cacheDir);
  } catch {
    return;
  }
  for (const name of names) {
    const path = join(config.cacheDir, name);
    if (isBoundEntry(destination, name) && path !== keep) {
      // The fresh token is already in place; a stale sibling goes on the next write or logout.
      await rm(path, { force: true }).catch(() => undefined);
    }
  }
}

/**
 * The writer a failed write warns to, and the once-per-invocation flag with it. `main` installs
 * the redacting stderr writer at entry (and the install clears the flag), so a failed write
 * reaches the operator once and can never carry a token; a spec installs its own collector and
 * clears it after the case. A direct caller that installs nothing warns nowhere.
 */
let warnWriter: ((text: string) => void) | undefined;
let warned = false;

export function setCacheWarningWriter(write: ((text: string) => void) | undefined): void {
  warnWriter = write;
  warned = false;
}

/** The errno code of a failed filesystem call, or the generic phrase when it carries none. */
function failureReason(error: unknown): string {
  const code =
    typeof error === 'object' && error !== null && 'code' in error ? error.code : undefined;
  return typeof code === 'string' && code.length > 0 ? code : 'write failed';
}

/**
 * One warning, never a token and never a path below the cache directory: the entry's digest
 * names and the token itself are what must not reach a log.
 */
function warnCacheUnwritable(cacheDir: string, reason: string): void {
  if (warnWriter === undefined || warned) {
    return;
  }
  warned = true;
  warnWriter(
    `evidgate: warning: could not write the token cache in ${escapeControlCharacters(cacheDir)} ` +
      `(${reason}); this run keeps its token in memory\n`,
  );
}

/**
 * Writes the cache file atomically: a `0600` temp file in the same directory (opened
 * `wx`, then `chmod`ed so the umask cannot widen it) is renamed over the target, which
 * replaces a symlink rather than following it. The directory must be a real directory
 * owned by the current user and not group/world-writable, else nothing is written at all;
 * a group/world-readable mode is tightened to `0700`. Any failure warns once and returns,
 * so the caller keeps the token it already holds.
 */
export async function writeCachedToken(
  config: TokenCacheConfig,
  value: CachedToken,
): Promise<void> {
  const path = tokenCachePath(config);
  const temp = join(config.cacheDir, `.ci-token.${randomUUID()}.tmp`);
  try {
    await mkdir(config.cacheDir, { recursive: true, mode: 0o700 });
    // Refuse a symlinked, foreign-owned or writable directory; tighten a readable one.
    if (process.platform !== 'win32') {
      const dirStats = await lstat(resolve(config.cacheDir));
      if (!dirIsSafe(dirStats)) {
        warnCacheUnwritable(config.cacheDir, 'unsafe directory');
        return;
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
    await purgeRotatedEntries(config, path);
  } catch (error) {
    await unlink(temp).catch(() => undefined);
    warnCacheUnwritable(config.cacheDir, failureReason(error));
  }
}

/**
 * Deletes every cached token for this destination, whatever credential minted it — including a
 * purge with no credential, which is what `auth logout` runs once the source is gone — plus the
 * destination-only file of the previous scheme and any temp file a crashed write left holding a
 * token. The destination digest in the name scopes it, so another project's files are untouched
 * and only the temp shape that writer mints is claimed; nothing is read to decide.
 */
export async function deleteCachedToken(config: TokenCacheConfig): Promise<void> {
  const destination = destinationKey(config);
  try {
    const names = await readdir(config.cacheDir).catch((error: unknown) => {
      const missing =
        typeof error === 'object' && error !== null && 'code' in error && error.code === 'ENOENT';
      if (missing) {
        return [];
      }
      throw error;
    });
    for (const name of names) {
      if (isBoundEntry(destination, name) || staleTempName.test(name)) {
        await rm(join(config.cacheDir, name), { force: true });
      }
    }
    await rm(legacyTokenCachePath(config), { force: true });
  } catch {
    throw new CliError('could not delete the token cache', { code: 'token_cache_error' });
  }
}

/**
 * True while the token is more than `refreshMarginMs` from expiry and no further than
 * `maxTokenLifetimeMs` away. A far-future `expires_at` is untrusted: the server never issues
 * a token that outlives `maxTokenLifetimeMs`.
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
