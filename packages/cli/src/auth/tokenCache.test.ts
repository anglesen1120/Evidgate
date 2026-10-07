import { execFileSync } from 'node:child_process';
import {
  chmod,
  lstat,
  mkdir,
  mkdtemp,
  readdir,
  readFile,
  rm,
  stat,
  symlink,
  writeFile,
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { redact, redacted, resetSecrets } from '../redact.js';

import {
  deleteCachedToken,
  isFresh,
  maxTokenLifetimeMs,
  readCachedToken,
  refreshMarginMs,
  tokenCachePath,
  writeCachedToken,
  type TokenCacheConfig,
} from './tokenCache.js';

// The mode/owner checks are POSIX-only (04.2.1 §8 T5); on win32 they are skipped.
const posix = process.platform !== 'win32';

const validBody = '{"token":"egt_secret","expires_at":"2030-01-01T00:00:00.000Z"}';

function cacheConfig(
  cacheDir: string,
  apiUrl = 'https://api.evidgate.test',
  project = 'proj-1',
): TokenCacheConfig {
  return { apiUrl, project, cacheDir };
}

// 04.2.1-T1.2 / AC3 / §8 T5: the `0600` cache file — atomic write, keyed by
// api_url+project, and every untrusted file (wrong mode, symlink, corrupt) ignored.
describe('tokenCache', () => {
  let root: string;
  let cacheDir: string;

  beforeEach(async () => {
    resetSecrets();
    root = await mkdtemp(join(tmpdir(), 'evidgate-cache-'));
    cacheDir = join(root, 'evidgate');
  });

  afterEach(async () => {
    await rm(root, { recursive: true, force: true });
  });

  it('cache_file_mode_0600', async () => {
    const config = cacheConfig(cacheDir);
    await writeCachedToken(config, { token: 'egt_secret', expiresAt: '2030-01-01T00:00:00.000Z' });

    const path = tokenCachePath(config);
    // Content is exactly `{token, expires_at}` — no extra fields, no plaintext ingest token.
    expect(await readFile(path, 'utf8')).toBe(validBody);
    if (posix) {
      expect((await stat(path)).mode & 0o777).toBe(0o600);
      expect((await stat(cacheDir)).mode & 0o777).toBe(0o700);
    }
  });

  it('reads back a written token and registers it for redaction', async () => {
    const config = cacheConfig(cacheDir);
    await writeCachedToken(config, { token: 'egt_secret', expiresAt: '2030-01-01T00:00:00.000Z' });
    resetSecrets();

    await expect(readCachedToken(config)).resolves.toEqual({
      token: 'egt_secret',
      expiresAt: '2030-01-01T00:00:00.000Z',
    });
    expect(redact('bearer egt_secret')).toBe(`bearer ${redacted}`);
  });

  it('keys the file by api_url and project', () => {
    const base = tokenCachePath(cacheConfig(cacheDir, 'https://api.evidgate.test', 'proj-1'));
    const otherProject = tokenCachePath(
      cacheConfig(cacheDir, 'https://api.evidgate.test', 'proj-2'),
    );
    const otherApi = tokenCachePath(cacheConfig(cacheDir, 'https://other.evidgate.test', 'proj-1'));

    expect(base).not.toBe(otherProject);
    expect(base).not.toBe(otherApi);
    expect(base.startsWith(join(cacheDir, 'ci-token.'))).toBe(true);
    expect(base.endsWith('.json')).toBe(true);
  });

  it.skipIf(!posix)('ignores a group- or world-readable file', async () => {
    const config = cacheConfig(cacheDir);
    await writeCachedToken(config, { token: 'egt_secret', expiresAt: '2030-01-01T00:00:00.000Z' });
    await chmod(tokenCachePath(config), 0o644);

    await expect(readCachedToken(config)).resolves.toBeUndefined();
  });

  it.skipIf(!posix)('ignores a symlink', async () => {
    const target = join(root, 'real.json');
    await writeFile(target, validBody, { mode: 0o600 });
    const config = cacheConfig(cacheDir);
    await mkdir(cacheDir, { recursive: true, mode: 0o700 });
    await symlink(target, tokenCachePath(config));

    await expect(readCachedToken(config)).resolves.toBeUndefined();
  });

  it.skipIf(!posix)('ignores a FIFO at the cache path without blocking', async () => {
    const config = cacheConfig(cacheDir);
    await mkdir(cacheDir, { recursive: true, mode: 0o700 });
    execFileSync('mkfifo', ['-m', '600', tokenCachePath(config)]);

    await expect(readCachedToken(config)).resolves.toBeUndefined();
  });

  // D1: a cache directory that is a symlink is refused, and nothing is written through it.
  it.skipIf(!posix)('refuses to write when the cache dir is a symlink', async () => {
    const realDir = join(root, 'real-dir');
    await mkdir(realDir, { mode: 0o700 });
    await symlink(realDir, cacheDir);

    await expect(
      writeCachedToken(cacheConfig(cacheDir), {
        token: 'egt_secret',
        expiresAt: '2030-01-01T00:00:00.000Z',
      }),
    ).rejects.toMatchObject({ code: 'token_cache_error' });
    expect(await readdir(realDir)).toEqual([]);
  });

  // D1: a loose pre-existing directory is tightened to 0700 on write.
  it.skipIf(!posix)('tightens a pre-existing 0755 cache dir to 0700', async () => {
    const config = cacheConfig(cacheDir);
    await mkdir(cacheDir, { recursive: true });
    await chmod(cacheDir, 0o755);

    await writeCachedToken(config, { token: 'egt_secret', expiresAt: '2030-01-01T00:00:00.000Z' });

    expect((await stat(cacheDir)).mode & 0o777).toBe(0o700);
  });

  // C5b: a failed write is `token_cache_error` and leaves no temp file behind.
  it('fails with token_cache_error when the cache dir path is a file', async () => {
    const config = cacheConfig(cacheDir);
    await writeFile(cacheDir, 'not a directory', { mode: 0o600 });

    await expect(
      writeCachedToken(config, { token: 'egt_secret', expiresAt: '2030-01-01T00:00:00.000Z' }),
    ).rejects.toMatchObject({ code: 'token_cache_error' });
  });

  it('fails with token_cache_error and removes the temp file when the target is a directory', async () => {
    const config = cacheConfig(cacheDir);
    await mkdir(cacheDir, { recursive: true, mode: 0o700 });
    await mkdir(tokenCachePath(config));

    await expect(
      writeCachedToken(config, { token: 'egt_secret', expiresAt: '2030-01-01T00:00:00.000Z' }),
    ).rejects.toMatchObject({ code: 'token_cache_error' });

    expect((await readdir(cacheDir)).filter((name) => name.endsWith('.tmp'))).toEqual([]);
  });

  // C5b: a pre-existing symlink at the target is replaced by a 0600 file, its target intact.
  it.skipIf(!posix)(
    'replaces a symlink at the target path without touching its target',
    async () => {
      const target = join(root, 'real.json');
      await writeFile(target, 'untouched', { mode: 0o600 });
      const config = cacheConfig(cacheDir);
      await mkdir(cacheDir, { recursive: true, mode: 0o700 });
      await symlink(target, tokenCachePath(config));

      await writeCachedToken(config, {
        token: 'egt_secret',
        expiresAt: '2030-01-01T00:00:00.000Z',
      });

      const path = tokenCachePath(config);
      expect((await lstat(path)).isFile()).toBe(true);
      expect((await stat(path)).mode & 0o777).toBe(0o600);
      expect(await readFile(target, 'utf8')).toBe('untouched');
    },
  );

  it('ignores corrupt or incomplete JSON', async () => {
    const config = cacheConfig(cacheDir);
    const path = tokenCachePath(config);
    await mkdir(cacheDir, { recursive: true, mode: 0o700 });
    for (const body of [
      '{not json',
      '{}',
      '{"token":"","expires_at":"2030-01-01T00:00:00.000Z"}',
      '{"token":"a b","expires_at":"2030-01-01T00:00:00.000Z"}',
      '{"token":"egt","expires_at":"not-a-date"}',
      '{"token":"egt"}',
    ]) {
      await writeFile(path, body);
      await chmod(path, 0o600);
      await expect(readCachedToken(config)).resolves.toBeUndefined();
    }
  });

  it('treats a missing file as absent', async () => {
    await expect(readCachedToken(cacheConfig(cacheDir))).resolves.toBeUndefined();
  });

  it('deletes an absent file without error', async () => {
    await expect(deleteCachedToken(cacheConfig(cacheDir))).resolves.toBeUndefined();
  });

  it('deletes a written file', async () => {
    const config = cacheConfig(cacheDir);
    await writeCachedToken(config, { token: 'egt_secret', expiresAt: '2030-01-01T00:00:00.000Z' });
    await deleteCachedToken(config);

    await expect(readCachedToken(config)).resolves.toBeUndefined();
  });

  it('isFresh uses the 60s margin', () => {
    const now = Date.parse('2030-01-01T00:00:00.000Z');
    const at = (offsetMs: number) => ({
      token: 'egt',
      expiresAt: new Date(now + offsetMs).toISOString(),
    });

    expect(isFresh(at(refreshMarginMs + 1), now)).toBe(true);
    expect(isFresh(at(refreshMarginMs), now)).toBe(false);
    expect(isFresh(at(refreshMarginMs - 1), now)).toBe(false);
    expect(isFresh(at(-1), now)).toBe(false);
  });

  // S3: a far-future `expires_at` is untrusted — the server never issues one that long.
  it('isFresh rejects a far-future expiry beyond the max token lifetime', () => {
    const now = Date.parse('2030-01-01T00:00:00.000Z');
    const at = (offsetMs: number) => ({
      token: 'egt',
      expiresAt: new Date(now + offsetMs).toISOString(),
    });

    expect(isFresh(at(maxTokenLifetimeMs), now)).toBe(true);
    expect(isFresh(at(maxTokenLifetimeMs + 1), now)).toBe(false);
  });
});
