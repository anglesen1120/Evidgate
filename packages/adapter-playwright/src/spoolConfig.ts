// The on-disk layout constants of the spool, in one place: the chunk name width and pattern, the
// two record names, the directory/file modes and the `EVIDGATE_SPOOL_MAX_BYTES` bounds. The CLI
// reader mirrors these, so a second copy here would let the two sides drift.

export const CHUNK_FILE_DIGITS = 6;
export const DEFAULT_FLUSH_THRESHOLD = 64;
export const DEFAULT_CHUNK_MAX_BYTES = 1024 * 1024;
export const DEFAULT_CHUNK_MAX_LINES = 1000;
export const SPOOL_DIR_MODE = 0o700;
export const SPOOL_FILE_MODE = 0o600;
/** The single reporter-owned sequence record. */
export const SHARDS_FILE = 'shards.json';
export const SHARDS_TMP_FILE = 'shards.json.tmp';
/**
 * The legacy sequence store: read as a fallback when `shards.json` is absent and never written
 * again; a leftover staging name is cleaned up at open.
 */
export const CHECKPOINT_FILE = 'checkpoint.json';
export const CHECKPOINT_TMP_FILE = 'checkpoint.json.tmp';
/** `EVIDGATE_SPOOL_MAX_BYTES` default: 256 MiB per run directory (contracts/cli.md §11.4). */
export const DEFAULT_SPOOL_MAX_BYTES = 268_435_456;
/** The largest accepted `EVIDGATE_SPOOL_MAX_BYTES`: 2^40 bytes, i.e. 1 TiB (cli.md §3). */
export const MAX_SPOOL_MAX_BYTES = 1_099_511_627_776;
export const SPOOL_MAX_BYTES_ENV = 'EVIDGATE_SPOOL_MAX_BYTES';

export const CHUNK_FILE_PATTERN = /^(\d{6})\.jsonl$/;
