// The `--from` intake: stream one JSONL file of event envelopes, validate every line, group
// the lines by their own `producer_id` and append them to the producer's spool under the
// exclusive `spool.lock` — after refusing while the reporter still holds that producer's
// `writer.lock` (`spool_locked`).
//
// The cap is enforced twice: a running byte total during the stream (so memory stays bounded
// by about the cap and an oversized input is refused while it is still being read) and one
// pre-append total over every producer before the first write, so a refusal on that total
// writes nothing at all. Each producer's guard then runs before THAT producer's directory is
// created, so a refused producer leaves none behind, though earlier producers of the same run
// may already be appended; `append` re-checks under the lock.
import { createReadStream } from 'node:fs';

import { CliError } from '../errors.js';
import {
  ProducerSpool,
  SpoolCapExceededError,
  SpoolMissingError,
  maxSpoolLineBytes,
  runDirBytes,
} from '../spool.js';

import { EnvelopeInvalidError } from '../batcher.js';
import { stdinName } from './eventsSendArgs.js';
import { envelopeInvalid, inputError, usageError } from './eventsSendErrors.js';

/** One producer's pending lines, and the sequence range and byte total they cover. */
interface ProducerGroup {
  readonly lines: string[];
  first: number;
  last: number;
  bytes: number;
}

/** What the intake needs; every field is already validated by the command. */
export interface IntakeOptions {
  readonly spoolDir: string;
  readonly runId: string;
  readonly onlyProducer: string | undefined;
  readonly from: string;
  readonly maxBytes: number;
  readonly validate: (line: string, sequence: number) => void;
  readonly stdin: AsyncIterable<Uint8Array>;
}

/** The `--from` intake: read, validate, group by producer, append under the lock. */
export async function intake(o: IntakeOptions): Promise<void> {
  const sourceName = o.from === stdinName ? 'stdin' : o.from;
  // The run directory's size is read once here, and the byte total is kept while the input
  // streams, so the cap is refused as soon as the input could not be spooled. Memory
  // therefore stays bounded by about `maxBytes` instead of by the size of the file or the
  // stdin stream.
  const usedBytes = await runDirBytes(o.spoolDir, o.runId);
  const groups = await readGroups(o, usedBytes, sourceName);
  await appendGroups(groups, o);
}

/**
 * Streams the source into per-producer groups, refusing the cap as soon as the input could
 * not be spooled. The bytes counted are exactly what `append` writes: `line + "\n"` in UTF-8
 * (`SpoolEvent.lineBytes` / `parseAppendLine`).
 */
async function readGroups(
  o: IntakeOptions,
  usedBytes: number,
  sourceName: string,
): Promise<Map<string, ProducerGroup>> {
  const groups = new Map<string, ProducerGroup>();
  let intakeBytes = 0;
  const source = o.from === stdinName ? o.stdin : createReadStream(o.from);
  try {
    for await (const raw of splitLines(source)) {
      intakeBytes = absorbLine(groups, raw, intakeBytes, o, usedBytes);
    }
  } catch (error) {
    if (error instanceof CliError) {
      throw error;
    }
    const code = (error as NodeJS.ErrnoException | undefined)?.code;
    const errno = typeof code === 'string' && /^[A-Z0-9_]{1,32}$/.test(code) ? code : 'unknown';
    throw inputError(`cannot read ${sourceName}: ${errno}`);
  }
  return groups;
}

/**
 * Routes, validates and groups one line, and returns the new running byte total. The order is
 * the rejection order: the line's own routing first, then the envelope verdict, then the
 * gap (`sequence` must advance within its producer), then the cap.
 */
function absorbLine(
  groups: Map<string, ProducerGroup>,
  raw: { readonly text: string; readonly lineNumber: number },
  intakeBytes: number,
  o: IntakeOptions,
  usedBytes: number,
): number {
  const text = raw.text;
  const lineNumber = raw.lineNumber;
  const routed = routeLine(text, lineNumber, o.runId, o.onlyProducer);
  validateLine(o, text, routed.sequence, lineNumber);
  const group = groups.get(routed.producerId);
  if (group !== undefined && routed.sequence <= group.last) {
    throw inputError(
      `--from line ${lineNumber} has sequence ${routed.sequence}, which does not follow ` +
        `${group.last} for producer ${routed.producerId}`,
    );
  }
  const lineBytes = Buffer.byteLength(text, 'utf8') + 1;
  const first = group?.first ?? routed.sequence;
  if (usedBytes + intakeBytes + lineBytes > o.maxBytes) {
    // Nothing has been written yet and nothing will be: the whole intake is refused while
    // the offending line is still the last one held. The range is what was read for that
    // producer so far, so it names the run and the producer the operator has to act on.
    throw new SpoolCapExceededError({
      runId: o.runId,
      producerId: routed.producerId,
      capBytes: o.maxBytes,
      usedBytes,
      requiredBytes: intakeBytes + lineBytes,
      unsentFirstSequence: first,
      unsentLastSequence: routed.sequence,
    });
  }
  const nextIntakeBytes = intakeBytes + lineBytes;
  if (group === undefined) {
    groups.set(routed.producerId, {
      lines: [text],
      first,
      last: routed.sequence,
      bytes: lineBytes,
    });
    return nextIntakeBytes;
  }
  group.lines.push(text);
  group.last = routed.sequence;
  group.bytes += lineBytes;
  return nextIntakeBytes;
}

/** The envelope verdict for one line, re-worded so the failure names the intake line. */
function validateLine(o: IntakeOptions, text: string, sequence: number, lineNumber: number): void {
  try {
    o.validate(text, sequence);
  } catch (error) {
    if (error instanceof EnvelopeInvalidError) {
      throw envelopeInvalid(`--from line ${lineNumber} ${error.message}`);
    }
    throw error;
  }
}

/**
 * One cap check over the TOTAL before the first append, then the per-producer appends. The
 * appends stay per producer, so a failure there can still leave the earlier producers
 * written; `append` re-checks under the lock.
 */
async function appendGroups(groups: Map<string, ProducerGroup>, o: IntakeOptions): Promise<void> {
  const usedBeforeAppend = await runDirBytes(o.spoolDir, o.runId);
  checkTotalCap(groups, usedBeforeAppend, o);
  for (const [producerId, group] of groups) {
    await appendGroup(producerId, group, o);
  }
}

/** Refuses the whole intake when every group together would pass the cap. */
function checkTotalCap(
  groups: Map<string, ProducerGroup>,
  usedBeforeAppend: number,
  o: IntakeOptions,
): void {
  let requiredBytes = 0;
  for (const [producerId, group] of groups) {
    requiredBytes += group.bytes;
    if (usedBeforeAppend + requiredBytes > o.maxBytes) {
      throw new SpoolCapExceededError({
        runId: o.runId,
        producerId,
        capBytes: o.maxBytes,
        usedBytes: usedBeforeAppend,
        requiredBytes,
        unsentFirstSequence: group.first,
        unsentLastSequence: group.last,
      });
    }
  }
}

/** Opens (or creates) one producer's spool, checks it continues, and appends under the lock. */
async function appendGroup(
  producerId: string,
  group: ProducerGroup,
  o: IntakeOptions,
): Promise<void> {
  // The continuation guard runs BEFORE anything is created: a refused intake must leave
  // no directory behind, and `readUnacked` could only report a gap much later as
  // `spool_corrupt`. A producer that does not exist yet starts at sequence 1.
  let existing: ProducerSpool | undefined;
  try {
    existing = await ProducerSpool.open({
      spoolDir: o.spoolDir,
      runId: o.runId,
      producerId,
      maxBytes: o.maxBytes,
    });
  } catch (error) {
    if (!(error instanceof SpoolMissingError)) {
      throw error;
    }
  }
  const expected = existing === undefined ? 1 : await nextExpectedSequence(existing);
  if (group.first !== expected) {
    throw inputError(
      `the first spooled sequence for producer ${producerId} is ${group.first}, but the ` +
        `spool continues at ${expected}`,
    );
  }
  const spool =
    existing ??
    (await ProducerSpool.open({
      spoolDir: o.spoolDir,
      runId: o.runId,
      producerId,
      maxBytes: o.maxBytes,
      createIfMissing: true,
    }));
  // `append` checks the run-directory cap before writing (all-or-nothing per call), so a
  // full spool stays byte-identical and the call raises `spool_cap_exceeded`. The
  // continuation is re-checked under the lock, which is the authoritative read. The
  // appender is also a chunk writer, so it refuses while the reporter still writes:
  // `spool_locked`, exit 3, before a single line is appended.
  await spool.withLock(
    async () => {
      const current = await nextExpectedSequence(spool);
      if (group.first !== current) {
        throw inputError(
          `producer ${producerId} moved to sequence ${current} while the intake was waiting ` +
            `for the lock (the input starts at ${group.first})`,
        );
      }
      await spool.append(group.lines);
    },
    { requireNoLiveWriter: true },
  );
}

/**
 * The next sequence the spool expects for a producer: one past the last durable line, or
 * the cursor's `next_unacked_sequence` when everything on disk is already acked. A first
 * intake that is not this value would leave a gap (or an overlap) that `readUnacked`
 * could only report as `spool_corrupt` later, so it is refused up front.
 */
async function nextExpectedSequence(spool: ProducerSpool): Promise<number> {
  const unsent = await spool.unsentRange();
  if (unsent !== null) {
    return unsent.last + 1;
  }
  const cursor = await spool.cursor();
  return cursor.nextUnackedSequence;
}

/**
 * Streams one JSONL source as lines. Only a bounded amount of text is held: the longest
 * line seen plus one read block. A line above {@link maxSpoolLineBytes} — the spool
 * reader's own bound — is refused as input rather than buffered, and the bound is
 * measured in UTF-8 BYTES, not UTF-16 code units: a multibyte line that fits the unit count
 * but not the byte count would otherwise be spooled and then be `spool_corrupt` for the
 * reader, blocking that producer. A final line without a trailing newline IS accepted here
 * (intake is not the spool reader, whose torn-tail rule drops such a line).
 */
async function* splitLines(
  source: AsyncIterable<Uint8Array>,
): AsyncGenerator<{ readonly text: string; readonly lineNumber: number }> {
  // `fatal` rejects a byte sequence that is not UTF-8 instead of silently replacing it
  // with U+FFFD, which would make an invalid envelope look like a different one.
  const decoder = new TextDecoder('utf-8', { fatal: true });
  let buffered = '';
  // The UTF-8 size of `buffered`, kept incrementally so the guard costs O(bytes) once
  // instead of re-encoding the pending line on every read block. Decoding is lossless
  // (fatal), so a chunk's byte length equals its decoded text's byte length, and a yielded
  // line removes exactly `text + "\n"` from the pending total.
  let bufferedBytes = 0;
  let lineNumber = 0;
  for await (const chunk of source) {
    let decoded: string;
    try {
      decoded = decoder.decode(chunk, { stream: true });
    } catch {
      throw inputError(`line ${lineNumber + 1} is not valid UTF-8`);
    }
    buffered += decoded;
    bufferedBytes += Buffer.byteLength(decoded, 'utf8');
    for (;;) {
      const newline = buffered.indexOf('\n');
      if (newline === -1) {
        break;
      }
      const text = buffered.slice(0, newline);
      buffered = buffered.slice(newline + 1);
      const textBytes = Buffer.byteLength(text, 'utf8');
      bufferedBytes -= textBytes + 1;
      lineNumber += 1;
      // A line that completes inside the block that pushed it past the bound never sits in
      // `buffered` at the end of the block, so the pending check below cannot see it: the
      // completed line is measured here, before it can be yielded and spooled.
      if (textBytes > maxSpoolLineBytes) {
        throw inputError(`line ${lineNumber} is longer than ${maxSpoolLineBytes} bytes`);
      }
      const line = text.endsWith('\r') ? text.slice(0, -1) : text;
      yield { text: line, lineNumber };
    }
    if (bufferedBytes > maxSpoolLineBytes) {
      throw inputError(`line ${lineNumber + 1} is longer than ${maxSpoolLineBytes} bytes`);
    }
  }
  try {
    buffered += decoder.decode();
  } catch {
    throw inputError(`line ${lineNumber + 1} is not valid UTF-8`);
  }
  bufferedBytes = Buffer.byteLength(buffered, 'utf8');
  if (bufferedBytes > maxSpoolLineBytes) {
    throw inputError(`line ${lineNumber + 1} is longer than ${maxSpoolLineBytes} bytes`);
  }
  if (buffered.length > 0) {
    lineNumber += 1;
    yield { text: buffered.endsWith('\r') ? buffered.slice(0, -1) : buffered, lineNumber };
  }
}

/**
 * Reads the routing members of one `--from` line: `producer_id` (the spool directory it
 * belongs to) and `sequence` (the envelope's place in it). A line that belongs to another
 * run or to another producer than `--producer` is a `usage` error, because the operator
 * pointed the command at the wrong file — nothing is spooled in that case.
 */
function routeLine(
  text: string,
  lineNumber: number,
  runId: string,
  onlyProducer: string | undefined,
): { readonly producerId: string; readonly sequence: number } {
  if (text.trim().length === 0) {
    throw inputError(`--from line ${lineNumber} is empty`);
  }
  let document: unknown;
  try {
    document = JSON.parse(text);
  } catch {
    throw envelopeInvalid(`--from line ${lineNumber} is not JSON`);
  }
  if (typeof document !== 'object' || document === null || Array.isArray(document)) {
    throw envelopeInvalid(`--from line ${lineNumber} is not a JSON event envelope`);
  }
  // `document` is narrowed to `object` here; the index-signature view is what the member
  // readers below need, and every value read from it is re-validated.
  const record = document as Record<string, unknown>;
  const sequence: unknown = record['sequence'];
  if (typeof sequence !== 'number' || !Number.isSafeInteger(sequence) || sequence < 0) {
    throw envelopeInvalid(`--from line ${lineNumber} has no integer "sequence"`);
  }
  const producerId: unknown = record['producer_id'];
  if (typeof producerId !== 'string' || producerId.length === 0) {
    throw envelopeInvalid(`--from line ${lineNumber} has no string "producer_id"`);
  }
  // The envelope schema has no `run_id` member, but a producer that adds one has said
  // which run it meant: a mismatch is the operator's mistake, not a spool problem.
  const envelopeRun: unknown = record['run_id'];
  if (envelopeRun !== undefined && envelopeRun !== runId) {
    throw usageError(`--from line ${lineNumber} belongs to another run than --run ${runId}`);
  }
  if (onlyProducer !== undefined && producerId !== onlyProducer) {
    throw usageError(
      `--from line ${lineNumber} belongs to producer ${JSON.stringify(producerId)}, ` +
        `not --producer ${onlyProducer}`,
    );
  }
  return { producerId, sequence };
}
