// An unwritable token cache is a warning, not a failure: the command keeps the exchanged token
// in memory, finishes with its normal exit code, and prints one `evidgate: warning:` line that
// carries neither the token nor a path below the cache directory (contracts/cli.md §7).
import { chmod, mkdir, mkdtemp, readdir, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { capture, jsonResponse, type Captured } from '../commands/cliSpec.testing.js';
import {
  devStubFetch,
  devToken,
  isDevExchange,
  mintedToken,
} from '../commands/devExchange.testing.js';
import { main } from '../main.js';
import { resetSecrets } from '../redact.js';

import { setCacheWarningWriter, writeCachedToken, type TokenCacheConfig } from './tokenCache.js';

// The mode-based cases are POSIX-only: win32 has no `chmod` bit the write guard reads.
const posix = process.platform !== 'win32';
const project = 'proj-1';
/** A valid `manifest_id` (openapi `format: uuid`). */
const manifestId = '9b2f4c1e-2a3b-4c5d-8e9f-0a1b2c3d4e5f';
/** A valid `manifest_hash` (`sha256:` + 64 lowercase hex). */
const manifestHash = `sha256:${'a'.repeat(64)}`;
/** The `run_id` the stubbed create answers with (openapi `RunCreated`). */
const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';

/** The warning lines of one run; exactly one is the contract. */
function warnings(out: Captured): string[] {
  return out.stderr.filter((line) => line.startsWith('evidgate: warning: '));
}

/** The dev source: a loopback api URL and `EVIDGATE_ENV=dev`, which the dev guard requires. */
function devEnv(cacheDir: string): Record<string, string> {
  return {
    EVIDGATE_API_URL: 'http://127.0.0.1:8080',
    EVIDGATE_PROJECT: project,
    EVIDGATE_ENV: 'dev',
    EVIDGATE_DEV_TOKEN: devToken,
    EVIDGATE_CACHE_DIR: cacheDir,
  };
}

/** `runs create` with both required flags: one exchange, then one request carrying the bearer. */
const createArgv = ['runs', 'create', '--manifest-id', manifestId, '--manifest-hash', manifestHash];

describe('token cache write warning', () => {
  const dirs: string[] = [];

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    setCacheWarningWriter(undefined);
    await Promise.all(
      dirs.splice(0).map(async (dir) => {
        await chmod(dir, 0o700).catch(() => undefined);
        await rm(dir, { recursive: true, force: true });
      }),
    );
  });

  async function tempDir(): Promise<string> {
    const dir = await mkdtemp(join(tmpdir(), 'evidgate-cache-warning-'));
    dirs.push(dir);
    return dir;
  }

  it.runIf(posix)(
    'warns once and still sends the token when the cache dir is read-only',
    async () => {
      const cacheDir = join(await tempDir(), 'cache');
      await mkdir(cacheDir, { mode: 0o700 });
      await chmod(cacheDir, 0o500);
      const stub = devStubFetch(() => jsonResponse(201, { run_id: runId, state: 'queued' }));
      const out = capture();

      await expect(
        main(createArgv, devEnv(cacheDir), out.io, { fetch: stub.fetchImpl }),
      ).resolves.toBe(0);

      // The command finished its own request on the exchanged token, never on a re-exchange.
      const apiCalls = stub.calls.filter((call) => !isDevExchange(call.request));
      expect(apiCalls).toHaveLength(1);
      expect(apiCalls[0]?.request.headers.get('authorization')).toBe(`Bearer ${mintedToken}`);
      expect(out.stdout.join('')).toContain(`run_id=${runId}`);
      expect(warnings(out)).toHaveLength(1);
      expect(await readdir(cacheDir)).toEqual([]);
    },
  );

  it.runIf(posix)('warns once and writes nothing through a symlinked cache dir', async () => {
    const dir = await tempDir();
    const realDir = join(dir, 'real');
    await mkdir(realDir, { mode: 0o700 });
    await symlink(realDir, join(dir, 'cache'));
    const stub = devStubFetch(() => jsonResponse(201, { run_id: runId, state: 'queued' }));
    const out = capture();

    await expect(
      main(createArgv, devEnv(join(dir, 'cache')), out.io, { fetch: stub.fetchImpl }),
    ).resolves.toBe(0);

    expect(warnings(out)).toHaveLength(1);
    expect(await readdir(realDir)).toEqual([]);
  });

  it.runIf(posix)('names the cache dir but no token and no entry path in the warning', async () => {
    const cacheDir = join(await tempDir(), 'cache');
    await mkdir(cacheDir, { mode: 0o700 });
    await chmod(cacheDir, 0o500);
    const stub = devStubFetch(() => jsonResponse(201, { run_id: runId, state: 'queued' }));
    const out = capture();

    await main(createArgv, devEnv(cacheDir), out.io, { fetch: stub.fetchImpl });

    const [warning] = warnings(out);
    expect(warning).toContain(cacheDir);
    expect(warning).toContain('EACCES');
    expect(warning).not.toContain(mintedToken);
    expect(warning).not.toContain(devToken);
    // The entry's digest name encodes the destination and the credential; it stays unprinted.
    expect(warning).not.toContain('ci-token.');
  });

  it('warns once when several writes fail in one invocation', async () => {
    const blocker = join(await tempDir(), 'blocker');
    await writeFile(blocker, 'not a directory', { mode: 0o600 });
    const config: TokenCacheConfig = {
      apiUrl: 'https://api.evidgate.test',
      project,
      cacheDir: join(blocker, 'cache'),
    };
    const value = { token: 'egt_secret_token_value', expiresAt: '2030-01-01T00:00:00.000Z' };
    const lines: string[] = [];
    setCacheWarningWriter((text) => lines.push(text));

    await writeCachedToken(config, value);
    await writeCachedToken(config, value);

    expect(lines).toHaveLength(1);
    expect(lines[0]).toMatch(/^evidgate: warning: could not write the token cache in /);
    expect(lines[0]).not.toContain(value.token);
  });
});
