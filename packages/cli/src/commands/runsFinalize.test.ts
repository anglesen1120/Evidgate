// `evidgate runs finalize`: the request the command sends — the cursor values it reads, the
// derived `Idempotency-Key`, the 202 reconcile-accepted output (text and `--json`), and the
// rule that the exchanged producer token lives only in the cache — never in the spool or a
// stream (contracts/cli.md §5/§7/§11).
import { readFile, readdir } from 'node:fs/promises';
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

import { readCachedToken } from '../auth/tokenCache.js';
import { resolveConfig } from '../config.js';

import { devExchangeAnswer, devToken, mintedToken } from './devExchange.testing.js';
import { deriveFinalizeIdempotencyKey } from './runsFinalize.js';
import {
  runId,
  producerId,
  shard,
  manifestHash,
  exchangeProjectId,
  capture,
  stubFetch,
  invoke,
  accepted,
  stubDevFetch,
  apiCalls,
  sentBody,
  finalizeArgv,
  finalizeWith,
  useFinalizeSpec,
} from './runsFinalize.testing.js';

describe('runs finalize', () => {
  const { writeSpool, devEnv } = useFinalizeSpec();

  // AC6: a 202 prints the reconcile-accepted state, and the request carries exactly the
  // five openapi members — `last_sequence`/`count` come from the cursor, not from flags.
  it('finalizes_with_cursor_values', async () => {
    const { spoolDir } = await writeSpool({ spooled: 3, acked: 3 });
    const env = await devEnv(spoolDir);
    const stub = stubDevFetch(() => accepted());
    const out = capture();

    await expect(invoke(finalizeWith(), env, out, stub.fetchImpl)).resolves.toBe(0);

    expect(out.stdout.join('')).toBe(
      `evidgate: finalized run=${runId} producer=${producerId} shard=1 ` +
        'last_sequence=3 count=3; reconcile accepted (asynchronous)\n',
    );
    expect(out.stderr.join('')).toBe('');
    const calls = apiCalls(stub);
    expect(calls).toHaveLength(1);
    const call = calls[0];
    expect(call?.request.method).toBe('POST');
    // `createApiClient` puts the SDK's `/v1` server prefix on the base URL.
    expect(new URL(call?.request.url ?? '').pathname).toBe(`/v1/runs/${runId}/finalize`);
    expect(call?.request.headers.get('authorization')).toBe(`Bearer ${mintedToken}`);
    const key = call?.request.headers.get('idempotency-key') ?? '';
    expect(key).toMatch(/^[0-9a-f]{64}$/);
    expect(key).toBe(
      deriveFinalizeIdempotencyKey({
        runId,
        producerId,
        shard,
        lastSequence: 3,
        count: 3,
        manifestHash,
      }),
    );
    // `FinalizeRunData.body` has `additionalProperties: false`: only these five members.
    expect(sentBody(call)).toEqual({
      producer_id: producerId,
      shard: 1,
      last_sequence: 3,
      count: 3,
      manifest_hash: manifestHash,
    });
    expect(Object.keys(sentBody(call)).sort()).toEqual([
      'count',
      'last_sequence',
      'manifest_hash',
      'producer_id',
      'shard',
    ]);
  });

  // AC6: `--json` prints one line with the documented key order and the accepted state.
  it('json_prints_reconcile_accepted', async () => {
    const { spoolDir } = await writeSpool({ spooled: 2, acked: 2 });
    const env = await devEnv(spoolDir);
    const stub = stubDevFetch(() => accepted());
    const out = capture();

    await expect(invoke(finalizeWith(['--json']), env, out, stub.fetchImpl)).resolves.toBe(0);

    expect(out.stdout.join('')).toBe(
      `${JSON.stringify({
        run_id: runId,
        producer_id: producerId,
        shard: 1,
        last_sequence: 2,
        count: 2,
        manifest_hash: manifestHash,
        state: 'reconcile_accepted',
      })}\n`,
    );
    expect(apiCalls(stub)).toHaveLength(1);
  });

  // AC6: the key is derived from the request values alone, so two separate invocations over
  // the same acked state send the same key (a retry after a lost 202 is the same no-op),
  // while a different cursor state — or a different manifest hash — never reuses it.
  it('idempotency_key_stable_across_invocations', async () => {
    const keysOf = async (acked: number, hash = manifestHash): Promise<string> => {
      const { spoolDir } = await writeSpool({ spooled: acked, acked });
      const env = await devEnv(spoolDir);
      const stub = stubDevFetch(() => accepted());
      const out = capture();
      const argv = finalizeArgv([
        '--run',
        runId,
        '--producer',
        producerId,
        '--shard',
        String(shard),
        '--manifest-hash',
        hash,
      ]);
      await expect(invoke(argv, env, out, stub.fetchImpl)).resolves.toBe(0);
      const calls = apiCalls(stub);
      expect(calls).toHaveLength(1);
      return calls[0]?.request.headers.get('idempotency-key') ?? '';
    };

    const first = await keysOf(2);
    const second = await keysOf(2);
    expect(first).toMatch(/^[0-9a-f]{64}$/);
    expect(second).toBe(first);
    expect(await keysOf(3)).not.toBe(first);
    expect(await keysOf(2, `sha256:${'c'.repeat(64)}`)).not.toBe(first);
  });

  // §8: the producer token reaches the cache and nothing else — not the spool, not a
  // stream — and the dev credential it was exchanged from never leaves the environment.
  it('exchanged_token_lives_only_in_the_cache', async () => {
    const { spoolDir, producerDir } = await writeSpool({ spooled: 2, acked: 2 });
    const env = await devEnv(spoolDir, { EVIDGATE_SPOOL_MAX_BYTES: '1048576' });
    const producerToken = 'egt_producer_token_for_finalize';
    const stub = stubFetch(
      (request) =>
        devExchangeAnswer(request, {
          token: producerToken,
          projectId: exchangeProjectId,
          expiresAt: new Date(Date.now() + 30 * 60_000).toISOString(),
        }) ?? accepted(),
    );
    const out = capture();

    await expect(invoke(finalizeWith(['--keep-spool']), env, out, stub.fetchImpl)).resolves.toBe(0);

    // The exchanged token, not the dev credential, is the bearer.
    expect(apiCalls(stub)[0]?.request.headers.get('authorization')).toBe(`Bearer ${producerToken}`);
    const everything = `${out.stdout.join('')}${out.stderr.join('')}`;
    expect(everything).not.toContain(devToken);
    expect(everything).not.toContain(producerToken);
    for (const name of await readdir(producerDir)) {
      const content = await readFile(join(producerDir, name), 'utf8');
      expect(content.includes(devToken), `dev token in ${name}`).toBe(false);
      expect(content.includes(producerToken), `producer token in ${name}`).toBe(false);
    }
    // The dev path now exchanges, so the cache holds exactly the exchanged token (and
    // never the dev credential it was exchanged from).
    const cached = await readCachedToken(resolveConfig({ env }));
    expect(cached?.token).toBe(producerToken);
    const cacheFiles = await readdir(String(env.EVIDGATE_CACHE_DIR));
    expect(cacheFiles).toHaveLength(1);
    const cacheText = await readFile(
      join(String(env.EVIDGATE_CACHE_DIR), cacheFiles[0] ?? ''),
      'utf8',
    );
    expect(cacheText).not.toContain(devToken);
  });
});
