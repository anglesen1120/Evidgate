// 04.3.3-T1.1: shard → producer mapping (T9/T54) and merge-replay detection (AC3).
//
// Playwright 1.63 exposes shard identity in ONE place: the resolved `FullConfig.shard`
// (`{ total, current }`, `current` 1-based), which `--shard <current>/<total>` sets
// (`node_modules/playwright/lib/program.js:216`), a config `shard:` may set
// (`node_modules/playwright/lib/common/index.js:528`, validated at 1381-1387), and the
// reporter receives as `onBegin(config, suite)` (`types/test.d.ts:2149-2159`). There is
// no `PLAYWRIGHT_SHARD` environment variable in the installed 1.63.0 (`grep SHARD
// node_modules/playwright/lib` finds only `PWTEST_SHARD_WEIGHTS`, which carries weights,
// not the index), so this module reads no environment variable at all. That absence is
// asserted by `shard.test.ts` ("no env override") rather than assumed.
//
// Naming is fixed by README T54: `shard-<index>`, 1-based, and `shard-1` for an unsharded
// run, so the id equals the manifest slot the server registered for that shard:
// `ProducerRegistry.ProducerId(int shardIndex) => $"shard-{shardIndex}"`
// (src/Modules/Evidgate.Modules.Ingestion/Application/ProducerRegistry.cs:344) and
// `planned_execution.shard_id = shard-<ordinal>`
// (src/Modules/Evidgate.Modules.Orchestrator/Application/Runs/CreateRunCommand.cs:666).
// A mismatch is a 422 `unknown_producer` server-side (04.1.1-T4.2), so the id is derived
// here and never invented.
import { EvidgateInvalidShardError } from './spool.js';

/**
 * The spool-safe producer id shape (04.3.3 §3, 00 §9.4 `producer_id`): letters, digits,
 * `.`, `_` and `-`, at most 128 characters, so it is one safe path segment and one valid
 * envelope `producer_id`. The server accepts a superset of the length bound
 * (`^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$`); every id this module derives satisfies both.
 */
export const PRODUCER_ID_PATTERN = /^[A-Za-z0-9._-]{1,128}$/;

/** The manifest shard range: `shard_plan.shards.maxItems` (contracts/manifest/manifest.v1.json). */
export const MAX_SHARD_INDEX = 256;

/** The Playwright CLI subcommand whose replay must not emit (`merge-reports`). */
export const MERGE_REPORTS_COMMAND = 'merge-reports';

/** True when `value` is a spool-safe producer id. */
export function isProducerId(value: string): boolean {
  return value.length <= 128 && PRODUCER_ID_PATTERN.test(value);
}

/** The resolved shard of a run, as Playwright reports it (`FullConfig.shard`, 1-based). */
export interface PlaywrightShard {
  total: number;
  current: number;
}

/** What {@link resolveProducerId} reads: the reporter's `onBegin` config, narrowed. */
export interface ProducerShardConfig {
  shard?: PlaywrightShard | null;
}

/** The producer identity of one shard (T54). `shard_total` is absent for an unsharded run. */
export interface ResolvedProducerId {
  producer_id: string;
  shard_index: number;
  shard_total?: number;
}

/**
 * The producer id of the shard `config` describes: `shard-<current>` when Playwright is
 * sharded, `shard-1` when it is not (T54 — the manifest declares exactly one slot then).
 *
 * Precedence: this derivation is the only source of an id; the reporter's explicit
 * `producer_id` option may override it, and the reporter refuses the override when a
 * shard config exists and the two disagree (reporter.ts).
 *
 * An out-of-range shard (`current`/`total` outside 1..256, non-integer, or
 * `current > total`) throws {@link EvidgateInvalidShardError} (`invalid_shard`) instead of
 * deriving an id the manifest cannot have declared. Playwright itself validates
 * `config.shard` (`lib/common/index.js:1381-1387`), so this only fires on a hand-built or
 * programmatic config.
 */
export function resolveProducerId(config?: ProducerShardConfig | null): ResolvedProducerId {
  const shard = config?.shard ?? null;
  if (shard === null) {
    // T54: an unsharded run is `shard-1`. No total is claimed: the manifest owns the shard
    // count, and a `--shard`-less invocation in a CI matrix says nothing about it.
    return { producer_id: 'shard-1', shard_index: 1 };
  }

  const total = shard.total;
  const current = shard.current;
  if (!Number.isSafeInteger(total) || total < 1 || total > MAX_SHARD_INDEX) {
    throw new EvidgateInvalidShardError(
      `config.shard.total must be an integer between 1 and ${String(MAX_SHARD_INDEX)}`,
    );
  }
  if (!Number.isSafeInteger(current) || current < 1 || current > total) {
    throw new EvidgateInvalidShardError(
      'config.shard.current must be an integer between 1 and config.shard.total',
    );
  }

  const producerId = `shard-${String(current)}`;
  // Defensive: the derivation is a template of an integer, so this can only fire if the
  // pattern and the derivation drift apart.
  if (!isProducerId(producerId)) {
    throw new EvidgateInvalidShardError('the derived producer id is not spool-safe');
  }
  return { producer_id: producerId, shard_index: current, shard_total: total };
}

/**
 * True when this process is Playwright's `merge-reports` replay
 * (`node_modules/playwright/lib/program.js:108-117`), whose reporter receives events of
 * several shards through ONE instance.
 *
 * The merge replay must not emit: every envelope was already produced by its own shard
 * process under that shard's `producer_id` and contiguous sequence (AC1/AC2), and the
 * replay exposes no per-event shard identity on the reporter API a custom reporter can
 * reach — the merged config is `shard: null` (`lib/runner/index.js` `mergeConfigs`), the
 * blob's own shard metadata is consumed inside Playwright, and the per-blob
 * `onReportConfigure` hook (which would carry each blob's shard config) is dispatched only
 * to built-in reporters: a custom reporter is wrapped in `ReporterV2Wrapper`
 * (`lib/runner/index.js:1697-1715`), which has no such method. Emitting here would have to
 * guess an id and would duplicate sequences, so the reporter refuses (AC3).
 *
 * Detection reads the CLI arguments, the only signal available before any callback.
 *
 * `args` are the CLI arguments (`process.argv.slice(2)`). Playwright's root program declares
 * commands only; `-c/--config`, `--reporter` and `--output` belong to the subcommands
 * themselves (`lib/program.js:116-117` for `merge-reports`, the same options on `test`), so in
 * every invocation Playwright accepts the subcommand is the first token that is not a flag.
 * No option-value skipping is needed, and skipping would let a value named `merge-reports`
 * hide a real replay.
 */
export function isMergeReplay(args: readonly string[] = process.argv.slice(2)): boolean {
  for (const arg of args) {
    if (arg.startsWith('-')) continue;
    return arg === MERGE_REPORTS_COMMAND;
  }
  return false;
}
