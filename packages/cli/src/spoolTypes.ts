// The shapes the spool hands to the rest of the CLI: one event as read back from a chunk,
// the ack cursor, the open options, and one chunk file.

/** One event read back from a chunk. */
export interface SpoolEvent {
  /** The envelope's `sequence` (contiguous from `cursor.nextUnackedSequence`). */
  readonly sequence: number;
  /** The envelope's `event_id`, the server-side dedupe identity. */
  readonly eventId: string;
  /** The exact JSON text of the line, without the trailing `\n`. */
  readonly line: string;
  /**
   * UTF-8 bytes of `line` including its `\n`, measured when the line was read. Nothing on
   * the send path reads it: the batcher sizes a batch from `line` itself (`batcher.ts`
   * body accounting) and the sender builds the body from `line`. It is kept because the
   * spool specs assert the measured length is exactly the on-disk byte count.
   */
  readonly lineBytes: number;
  /** The chunk file it came from, e.g. `000003.jsonl` (for error messages). */
  readonly chunk: string;
  /** 1-based physical line number inside that chunk. */
  readonly lineNumber: number;
}

/** The CLI-owned ack cursor. `next_unacked_sequence === last_acked_sequence + 1`. */
export interface SpoolCursor {
  readonly producerId: string;
  readonly nextUnackedSequence: number;
  readonly lastAckedSequence: number;
  readonly ackedCount: number;
}

/** Options of {@link ProducerSpool.open}. */
export interface OpenProducerSpoolOptions {
  /** The spool root (`EVIDGATE_SPOOL_DIR`), already absolute-resolved by the command. */
  readonly spoolDir: string;
  readonly runId: string;
  readonly producerId: string;
  /** The run-directory byte cap (`EVIDGATE_SPOOL_MAX_BYTES`). */
  readonly maxBytes: number;
  /**
   * Create `<spool_dir>/<run_id>/<producer_id>` (0700) when absent. `false` (default)
   * opens an existing producer spool only, and fails with `spool_missing`.
   */
  readonly createIfMissing?: boolean;
}

/** One chunk file of a producer spool, in index order. */
export interface ChunkFile {
  readonly index: number;
  readonly name: string;
  readonly path: string;
}
