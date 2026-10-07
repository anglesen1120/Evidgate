import { chmod, mkdir, mkdtemp, readFile, rm, stat, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { redact, redacted, resetSecrets } from '../redact.js';

import {
  deleteCachedToken,
  isFresh,
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
});
