// The `Idempotency-Key` of `evidgate runs create`: the documented derived key and its golden
// pre-image, the replay of an identical request, and the 409 conflict that a different body
// under the same key earns (contracts/cli.md §5/§7/§11).
import { createHash } from 'node:crypto';

import { describe, expect, it } from 'vitest';

import { readCachedToken, writeCachedToken } from '../auth/tokenCache.js';
import { resolveConfig } from '../config.js';

import { deriveRunIdempotencyKey } from './runsCreate.js';
import {
  keyVersion,
  manifestHash,
  manifestId,
  otherManifestHash,
  otherManifestId,
  runId,
  apiCalls,
  capture,
  createArgv,
  created,
  invoke,
  jsonResponse,
  sentBody,
  stubDevFetch,
  stubFetch,
  useRunsCreateSpec,
} from './runsCreate.testing.js';

describe('runs create', () => {
  const { devEnv, ingestEnv } = useRunsCreateSpec();

  // AC3: two invocations with the same key and the same payload send the
  // identical request, the stub replays the stored 201, and both print that same run.
  it('idempotent_replay', async () => {
    const env = await devEnv();
    const stub = stubDevFetch(() => created());
    const argv = createArgv(['--idempotency-key', 'replay-key-1']);
    const first = capture();
    const second = capture();

    await expect(invoke(argv, env, first, stub.fetchImpl)).resolves.toBe(0);
    await expect(invoke(argv, env, second, stub.fetchImpl)).resolves.toBe(0);

    const line = `run_id=${runId} manifest_hash=${manifestHash} state=queued\n`;
    expect(first.stdout.join('')).toBe(line);
    expect(first.stdout.join('')).toBe(second.stdout.join(''));
    expect(first.stderr.join('')).toBe('');
    // One exchange (the second invocation reuses the cached token), then two creates.
    const calls = apiCalls(stub);
    expect(calls).toHaveLength(2);
    expect(calls[1]?.request.headers.get('idempotency-key')).toBe('replay-key-1');
    // The second request is byte-identical, so the server replays instead of conflicting.
    expect(sentBody(calls[1])).toEqual(sentBody(calls[0]));
    expect(calls[1]?.body.toString('utf8')).toBe(calls[0]?.body.toString('utf8') ?? '');
  });

  // AC3 + T11: the same key with a different body is 409
  // `idempotency_conflict`, which exits 3 echoing the server's code — and the cached
  // token survives, because a conflict says nothing about the token (contracts/cli.md §7).
  it('idempotency_conflict_exit3', async () => {
    const env = await ingestEnv();
    const config = resolveConfig({ env });
    const expiresAt = new Date(Date.now() + 30 * 60_000).toISOString();
    await writeCachedToken(config, { token: 'egt_cached_token', expiresAt });

    const stub = stubFetch(() =>
      jsonResponse(409, {
        error: {
          code: 'idempotency_conflict',
          message: 'the key was used with a different body',
          request_id: 'req-9',
          retryable: false,
        },
      }),
    );
    const out = capture();

    const argv = createArgv(['--idempotency-key', 'conflict-key-1']);
    await expect(invoke(argv, env, out, stub.fetchImpl)).resolves.toBe(3);

    const text = out.stderr.join('');
    expect(text).toContain('evidgate: error idempotency_conflict: ');
    expect(text).toContain('evidgate: request_id: req-9');
    expect(out.stdout.join('')).toBe('');
    // The cached token was reused (no exchange), so the create is the only call.
    expect(stub.calls).toHaveLength(1);
    await expect(readCachedToken(config)).resolves.toEqual({
      token: 'egt_cached_token',
      expiresAt,
    });
  });

  // AC4 (amended 2026-10-14): the derived key is the SHA-256 hex
  // of the documented JSON array. Same inputs → same key, across two separate
  // invocations; each request field moves the key; an absent optional member is JSON null.
  it('derived_key_stable', async () => {
    const key = deriveRunIdempotencyKey({ manifestId, manifestHash });
    expect(key).toMatch(/^[0-9a-f]{64}$/);
    expect(key.length).toBeLessThanOrEqual(128);
    // Deterministic: the same inputs derive the same key on a second call.
    expect(deriveRunIdempotencyKey({ manifestId, manifestHash })).toBe(key);
    // An absent optional member occupies the same slot as an explicit `null`.
    const absent = deriveRunIdempotencyKey({
      manifestId,
      manifestHash,
      release: null,
      ciRef: null,
    });
    expect(absent).toBe(key);
    // Golden vector, recomputed here from the documented pre-image (never a literal).
    const golden = [keyVersion, manifestHash, manifestId, null, null];
    expect(key).toBe(createHash('sha256').update(JSON.stringify(golden), 'utf8').digest('hex'));

    const withOptionals = deriveRunIdempotencyKey({
      manifestId,
      manifestHash,
      release: 'rel-1',
      ciRef: 'refs/heads/main',
    });
    const goldenWithOptionals = [keyVersion, manifestHash, manifestId, 'rel-1', 'refs/heads/main'];
    expect(withOptionals).toBe(
      createHash('sha256').update(JSON.stringify(goldenWithOptionals), 'utf8').digest('hex'),
    );

    // Every request value feeds the key: a different manifest_id, manifest_hash, release
    // or ci-ref never reuses it, and the variants are distinct from each other too.
    const variants = [
      deriveRunIdempotencyKey({ manifestId: otherManifestId, manifestHash }),
      deriveRunIdempotencyKey({ manifestId, manifestHash: otherManifestHash }),
      deriveRunIdempotencyKey({ manifestId, manifestHash, release: 'rel-1' }),
      deriveRunIdempotencyKey({ manifestId, manifestHash, ciRef: 'refs/heads/main' }),
    ];
    for (const variant of variants) {
      expect(variant).not.toBe(key);
      expect(variant).toMatch(/^[0-9a-f]{64}$/);
    }
    expect(new Set(variants).size).toBe(variants.length);
    expect(withOptionals).not.toBe(key);

    // The same derivation reaches the wire: one key per body, and the identical key in a
    // second, separate invocation (no `--idempotency-key` is passed at all).
    const env = await devEnv();
    const stub = stubDevFetch(() => created());
    await expect(invoke(createArgv(), env, capture(), stub.fetchImpl)).resolves.toBe(0);
    await expect(invoke(createArgv(), env, capture(), stub.fetchImpl)).resolves.toBe(0);
    const flagged = createArgv(['--release', 'rel-1', '--ci-ref', 'refs/heads/main']);
    await expect(invoke(flagged, env, capture(), stub.fetchImpl)).resolves.toBe(0);
    const calls = apiCalls(stub);
    expect(calls).toHaveLength(3);
    expect(calls[0]?.request.headers.get('idempotency-key')).toBe(key);
    expect(calls[1]?.request.headers.get('idempotency-key')).toBe(key);
    expect(calls[2]?.request.headers.get('idempotency-key')).toBe(withOptionals);
  });
});
