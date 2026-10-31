// Reading a chunk line by line, and the two repairs a crash leaves behind: a torn trailing
// fragment is truncated back to the last newline, and a line past the size bound is
// `spool_corrupt` instead of a reason to grow the heap. Neither scan loads a chunk whole.
import { constants as fsConstants } from 'node:fs';
import { basename } from 'node:path';

import { CliError } from './errors.js';
import { SpoolCorruptError } from './spoolErrors.js';
import { openVerifiedFile } from './spoolFs.js';
import { O_NOFOLLOW, maxSpoolLineBytes, maxTailLineBytes, readBlockBytes } from './spoolLimits.js';

import type { FileHandle } from 'node:fs/promises';

/**
 * Truncates a trailing fragment that has no `\n` (a crash inside `write(2)`) back to the
 * last newline and fsyncs, so the JSONL stays line-delimited for the reader. Returns the
 * chunk's new size in bytes.
 */
export async function truncateTornTail(path: string): Promise<number> {
  const handle = await openVerifiedFile(path, fsConstants.O_RDWR | O_NOFOLLOW);
  try {
    const { size } = await handle.stat();
    if (size === 0) return 0;
    const last = Buffer.allocUnsafe(1);
    const tail = await handle.read(last, 0, 1, size - 1);
    if (tail.bytesRead === 1 && last[0] === 0x0a) return size;
    const newline = await findLastNewlineOffset(handle, size);
    const newSize = newline === null ? 0 : newline + 1;
    await handle.truncate(newSize);
    await handle.sync();
    return newSize;
  } finally {
    await handle.close();
  }
}

/** Byte offset of the last `\n` before `size`, or `null` when the file has none. */
async function findLastNewlineOffset(handle: FileHandle, size: number): Promise<number | null> {
  const block = Buffer.allocUnsafe(readBlockBytes);
  let end = size;
  while (end > 0) {
    const start = Math.max(0, end - readBlockBytes);
    const length = end - start;
    const { bytesRead } = await handle.read(block, 0, length, start);
    for (let i = bytesRead - 1; i >= 0; i -= 1) {
      if (block[i] === 0x0a) return start + i;
    }
    end = start;
  }
  return null;
}

/**
 * Counts complete lines of a chunk, streaming (a torn tail was truncated before this).
 * One line is bounded by {@link maxSpoolLineBytes}, so a chunk carrying a longer one is
 * `spool_corrupt` here too, before the appender can decide to write behind it.
 */
export async function countChunkLines(path: string): Promise<number> {
  const chunk = basename(path);
  const handle = await openVerifiedFile(path, fsConstants.O_RDONLY | O_NOFOLLOW);
  try {
    const block = Buffer.allocUnsafe(readBlockBytes);
    let position = 0;
    let count = 0;
    /** Bytes of the current, not yet newline-terminated line seen in earlier blocks. */
    let partial = 0;
    for (;;) {
      const { bytesRead } = await handle.read(block, 0, readBlockBytes, position);
      if (bytesRead === 0) return count;
      position += bytesRead;
      let start = 0;
      for (let i = 0; i < bytesRead; i += 1) {
        if (block[i] !== 0x0a) continue;
        if (partial + (i - start) > maxSpoolLineBytes) {
          throw lineTooLong(chunk, count + 1);
        }
        count += 1;
        partial = 0;
        start = i + 1;
      }
      partial += bytesRead - start;
      if (partial > maxSpoolLineBytes) throw lineTooLong(chunk, count + 1);
    }
  } finally {
    await handle.close();
  }
}

/** `spool_corrupt` for a line that cannot be a legal envelope: it is past every size cap. */
function lineTooLong(chunk: string, lineNumber: number): SpoolCorruptError {
  return new SpoolCorruptError({
    chunk,
    lineNumber,
    reason:
      `line is longer than the ${maxSpoolLineBytes}-byte limit for one envelope ` +
      '(no legal writer produces one, so this is not a torn write)',
  });
}

interface RawLine {
  readonly text: string;
  /** `false` for the final fragment of a file that has no trailing `\n` (a torn write). */
  readonly terminated: boolean;
  readonly lineNumber: number;
}

/**
 * The pieces of a line that straddles a block boundary. Kept as a list (never
 * re-concatenated per block, which made the scan quadratic) and joined once, when the line
 * is complete.
 */
class LineBuilder {
  private pieces: Buffer[] = [];
  private bytes = 0;

  constructor(private readonly chunk: string) {}

  /** A fragment is held for the next block. */
  get pending(): boolean {
    return this.bytes > 0;
  }

  /** One complete line, assembled from the pieces only when it straddled a boundary. */
  complete(view: Buffer, start: number, end: number, lineNumber: number): string {
    // Only a line straddling a block boundary is assembled: the common case is a
    // decode straight out of the block, with no intermediate copy.
    if (this.bytes === 0) {
      return view.toString('utf8', start, end);
    }
    const segment = view.subarray(start, end);
    if (this.bytes + segment.length > maxSpoolLineBytes) {
      throw lineTooLong(this.chunk, lineNumber);
    }
    this.pieces.push(segment);
    const text = Buffer.concat(this.pieces).toString('utf8');
    this.pieces = [];
    this.bytes = 0;
    return text;
  }

  /** Holds the unterminated rest of a block until the next one arrives. */
  hold(rest: Buffer, lineNumber: number): void {
    if (this.bytes + rest.length > maxSpoolLineBytes) {
      throw lineTooLong(this.chunk, lineNumber);
    }
    // Copied: the next block read overwrites the buffer this subarray points into.
    this.pieces.push(Buffer.from(rest));
    this.bytes += rest.length;
  }

  /** The final, unterminated line of the file. */
  tail(): string {
    const only = this.pieces[0];
    return this.pieces.length === 1 && only !== undefined
      ? only.toString('utf8')
      : Buffer.concat(this.pieces).toString('utf8');
  }
}

/**
 * Streams a chunk one line at a time from bounded blocks, so neither the chunk nor the run
 * is ever loaded whole. A line past {@link maxSpoolLineBytes} is `spool_corrupt` naming
 * chunk + line as soon as the limit is crossed, so a chunk with no newline can no longer
 * grow the heap without bound.
 */
export async function* readChunkLines(path: string): AsyncGenerator<RawLine> {
  const chunk = basename(path);
  const handle = await openVerifiedFile(path, fsConstants.O_RDONLY | O_NOFOLLOW);
  try {
    const block = Buffer.allocUnsafe(readBlockBytes);
    const line = new LineBuilder(chunk);
    let position = 0;
    let lineNumber = 0;
    for (;;) {
      const { bytesRead } = await handle.read(block, 0, readBlockBytes, position);
      if (bytesRead === 0) break;
      position += bytesRead;
      const view = block.subarray(0, bytesRead);
      let start = 0;
      for (;;) {
        const newline = view.indexOf(0x0a, start);
        if (newline === -1) break;
        const text = line.complete(view, start, newline, lineNumber + 1);
        lineNumber += 1;
        yield { text, terminated: true, lineNumber };
        start = newline + 1;
      }
      if (start < view.length) line.hold(view.subarray(start), lineNumber + 1);
    }
    if (line.pending) {
      const text = line.tail();
      lineNumber += 1;
      yield { text, terminated: false, lineNumber };
    }
  } finally {
    await handle.close();
  }
}

/**
 * The sequence of a chunk's last newline-terminated line, read from the file tail only, or
 * `null` when that line is missing/unparseable — or longer than {@link maxTailLineBytes},
 * which keeps this fast path cheap; the caller then falls back to the full streaming scan,
 * whose bound is {@link maxSpoolLineBytes}.
 */
export async function lastTerminatedSequence(path: string): Promise<number | null> {
  const handle = await openVerifiedFile(path, fsConstants.O_RDONLY | O_NOFOLLOW);
  try {
    const { size } = await handle.stat();
    if (size === 0) return null;
    const newline = await findLastNewlineOffset(handle, size);
    if (newline === null) return null;
    const lineEnd = newline;
    const previous = lineEnd === 0 ? null : await findLastNewlineOffset(handle, lineEnd);
    const start = previous === null ? 0 : previous + 1;
    const length = lineEnd - start;
    if (length <= 0 || length > maxTailLineBytes) return null;
    const buffer = Buffer.allocUnsafe(length);
    const { bytesRead } = await handle.read(buffer, 0, length, start);
    if (bytesRead !== length) return null;
    const parsed = tryParseLine(buffer.toString('utf8'));
    return parsed === null ? null : parsed.sequence;
  } finally {
    await handle.close();
  }
}

/** The last parseable sequence in a chunk, streaming; `null` when it has none. */
export async function scanLastSequence(path: string): Promise<number | null> {
  let last: number | null = null;
  for await (const raw of readChunkLines(path)) {
    if (!raw.terminated && raw.text.length === 0) continue;
    const parsed = tryParseLine(raw.text);
    if (parsed !== null) last = parsed.sequence;
  }
  return last;
}

interface ParsedSpoolLine {
  readonly sequence: number;
  readonly eventId: string;
}

export function tryParseLine(text: string): ParsedSpoolLine | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    return null;
  }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return null;
  const record: object = parsed;
  const sequence: unknown = 'sequence' in record ? record.sequence : undefined;
  const eventId: unknown = 'event_id' in record ? record.event_id : undefined;
  if (typeof sequence !== 'number' || !Number.isSafeInteger(sequence) || sequence < 0) return null;
  if (typeof eventId !== 'string' || eventId.length === 0) return null;
  return { sequence, eventId };
}

/** Parses a line the reader must trust, or throws `spool_corrupt`. */
export function parseSpoolLine(text: string, chunk: string, lineNumber: number): ParsedSpoolLine {
  const parsed = tryParseLine(text);
  if (parsed === null) {
    throw new SpoolCorruptError({
      chunk,
      lineNumber,
      reason: 'not a JSON envelope with an integer "sequence" and a string "event_id"',
    });
  }
  return parsed;
}

export interface AppendLine {
  readonly text: string;
  readonly lineBytes: number;
  readonly sequence: number;
}

/** Validates one line handed to `append`: one JSON envelope, no line break, a sequence. */
export function parseAppendLine(line: string, index: number): AppendLine {
  const position = index + 1;
  if (typeof line !== 'string' || line.length === 0) {
    throw invalidLine(position, 'is empty');
  }
  if (line.includes('\n') || line.includes('\r')) {
    throw invalidLine(position, 'contains a line break');
  }
  const parsed = tryParseLine(line);
  if (parsed === null) {
    throw invalidLine(
      position,
      'is not a JSON envelope with an integer "sequence" and a string "event_id"',
    );
  }
  return {
    text: line,
    lineBytes: Buffer.byteLength(line, 'utf8') + 1,
    sequence: parsed.sequence,
  };
}

function invalidLine(position: number, reason: string): CliError {
  return new CliError(`spool line ${position} ${reason}`, {
    code: 'invalid_spool_line',
    hint: 'the spool accepts one JSON event envelope per line, without a trailing newline',
  });
}
