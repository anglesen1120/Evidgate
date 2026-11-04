// The `Idempotency-Key` derivation `runs finalize` sends: a documented versioned pre-image
// hashed to 64 lowercase hex, stable across calls and moved by every request value
// (contracts/cli.md §11).
import { createHash } from 'node:crypto';

import { describe, expect, it } from 'vitest';

import { deriveFinalizeIdempotencyKey } from './runsFinalize.js';
import { runId, producerId, shard, manifestHash, keyVersion } from './runsFinalize.testing.js';

describe('deriveFinalizeIdempotencyKey', () => {
  const input = { runId, producerId, shard, lastSequence: 7, count: 7, manifestHash };

  // AC6: 64 lowercase hex, stable across calls, and inside the 128-char route cap.
  it('derives a stable sha256 hex key', () => {
    const key = deriveFinalizeIdempotencyKey(input);
    expect(key).toMatch(/^[0-9a-f]{64}$/);
    expect(key.length).toBeLessThanOrEqual(128);
    expect(deriveFinalizeIdempotencyKey(input)).toBe(key);
  });

  // Every value the command can send in the body feeds the key, so a different body never
  // reuses it (a changed cursor included).
  it('moves with every request value', () => {
    const key = deriveFinalizeIdempotencyKey(input);
    const variants = [
      deriveFinalizeIdempotencyKey({ ...input, runId: '1c2d3e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f' }),
      deriveFinalizeIdempotencyKey({ ...input, producerId: 'shard-2' }),
      deriveFinalizeIdempotencyKey({ ...input, shard: 2 }),
      deriveFinalizeIdempotencyKey({ ...input, lastSequence: 8, count: 8 }),
      deriveFinalizeIdempotencyKey({ ...input, count: 6 }),
      deriveFinalizeIdempotencyKey({ ...input, manifestHash: `sha256:${'b'.repeat(64)}` }),
    ];
    expect(new Set([key, ...variants]).size).toBe(variants.length + 1);
  });

  // The documented pre-image: a versioned JSON array in a fixed order (contracts/cli.md §11).
  it('matches the documented pre-image', () => {
    const preImage = JSON.stringify([
      'evidgate.finalize-key.v1',
      runId,
      producerId,
      shard,
      7,
      7,
      manifestHash,
    ]);
    const expected = createHash('sha256').update(preImage, 'utf8').digest('hex');
    expect(keyVersion).toBe('evidgate.finalize-key.v1');
    expect(deriveFinalizeIdempotencyKey(input)).toBe(expected);
  });
});
