// What `runs create` puts on the wire: the documented derived key, an explicit
// `--idempotency-key` overriding it, and the closed request body (contracts/cli.md §9,
// openapi `CreateRunRequest`).
import { createHash } from 'node:crypto';

import { describe, expect, it } from 'vitest';

import { mintedToken } from './devExchange.testing.js';
import { deriveRunIdempotencyKey, type RunIdempotencyKeyInput } from './runsCreate.js';
import {
  apiCalls,
  capture,
  createArgv,
  created,
  invoke,
  manifestHash,
  manifestId,
  otherManifestHash,
  otherManifestId,
  sentBody,
  stubDevFetch,
  useRunsCreateQaSpec,
} from './runsCreate.qa.testing.js';

describe('runs create — QA boundaries', () => {
  const { devEnv } = useRunsCreateQaSpec();

  // AC4: the pre-image is a JSON array, so no separator or field split can make two
  // different intents collide. Every pair here would collide under a concatenation with any
  // single separator, and none of them collide under the array.
  it('derived_key_cannot_collide_across_a_field_split', () => {
    const pairs: ReadonlyArray<readonly [RunIdempotencyKeyInput, RunIdempotencyKeyInput]> = [
      [
        { manifestId, manifestHash, release: 'a', ciRef: 'b' },
        { manifestId, manifestHash, release: 'ab', ciRef: null },
      ],
      [
        { manifestId, manifestHash, release: null, ciRef: 'refs/heads/main' },
        { manifestId, manifestHash, release: 'refs/heads/main', ciRef: null },
      ],
      [
        { manifestId, manifestHash, release: 'a"', ciRef: 'b' },
        { manifestId, manifestHash, release: 'a', ciRef: '"b' },
      ],
      [
        { manifestId: 'a-b', manifestHash: 'c' },
        { manifestId: 'a', manifestHash: 'b-c' },
      ],
      [
        { manifestId, manifestHash, release: 'x', ciRef: 'y' },
        { manifestId: otherManifestId, manifestHash: otherManifestHash, release: 'x', ciRef: 'y' },
      ],
    ];

    const keys: string[] = [];
    for (const [left, right] of pairs) {
      const leftKey = deriveRunIdempotencyKey(left);
      const rightKey = deriveRunIdempotencyKey(right);
      expect(leftKey).not.toBe(rightKey);
      keys.push(leftKey, rightKey);
    }
    expect(new Set(keys).size).toBe(keys.length);
    for (const key of keys) {
      expect(key).toMatch(/^[0-9a-f]{64}$/);
      expect(key.length).toBeLessThanOrEqual(128);
    }
    // The derivation is pure: the same input object derives the same key on a re-call.
    expect(deriveRunIdempotencyKey({ manifestId, manifestHash, release: 'a', ciRef: 'b' })).toBe(
      createHash('sha256')
        .update(JSON.stringify(['evidgate.run-key.v1', manifestHash, manifestId, 'a', 'b']), 'utf8')
        .digest('hex'),
    );
  });

  // AC2/AC4: an explicit key is sent verbatim and is the only key on the header — the
  // derived value never also travels (they are different values here).
  it('explicit_key_overrides_the_derived_key', async () => {
    const env = await devEnv();
    const derived = deriveRunIdempotencyKey({ manifestId, manifestHash });
    const stub = stubDevFetch(() => created());
    const argv = createArgv(['--idempotency-key', 'qa-explicit-key']);
    await expect(invoke(argv, env, capture(), stub.fetchImpl)).resolves.toBe(0);

    const header = apiCalls(stub)[0]?.request.headers.get('idempotency-key');
    expect(header).toBe('qa-explicit-key');
    expect(header).not.toBe(derived);
    expect(apiCalls(stub)).toHaveLength(1);
  });

  // AC5 / openapi `CreateRunRequest` + `RunRefRequest` are closed shapes: the wire body
  // carries exactly the members the CLI owns, and the hash/policy/suite/env never travel.
  it('request_body_carries_only_documented_members', async () => {
    const env = await devEnv();
    const stub = stubDevFetch(() => created());
    const argv = createArgv([
      '--release',
      'rel/1.2',
      '--ci-ref',
      'refs/heads/main',
      '--idempotency-key',
      'qa-body-key',
    ]);
    await expect(invoke(argv, env, capture(), stub.fetchImpl)).resolves.toBe(0);

    const call = apiCalls(stub)[0];
    // The command runs on the exchanged token: the dev token only travels in the exchange
    // body, so a regression that sent it as the bearer fails here.
    expect(call?.request.headers.get('authorization')).toBe(`Bearer ${mintedToken}`);
    expect(Object.keys(sentBody(call)).sort()).toEqual(['manifest_id', 'ref', 'release']);
    const raw = call?.body.toString('utf8') ?? '';
    for (const forbidden of [
      'manifest_hash',
      'purpose',
      'components',
      'replay_grant_id',
      'policy',
      'suite',
      'env',
      'head_sha',
      'base_sha',
      'trigger_event',
      'unverified_ref',
    ]) {
      expect(raw, forbidden).not.toContain(forbidden);
    }
    // The hash travels nowhere: not the body, not a query string, not a header.
    expect(call?.request.url).not.toContain('sha256');
    for (const [, value] of call?.request.headers ?? []) {
      expect(value).not.toContain('sha256');
    }
  });
});
