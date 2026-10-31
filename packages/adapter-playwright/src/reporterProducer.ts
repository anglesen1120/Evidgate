// The producer identity one run writes under, derived from Playwright's shard config. Two
// shards sharing one producer would interleave one sequence, so a disagreeing override is
// refused here, before the first envelope, instead of failing later per batch.
import type { FullConfig } from '@playwright/test/reporter';

import { ProducerShardConflictError } from './reporterFailure.js';
import { resolveProducerId, type ResolvedProducerId } from './shard.js';
import type { SpoolShard } from './spool.js';

export interface ProducerIdentity {
  readonly producer_id: string;
  /** The manifest slot, present only when the producer *is* the derived slot. */
  readonly shard?: SpoolShard;
}

/**
 * Precedence: an explicit `producer_id` wins over the derivation, but only when it agrees
 * with Playwright's shard. Without a shard config there is nothing to compare against, so
 * an explicit id is honoured as before; omitting it derives `shard-1`.
 */
export function resolveProducerIdentity(
  explicitProducerId: string | undefined,
  config: FullConfig | undefined,
): ProducerIdentity {
  const derived = resolveProducerId(config);
  const producerId = explicitProducerId ?? derived.producer_id;
  if (config?.shard !== undefined && config.shard !== null && producerId !== derived.producer_id) {
    throw new ProducerShardConflictError(
      `@evidgate/playwright-reporter: the producer_id option ` +
        `${JSON.stringify(explicitProducerId)} conflicts with the shard Playwright ` +
        `selected (${String(config.shard.current)}/${String(config.shard.total)}): this ` +
        `shard's manifest slot is ${JSON.stringify(derived.producer_id)}. Drop the ` +
        'option to derive it, or pass that id; another id would be rejected by the server ' +
        '(422 unknown_producer).',
    );
  }
  // A custom id on an unsharded run claims no slot: the manifest owns the shard count.
  return producerId === derived.producer_id
    ? { producer_id: producerId, shard: shardOption(derived) }
    : { producer_id: producerId };
}

/** The writer's `shard` option: `total` only when Playwright declared one. */
function shardOption(resolved: ResolvedProducerId): SpoolShard {
  return resolved.shard_total === undefined
    ? { index: resolved.shard_index }
    : { index: resolved.shard_index, total: resolved.shard_total };
}
