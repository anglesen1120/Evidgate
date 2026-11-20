// A manifest's shard plan: the resolved shard count and the contiguous index ranges it cuts
// `planned_executions` into. The ranges partition every execution exactly once, each holds
// `floor(E / N)` entries, and the first `E mod N` shards hold one extra (contracts/cli.md §14.4).
import { CliError } from './errors.js';

/** `manifest.v1` caps `shard_plan.shards` at 256 entries. */
const maxShards = 256;

/** A resolved shard count, or the request for one shard per planned execution. */
export type ShardCount = number | 'per-execution';

/** One `shard_plan.shards[]` entry; `to_index` is exclusive. */
export interface ShardDocument {
  readonly shard_id: string;
  readonly from_index: number;
  readonly to_index: number;
}

/** A count that is a decimal integer in `[1, 256]`, or `per-execution`. */
export function parseShardCount(raw: string): ShardCount {
  if (raw === 'per-execution') {
    return raw;
  }
  if (!/^[0-9]+$/.test(raw)) {
    throw shardsRefused();
  }
  const count = Number(raw);
  if (!Number.isSafeInteger(count) || count < 1 || count > maxShards) {
    throw shardsRefused();
  }
  return count;
}

/**
 * The shard plan for `executions` planned executions. `per-execution` resolves to one shard
 * per execution; a count above the execution count or the 256-entry cap is refused, so no
 * shard range is ever empty or outside the execution list.
 */
export function shardDocuments(executions: number, count: ShardCount): ShardDocument[] {
  const resolved = count === 'per-execution' ? executions : count;
  if (resolved < 1 || resolved > executions || resolved > maxShards) {
    throw shardsRefused();
  }
  const size = Math.floor(executions / resolved);
  const larger = executions % resolved;
  const shards: ShardDocument[] = [];
  let from = 0;
  for (let index = 0; index < resolved; index += 1) {
    const to = from + size + (index < larger ? 1 : 0);
    shards.push({ shard_id: `shard-${index + 1}`, from_index: from, to_index: to });
    from = to;
  }
  return shards;
}

/** Names only the flag shape: the offending value is never echoed. */
function shardsRefused(): CliError {
  return new CliError('the resolved shard count is outside the shard plan bounds', {
    code: 'manifest_shards_invalid',
    hint: 'use an integer from 1 to the execution count (at most 256), or per-execution',
  });
}
