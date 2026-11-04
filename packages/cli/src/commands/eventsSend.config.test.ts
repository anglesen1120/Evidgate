// Configuration and credentials: the flags and the spool config are validated before the token
// exchange or any request, and a 401 purges only the cached CI token, never the spooled events.
import { writeFile } from 'node:fs/promises';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { readCachedToken, writeCachedToken } from '../auth/tokenCache.js';
import { resolveConfig } from '../config.js';
import { resetSecrets } from '../redact.js';

import {
  acked,
  baseEnv,
  capture,
  cleanupSpecState,
  envelopeText,
  errorResponse,
  invoke,
  openSpool,
  runId,
  sendArgv,
  stubFetch,
  tempDir,
} from './eventsSend.testing.js';

describe('events send', () => {
  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await cleanupSpecState();
  });

  it('validates every flag before the token exchange or any request', async () => {
    const spoolDir = await tempDir();
    const cases: ReadonlyArray<{ readonly argv: string[]; readonly reason: string }> = [
      { argv: ['events', 'send', '--run', runId, '--run', runId], reason: 'only once' },
      { argv: ['events', 'send', '--run', 'not-a-uuid'], reason: '--run must be a UUID' },
      {
        argv: ['events', 'send', '--run', runId, '--producer', '../escape'],
        reason: '--producer must be 1..128',
      },
      {
        argv: ['events', 'send', '--run', runId, '--from', 'a.jsonl', '--from', 'b.jsonl'],
        reason: 'only once',
      },
      { argv: ['events', 'send', '--run', runId, '--from', '   '], reason: '--from must be' },
    ];
    for (const testCase of cases) {
      const stub = stubFetch((call) => acked(call));
      const out = capture();
      const code = await invoke(testCase.argv, baseEnv(spoolDir), out, {
        fetch: stub.fetchImpl,
      });
      expect(code).toBe(3);
      expect(stub.calls).toHaveLength(0);
      expect(out.stderr.join('')).toContain('evidgate: error usage:');
      expect(out.stderr.join('')).toContain(testCase.reason);
    }
  });

  it('refuses a missing EVIDGATE_SPOOL_DIR before the verbose dump and any request', async () => {
    const env = baseEnv('/tmp/unused-spool');
    delete env['EVIDGATE_SPOOL_DIR'];
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(['events', 'send', '--run', runId, '--verbose'], env, out, {
      fetch: stub.fetchImpl,
    });

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    const stderr = out.stderr.join('');
    expect(stderr).toContain('evidgate: error missing_config:');
    expect(stderr).toContain('EVIDGATE_SPOOL_DIR');
    // `validateConfig` runs before the dump, so the refused configuration is not echoed.
    expect(stderr).not.toContain('resolved config');
  });

  // CR8: the spool surface is lazy and the command forces it in `validateConfig`, so a
  // malformed value is this command's `invalid_config` — before any request and before the
  // `--verbose` dump, which therefore never echoes it.
  it('refuses a malformed EVIDGATE_SPOOL_MAX_BYTES before any request (CR8)', async () => {
    const spoolDir = await tempDir();
    const stub = stubFetch((call) => acked(call));
    const out = capture();

    const code = await invoke(
      sendArgv([]),
      baseEnv(spoolDir, { EVIDGATE_SPOOL_MAX_BYTES: 'lots' }),
      out,
      { fetch: stub.fetchImpl },
    );

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(0);
    const stderr = out.stderr.join('');
    expect(stderr).toContain('evidgate: error invalid_config:');
    expect(stderr).toContain('EVIDGATE_SPOOL_MAX_BYTES');
    expect(stderr).not.toContain('lots');
    expect(stderr).not.toContain('resolved config');
  });

  it('purges the cached CI token on 401 only (contracts/cli.md §7)', async () => {
    const input = await tempDir();
    const spoolDir = await tempDir();
    const cacheDir = await tempDir();
    const file = join(input, 'events.jsonl');
    await writeFile(file, `${envelopeText(1)}\n`);
    // A `string | undefined` record: the dev token must be absent, and a cached CI token
    // is then the only source (deleting an optional-typed key keeps the type honest).
    const env: Record<string, string | undefined> = baseEnv(spoolDir, {
      EVIDGATE_CACHE_DIR: cacheDir,
      EVIDGATE_INGEST_TOKEN: ['ingest', 'credential'].join('-'),
    });
    delete env['EVIDGATE_DEV_TOKEN'];
    const config = resolveConfig({ env });
    await writeCachedToken(config, {
      token: ['cached', 'token'].join('-'),
      expiresAt: new Date(Date.now() + 30 * 60_000).toISOString(),
    });
    const stub = stubFetch(() => errorResponse(401, 'unauthorized', 'req-401'));
    const out = capture();

    const code = await invoke(sendArgv(['--from', file]), env, out, { fetch: stub.fetchImpl });

    expect(code).toBe(3);
    expect(stub.calls).toHaveLength(1);
    expect(out.stderr.join('')).toContain('evidgate: error unauthorized:');
    expect(out.stderr.join('')).toContain('evidgate: request_id: req-401');
    expect(await readCachedToken(config)).toBeUndefined();
    // The spool is kept: a 401 is not a reason to drop the operator's events.
    expect((await openSpool(spoolDir).then((spool) => spool.cursor())).nextUnackedSequence).toBe(1);
  });
});
