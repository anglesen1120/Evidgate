// The spool writer. The on-disk layout is a contract with the CLI reader:
//
//   <spool_dir>/<run_id>/<producer_id>/NNNNNN.jsonl     (6-digit zero-padded chunks)
//   <spool_dir>/<run_id>/<producer_id>/shards.json      { producer_id, shard_index,
//                                                         shard_total?, sequence_next,
//                                                         last_restart_at? }
//   <spool_dir>/<run_id>/<producer_id>/writer.lock      { pid, started_at } (writerLock.ts)
//   <spool_dir>/<run_id>/<producer_id>/checkpoint.json  legacy, read-only fallback
//
// `cursor.json` and `spool.lock` belong to the CLI (contracts/cli.md §11), never to this writer.
//
// `shards.json` is the single reporter-owned sequence record, written atomically at rotation,
// close and restart detection only — a per-callback fsync costs measurable time. The chunk scan
// stays authoritative, so the record may be sparse and may lag: `sequence_next` is the sequence
// after the last line that reached a chunk, never one only buffered here (a rotation mid-batch
// would leave a hole after a crash that the reader refuses as `spool_corrupt`).
//
// A value already on disk at open is a floor and is never raised; a failed record write is retried
// at the next rotation or close instead of failing the flush, because the record is advisory.
//
// Two advisory conditions are queued once per writer for `takeNotices()`: a failed record write at
// rotation (the flush succeeded, so nothing else would report it), and a record floor read at open
// that is ahead of `last chunk sequence + 1` (a hand-edited or legacy file — the resume is
// unchanged). The queue never throws and never carries a path.
//
// Hardening: `O_NOFOLLOW` and, outside Windows, `O_NONBLOCK` on every open, and a write target is
// `lstat`-refused unless regular, so a planted symlink, FIFO or device can neither be followed nor
// block the writer.
//
// Durability: per-callback appends go to a small pending batch, so O(1) per callback and no
// whole-suite buffering; a flush writes it and fsyncs, so a crash loses at most
// `flushThreshold - 1` envelopes and those sequences are minted again. On restart the record is
// reconciled against the highest sequence on disk (the last complete line of the highest chunk,
// backwards, then lower chunks), so a sequence is never reused, and a torn tail is truncated to the
// last newline and fsynced before the first append.
//
// A restart keeps the SAME `producer_id`, records `last_restart_at` and resumes where the durable
// chunks end. `writer.lock` is held for the writer's whole life, so two live processes sharing
// `(run_id, producer_id)` cannot interleave lines; the second fails with `spool_writer_locked` and
// a dead holder's lock is taken over (writerLock.ts, ported from the CLI).
//
// Cap and rotation: `EVIDGATE_SPOOL_MAX_BYTES` caps one run directory (scanned at open, incremented
// by our appends, re-scanned at rotation) and a batch that would cross it is refused with
// `spool_cap_exceeded` BEFORE a byte is written. A chunk rotates at `chunkMaxBytes` (1 MiB) or
// `chunkMaxLines` (1000), always to `highest + 1`; only a successful finalize purges the directory.
//
// This module is the public surface; the responsibilities behind it are split:
// `spoolWriter.ts` (buffering, rotation, close), `spoolScan.ts` (chunk scan), `spoolRecord.ts`
// (record files), `spoolFs.ts` (hardened primitives), `spoolValidation.ts` (input rules),
// `spoolErrors.ts`, `spoolTypes.ts` and `spoolLock.ts` (the exit hook).
export {
  EvidgateSpoolUnsafePathError,
  EvidgateSpoolWriterLockedError,
  isProcessAlive,
  readWriterLock,
  WRITER_LOCK_FILE,
  WRITER_LOCK_FILE_MODE,
  type WriterLockFile,
} from './writerLock.js';

export {
  CHECKPOINT_FILE,
  CHECKPOINT_TMP_FILE,
  CHUNK_FILE_DIGITS,
  DEFAULT_CHUNK_MAX_BYTES,
  DEFAULT_CHUNK_MAX_LINES,
  DEFAULT_FLUSH_THRESHOLD,
  DEFAULT_SPOOL_MAX_BYTES,
  MAX_SPOOL_MAX_BYTES,
  SHARDS_FILE,
  SHARDS_TMP_FILE,
  SPOOL_DIR_MODE,
  SPOOL_FILE_MODE,
  SPOOL_MAX_BYTES_ENV,
} from './spoolConfig.js';

export {
  EvidgateInvalidShardError,
  EvidgateInvalidSpoolConfigError,
  EvidgateInvalidSpoolIdError,
  EvidgateShardRecordMismatchError,
  EvidgateSpoolCapExceededError,
} from './spoolErrors.js';

export {
  assertSpoolMaxBytes,
  assertValidShardOption,
  assertValidSpoolId,
  resolveSpoolMaxBytes,
  SPOOL_ID_MAX_LENGTH,
} from './spoolValidation.js';

export type {
  ShardsRecord,
  SpoolCheckpoint,
  SpoolNotice,
  SpoolNoticeCode,
  SpoolShard,
  SpoolWriterOptions,
} from './spoolTypes.js';

export { SpoolWriter } from './spoolWriter.js';
