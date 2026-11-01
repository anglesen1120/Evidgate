import { execFileSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
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
import { basename, join } from 'node:path';

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

// The mode/owner checks are POSIX-only; on win32 they are skipped.
const posix = process.platform !== 'win32';

const validBody = '{"token":"egt_secret","expires_at":"2030-01-01T00:00:00.000Z"}';

function cacheConfig(
  cacheDir: string,
  apiUrl = 'https://api.evidgate.test',
  project = 'proj-1',
): TokenCacheConfig {
  return { apiUrl, project, cacheDir };
}

/** The destination-only name the previous scheme wrote; the new scheme must never read it. */
function legacyCachePath(config: TokenCacheConfig): string {
  const key = createHash('sha256').update(`${config.apiUrl}\n${config.project}`).digest('hex');
  return join(config.cacheDir, `ci-token.${key}.json`);
}

// The `0600` cache file: atomic write, keyed by api_url, project and the source credential,
// and every untrusted file (wrong mode, symlink, corrupt) or cache dir ignored.
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

  // A credential replaced under the same api_url/project must not reuse the token the old one
  // minted. A dev token is the ingest credential `getToken` presents, so both key one file —
  // which is what lets a command purge with the raw config the read did not use.
  it('keys the file by the source credential', () => {
    const base = cacheConfig(cacheDir);
    const first = tokenCachePath({ ...base, ingestToken: 'ingest-one' });
    const rotated = tokenCachePath({ ...base, ingestToken: 'ingest-two' });
    const oidc = tokenCachePath({ ...base, oidcToken: 'oidc-jwt' });
    const dev = tokenCachePath({ ...base, devToken: 'dev-token-value' });
    const asIngest = tokenCachePath({ ...base, ingestToken: 'dev-token-value' });

    expect(first).not.toBe(rotated);
    expect(first).not.toBe(oidc);
    expect(dev).toBe(asIngest);
  });

  // The binding is a one-way digest: neither the name nor the content may carry the credential
  // or a reversible encoding of it, and the name must differ from the destination-only one.
  it('keeps the credential and its reversible encodings out of the name and content', async () => {
    const credential = 'egt_ingest_credential_value';
    const config = { ...cacheConfig(cacheDir), ingestToken: credential };
    await writeCachedToken(config, {
      token: 'egt_ci_token',
      expiresAt: '2030-01-01T00:00:00.000Z',
    });

    const path = tokenCachePath(config);
    const text = await readFile(path, 'utf8');
    const forms = [
      credential,
      Buffer.from(credential).toString('base64'),
      Buffer.from(credential).toString('base64url'),
      Buffer.from(credential).toString('hex'),
    ];

    expect(path).not.toBe(legacyCachePath(config));
    for (const form of forms) {
      expect(path).not.toContain(form);
      expect(text).not.toContain(form);
    }
    expect(text).toBe('{"token":"egt_ci_token","expires_at":"2030-01-01T00:00:00.000Z"}');
  });

  // An entry written before the credential was part of the key has no binding to this read:
  // it is ignored, never trusted, even though its file name names this api_url and project.
  it('ignores an entry under the destination-only name of the previous scheme', async () => {
    const config = { ...cacheConfig(cacheDir), ingestToken: 'ingest-token' };
    await mkdir(cacheDir, { recursive: true, mode: 0o700 });
    await writeFile(legacyCachePath(config), validBody, { mode: 0o600 });

    await expect(readCachedToken(config)).resolves.toBeUndefined();
  });

  it('purges the destination-only file of the previous scheme', async () => {
    const config = { ...cacheConfig(cacheDir), ingestToken: 'ingest-token' };
    await mkdir(cacheDir, { recursive: true, mode: 0o700 });
    await writeFile(legacyCachePath(config), validBody, { mode: 0o600 });

    await deleteCachedToken(config);

    await expect(lstat(legacyCachePath(config))).rejects.toThrow();
  });

  // `auth logout` needs no credential (it must work once the source is gone): a purge with only
  // api_url/project/cache_dir removes every bound entry for that destination and nothing else.
  it('deletes every credential entry for the destination without a credential', async () => {
    const base = cacheConfig(cacheDir);
    const bound = { ...base, ingestToken: 'ingest-one' };
    const otherProject = { ...base, project: 'proj-2', ingestToken: 'ingest-one' };
    await writeCachedToken(bound, { token: 'egt_bound', expiresAt: '2030-01-01T00:00:00.000Z' });
    await writeCachedToken(otherProject, {
      token: 'egt_other',
      expiresAt: '2030-01-01T00:00:00.000Z',
    });

    await deleteCachedToken(base);

    await expect(readCachedToken(bound)).resolves.toBeUndefined();
    await expect(readCachedToken(otherProject)).resolves.toEqual({
      token: 'egt_other',
      expiresAt: '2030-01-01T00:00:00.000Z',
    });
  });

  // A write that crashed left a temp file holding a whole token at 0600, and its name carries no
  // destination. A purge removes that one shape and leaves every other name in the directory.
  it('deletes a crash-left temp file and keeps unrelated names and other projects', async () => {
    const base = cacheConfig(cacheDir);
    const otherProject = { ...base, project: 'proj-2' };
    await mkdir(cacheDir, { recursive: true, mode: 0o700 });
    const staleTemp = join(cacheDir, `.ci-token.${randomUUID()}.tmp`);
    const plainTemp = join(cacheDir, '.notes.tmp');
    const wrongShape = join(cacheDir, '.ci-token.not-a-uuid.tmp');
    await writeFile(staleTemp, validBody, { mode: 0o600 });
    await writeFile(plainTemp, 'keep', { mode: 0o600 });
    await writeFile(wrongShape, 'keep', { mode: 0o600 });
    await writeCachedToken(otherProject, {
      token: 'egt_other',
      expiresAt: '2030-01-01T00:00:00.000Z',
    });

    await deleteCachedToken(base);

    await expect(lstat(staleTemp)).rejects.toThrow();
    expect(await readFile(plainTemp, 'utf8')).toBe('keep');
    expect(await readFile(wrongShape, 'utf8')).toBe('keep');
    await expect(readCachedToken(otherProject)).resolves.toEqual({
      token: 'egt_other',
      expiresAt: '2030-01-01T00:00:00.000Z',
    });
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

  it.skipIf(!posix)('reads as absent through a symlinked cache dir', async () => {
    const config = cacheConfig(cacheDir);
    const realDir = join(root, 'real-dir');
    await mkdir(realDir, { mode: 0o700 });
    await writeFile(join(realDir, basename(tokenCachePath(config))), validBody, { mode: 0o600 });
    await symlink(realDir, cacheDir);

    await expect(readCachedToken(config)).resolves.toBeUndefined();
  });

  it.skipIf(!posix)('refuses a symlinked cache dir given with a trailing slash', async () => {
    const realDir = join(root, 'real-dir');
    await mkdir(realDir, { mode: 0o700 });
    await symlink(realDir, cacheDir);
    const config = cacheConfig(`${cacheDir}/`);
    await writeFile(join(realDir, basename(tokenCachePath(config))), validBody, { mode: 0o600 });

    await expect(readCachedToken(config)).resolves.toBeUndefined();
    await expect(
      writeCachedToken(config, { token: 'egt_secret', expiresAt: '2030-01-01T00:00:00.000Z' }),
    ).rejects.toMatchObject({ code: 'token_cache_error' });
    expect(await readdir(realDir)).toEqual([basename(tokenCachePath(config))]);
  });

  it.skipIf(!posix)('reads as absent from a group/world-writable cache dir', async () => {
    const config = cacheConfig(cacheDir);
    await mkdir(cacheDir, { mode: 0o700 });
    await writeFile(tokenCachePath(config), validBody, { mode: 0o600 });
    await expect(readCachedToken(config)).resolves.toMatchObject({ token: 'egt_secret' });

    await chmod(cacheDir, 0o770);
    await expect(readCachedToken(config)).resolves.toBeUndefined();
    await chmod(cacheDir, 0o702);
    await expect(readCachedToken(config)).resolves.toBeUndefined();
  });

  it.skipIf(!posix)('refuses to write into a group/world-writable cache dir', async () => {
    const config = cacheConfig(cacheDir);
    await mkdir(cacheDir, { mode: 0o700 });
    await chmod(cacheDir, 0o777);

    await expect(
      writeCachedToken(config, { token: 'egt_secret', expiresAt: '2030-01-01T00:00:00.000Z' }),
    ).rejects.toMatchObject({ code: 'token_cache_error' });
    expect(await readdir(cacheDir)).toEqual([]);
  });

  it('purges entries of the destination minted under another credential after a write', async () => {
    const base = cacheConfig(cacheDir);
    const first = { ...base, ingestToken: 'ingest-one' };
    const rotated = { ...base, ingestToken: 'ingest-two' };
    const otherProject = { ...base, project: 'proj-2', ingestToken: 'ingest-one' };
    const body = { token: 'egt_secret', expiresAt: '2030-01-01T00:00:00.000Z' };
    await writeCachedToken(first, body);
    await writeCachedToken(otherProject, body);
    await writeFile(join(cacheDir, 'notes.txt'), 'keep', { mode: 0o600 });

    await writeCachedToken(rotated, body);

    expect((await readdir(cacheDir)).sort()).toEqual(
      [
        basename(tokenCachePath(rotated)),
        basename(tokenCachePath(otherProject)),
        'notes.txt',
      ].sort(),
    );
  });

  it.skipIf(!posix)('purges an orphan symlink without touching its target', async () => {
    const base = cacheConfig(cacheDir);
    const first = { ...base, ingestToken: 'ingest-one' };
    const rotated = { ...base, ingestToken: 'ingest-two' };
    const target = join(root, 'outside.json');
    await writeFile(target, 'untouched', { mode: 0o600 });
    await mkdir(cacheDir, { mode: 0o700 });
    await symlink(target, tokenCachePath(first));

    await writeCachedToken(rotated, { token: 'egt_secret', expiresAt: '2030-01-01T00:00:00.000Z' });

    await expect(lstat(tokenCachePath(first))).rejects.toMatchObject({ code: 'ENOENT' });
    expect(await readFile(target, 'utf8')).toBe('untouched');
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
