// The chunk scan the writer does at open and at rotation: which chunk is highest, the torn-tail
// repair before the first append, and the highest durable sequence. The scan — not `shards.json` —
// is the resume authority, so these three walk the same chunk files in the same order.
import {
  closeSync,
  constants as fsConstants,
  fsyncSync,
  ftruncateSync,
  readdirSync,
} from 'node:fs';

import { CHUNK_FILE_PATTERN, SPOOL_FILE_MODE } from './spoolConfig.js';
import {
  O_NOFOLLOW,
  assertWritableTargetIsRegularFile,
  chunkPath,
  countLines,
  openVerified,
  readChunk,
  readChunkIfExists,
} from './spoolFs.js';

/** The descriptor and counters of a chunk that is open for appending. */
export interface OpenedChunk {
  chunkIndex: number;
  chunkBytes: number;
  chunkLines: number;
  fd: number;
}

/**
 * Opens the chunk of `currentIndex` for appending (index 1 when none was opened yet) and answers
 * the counters its content implies. `content` is the already-read body of the highest chunk, so a
 * caller that needed it for the scan does not pay for a second read.
 */
export function openChunkFile(
  directory: string,
  currentIndex: number,
  content: Buffer | null,
): OpenedChunk {
  const chunkIndex = currentIndex < 1 ? 1 : currentIndex;
  const path = chunkPath(directory, chunkIndex);
  const existing = content ?? readChunkIfExists(path);
  let chunkBytes = 0;
  let chunkLines = 0;
  if (existing !== null) {
    chunkBytes = existing.byteLength;
    chunkLines = countLines(existing.toString('utf8'));
  }
  // O_NOFOLLOW so a planted chunk symlink is refused; openVerified then rejects a non-regular
  // or foreign-owned file and forces 0600 on the descriptor. The append open is the one that
  // would BLOCK on a planted FIFO, so the target is refused before the open when it is not a
  // regular file.
  assertWritableTargetIsRegularFile(path);
  const fd = openVerified(
    path,
    fsConstants.O_WRONLY | fsConstants.O_APPEND | fsConstants.O_CREAT | O_NOFOLLOW,
    SPOOL_FILE_MODE,
  );
  return { chunkIndex, chunkBytes, chunkLines, fd };
}

/** The highest chunk index present in `directory`, or 0 when the directory has no chunk yet. */
export function highestChunkIndex(directory: string): number {
  let highest = 0;
  for (const entry of readdirSync(directory)) {
    const match = CHUNK_FILE_PATTERN.exec(entry);
    if (match) highest = Math.max(highest, Number(match[1] ?? '0'));
  }
  return highest;
}

/**
 * A crash mid-`writeSync` can leave a partial line with no trailing newline at the end of the
 * highest chunk. Appending after it would concatenate the new JSON onto the fragment and
 * corrupt the JSONL for the reader, so truncate the fragment back to the last newline and
 * fsync before the first append. Returns the repaired content so the caller can share it with
 * the sequence scan and the chunk open.
 */
export function truncateTornTail(content: Buffer | null, path: string): Buffer | null {
  if (content === null) return null;
  if (content.byteLength === 0 || content[content.byteLength - 1] === 0x0a) return content;
  const lastNewline = content.lastIndexOf(0x0a);
  // O_NOFOLLOW so a planted chunk symlink is refused.
  const fd = openVerified(path, fsConstants.O_RDWR | O_NOFOLLOW);
  try {
    ftruncateSync(fd, lastNewline + 1);
    fsyncSync(fd);
  } finally {
    closeSync(fd);
  }
  return content.subarray(0, lastNewline + 1);
}

/**
 * The highest sequence already durable on disk. Scans chunks from the highest index down, and
 * within each chunk from the last line backwards, so a torn or corrupt final line falls back to
 * the previous COMPLETE parseable line instead of dropping the whole chunk — otherwise the
 * counter restarts at 1 and reuses durable sequences. The highest chunk's already-read (and
 * repaired) buffer is reused instead of re-read.
 */
export function lastSequenceInChunks(
  directory: string,
  highestChunkIndexOnDisk: number,
  highestContent: Buffer | null,
): number {
  const indices = readdirSync(directory)
    .map((entry) => CHUNK_FILE_PATTERN.exec(entry))
    .filter((match): match is RegExpExecArray => match !== null)
    .map((match) => Number(match[1] ?? '0'))
    .sort((a, b) => b - a);
  for (const index of indices) {
    const content =
      index === highestChunkIndexOnDisk && highestContent !== null
        ? highestContent
        : readChunk(chunkPath(directory, index));
    const lines = content.toString('utf8').split('\n');
    for (let i = lines.length - 1; i >= 0; i -= 1) {
      const line = lines[i];
      if (line === undefined || line.length === 0) continue;
      try {
        const parsed = JSON.parse(line) as { sequence?: unknown };
        // Only a safe integer `>= 1` is a sequence, exactly as the `shards.json` and
        // `checkpoint.json` readers require. `1e300` (a float past the safe range) or a
        // negative/float value is not a sequence this writer may resume after: it would move
        // the counter to an imprecise number and the record would carry it. Such a line is
        // unreadable, and — like the `catch` below for a non-JSON tail — the scan keeps going
        // backwards to the last line that IS a sequence.
        const sequence = parsed.sequence;
        if (typeof sequence === 'number' && Number.isSafeInteger(sequence) && sequence >= 1) {
          return sequence;
        }
      } catch {
        // A torn or corrupt line (crash mid-write) is skipped; keep scanning backwards.
      }
    }
  }
  return 0;
}
