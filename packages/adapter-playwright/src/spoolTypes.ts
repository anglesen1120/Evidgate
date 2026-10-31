// The shapes the spool writer exposes: the shard slot, the `shards.json`/`checkpoint.json`
// records, the writer options and the non-fatal notices. They live together so a consumer
// imports one module for the contract and the writer keeps only behaviour.
import type { WriterLockOptions } from './writerLock.js';

/** The shard slot a producer stands for; `index` is 1-based. */
export interface SpoolShard {
  index: number;
  total?: number;
}

/**
 * `shards.json` — the reporter-owned sequence record. It is written at rotation, close and
 * restart detection only; the chunk scan stays authoritative for the resume sequence, so the
 * record may lag behind the chunks after a crash.
 */
export interface ShardsRecord {
  producer_id: string;
  shard_index?: number;
  shard_total?: number;
  sequence_next: number;
  last_restart_at?: string;
}

/** The legacy `checkpoint.json` shape, read-only. */
export interface SpoolCheckpoint {
  producer_id: string;
  next_sequence: number;
}

export interface SpoolWriterOptions {
  spoolDir: string;
  runId: string;
  producerId: string;
  /** The manifest shard slot this producer stands for. */
  shard?: SpoolShard;
  /** Envelopes buffered before an automatic flush. Default {@link DEFAULT_FLUSH_THRESHOLD}. */
  flushThreshold?: number;
  chunkMaxBytes?: number;
  chunkMaxLines?: number;
  /**
   * The run-directory byte cap. Defaults to `env.EVIDGATE_SPOOL_MAX_BYTES` (256 MiB),
   * resolved once in the constructor with the CLI's parsing rules (contracts/cli.md
   * §3/§11.4).
   */
  maxRunBytes?: number;
  /** Environment for the cap resolution; defaults to `process.env` (test seam). */
  env?: Readonly<Record<string, string | undefined>>;
  /** Clock for `last_restart_at`; defaults to `new Date()` (injected by tests). */
  now?: () => Date;
  /** Single-writer lock seams (pid, liveness, probe pause); the lock keeps its own clock. */
  lock?: Omit<WriterLockOptions, 'now'>;
}

/** The advisory conditions {@link SpoolWriter.takeNotices} can report. */
export type SpoolNoticeCode = 'shards_write_failed' | 'shards_floor_ahead';

/**
 * One non-fatal spool condition, queued once per writer. `message` is safe to log or attach
 * to an event: it names base names, numbers and errno codes only — never a full path, an
 * error message body or any file content.
 */
export interface SpoolNotice {
  code: SpoolNoticeCode;
  message: string;
}
