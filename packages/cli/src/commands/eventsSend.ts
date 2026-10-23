// 04.2.3-T1.4: `evidgate events send` — the CLI's send path (04.2.3 §1, §2 AC1–AC4,
// §5; contracts/cli.md §11). One invocation does up to two things in this order:
//
// 1. **Intake** (`--from <file|->`): stream one JSONL file of 00 §9.4 envelopes, validate
//    every line against the shared Ajv envelope schema, group the lines by their own
//    `producer_id` and append them to the producer's spool under the exclusive
//    `spool.lock` (D2). The cap is enforced twice: a running byte total during the stream
//    (so memory stays bounded by about the cap and an oversized input is refused while it
//    is still being read) and one pre-append total over every producer before the first
//    write, so a refused intake writes nothing at all (CR2/CR3). `append` re-checks under
//    the lock; the failure is `spool_cap_exceeded` (exit 3) naming run, producer and range.
// 2. **Drain**: for `--producer <p>` or every producer directory of the run, read the
//    unacked events from `cursor.json`'s `next_unacked_sequence`, pack them into
//    ≤ 500-event / ≤ 1 MiB batches with a derived `Idempotency-Key`, and send them with
//    one deadline (`EVIDGATE_SEND_DEADLINE`) and jittered backoff. Each ack advances the
//    cursor over the *contiguous* prefix only, so a restart resumes at the first unacked
//    sequence and no event is skipped or re-sent under a different identity (AC1/AC2).
//
// The command never deletes spool data: acked chunks stay until `runs finalize` purges
// the producer directory (D2/D7). A 401 purges the cached CI token (the `runs create`
// rule, contracts/cli.md §7); 403/422 and every other 4xx keep it and surface the
// server's `error.code` + `request_id` verbatim (T9).
//
// Every flag is validated BEFORE `getToken()`, so a bad flag makes no request and no
// token exchange at all (04.2.2 §8/§9 `runs create` rule). Each option is single-use
// (`multiple: true`), so a repetition is refused as `usage` instead of silently taking
// the last value.
import { createReadStream } from 'node:fs';
import { setTimeout as delay } from 'node:timers/promises';

import { getToken } from '../auth/getToken.js';
import { deleteCachedToken } from '../auth/tokenCache.js';
import {
  EnvelopeInvalidError,
  OversizedEventError,
  createEnvelopeValidator,
  packBatches,
  type Batch,
} from '../batcher.js';
import { resolveSpoolDir, spoolDirMissingError, type ResolvedConfig } from '../config.js';
import { CliError } from '../errors.js';
import { ExitCode } from '../exitCodes.js';
import { SendFailedError, sendBatches, type SendDeps } from '../sender.js';
import {
  ProducerSpool,
  SpoolCapExceededError,
  SpoolMissingError,
  assertValidSpoolId,
  listProducers,
  maxSpoolLineBytes,
  runDirBytes,
} from '../spool.js';

import type { ExchangeDeps } from '../auth/exchange.js';
import type { CommandDefinition, CommandContext, ParsedValues } from './index.js';

/**
 * The usage-error hint `main.ts` attaches to its parse-level usage errors (`helpHint`
 * there, module-private). Repeated here so the command-level flag usage errors carry the
 * same pointer (contracts/cli.md §5 `usage` row); keep the two literals in sync.
 */
const helpHint = "run 'evidgate --help' for usage";
/** `--from -` reads the envelopes from stdin. */
const stdinName = '-';
/**
 * openapi `RunId`: the path parameter `r` of `POST /v1/runs/{r}/events` and the `run_id`
 * the reporter writes `<spool_dir>/<run_id>/…` with. Validated here exactly like
 * `runs finalize` does, so the two spool commands agree on the same flag.
 */
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
/** The producer id shape every producer-writing command accepts (same as `runs finalize`). */
const producerPattern = /^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$/;

/**
 * The seams `main` forwards (`ExchangeDeps`) plus the ones a deadline-bounded send needs
 * (04.2.3 contract §sender.ts `SendDeps`) and the stdin surrogate for `--from -`. All are
 * optional, so the production path falls back to the real clock, `Math.random`, the
 * global `fetch`, a real timer and `process.stdin`; a spec injects each of them, which is
 * what makes AC1's ten-minute outage and AC2's restart deterministic (no real waiting).
 */
interface EventsSendSeams extends ExchangeDeps {
  readonly now?: () => number;
  readonly random?: () => number;
  readonly stdin?: AsyncIterable<Uint8Array>;
}

/** What one producer contributed to the report; only producers with work appear. */
interface ProducerReport {
  readonly producerId: string;
  readonly batches: number;
  readonly accepted: number;
  readonly duplicates: number;
}

export const eventsSendCommand: CommandDefinition = {
  name: 'events send',
  summary: 'send the spooled event envelopes of a run',
  requireToken: true,
  // D5/CR8: the spool root is required and refused here, before the `--verbose` dump and
  // before any flag, token or filesystem work (main runs `validateConfig` right after
  // `resolveConfig`). Forcing `spoolSettings()` is also what makes a malformed
  // `EVIDGATE_SPOOL_MAX_BYTES` or `EVIDGATE_SEND_DEADLINE` an `invalid_config` for THIS
  // command only: the surface is lazy, so `runs create` and the other non-spool commands
  // never read those variables at all.
  validateConfig: (config) => {
    if (config.spoolSettings().spoolDir === undefined) {
      throw spoolDirMissingError();
    }
  },
  options: {
    // `multiple: true` is what makes a repetition observable: with the default `false`
    // `parseArgs` keeps the last value and reports nothing, so `--run a --run b` would
    // quietly send `b`.
    run: { type: 'string', multiple: true },
    producer: { type: 'string', multiple: true },
    from: { type: 'string', multiple: true },
  },
  run: (context) => runEventsSend(context),
};

async function runEventsSend(context: CommandContext): Promise<ExitCode> {
  const { config, io, values, env, deps } = context;
  const seams = deps as EventsSendSeams;

  // Flags first, token second: an invalid flag reaches neither the token exchange nor the
  // spool. Both ids become path segments verbatim (S3), so the spool-id rules are applied
  // in addition to the wire shapes.
  const runId = requiredFlag(values, 'run');
  if (!uuidPattern.test(runId)) {
    throw usageError('--run must be a UUID');
  }
  assertValidSpoolId('run_id', runId);
  const onlyProducer = flagValue(values, 'producer');
  if (onlyProducer !== undefined) {
    if (!producerPattern.test(onlyProducer)) {
      throw usageError('--producer must be 1..128 of [A-Za-z0-9_.-], starting alphanumeric');
    }
    assertValidSpoolId('producer_id', onlyProducer);
  }
  const from = flagValue(values, 'from');
  if (from !== undefined && from.trim().length === 0) {
    throw usageError(`--from must be a file path or ${stdinName}`);
  }

  // Configuration before the token too (D5): `validateConfig` already forced the lazy
  // settings, so this is a memoised read and cannot re-parse or re-fail (CR8).
  const settings = config.spoolSettings();
  const spoolDir = settings.spoolDir ?? resolveSpoolDir(env);
  const maxBytes = settings.spoolMaxBytes;
  // One Ajv compile for the whole invocation: the validator is stateless, and both the
  // intake and the drain must apply exactly the same verdicts as the server (T42).
  const validate = createEnvelopeValidator();

  if (from !== undefined) {
    await intake({
      spoolDir,
      runId,
      onlyProducer,
      from,
      maxBytes,
      validate,
      stdin: seams.stdin ?? process.stdin,
    });
  }

  return drain({
    config,
    runId,
    onlyProducer,
    spoolDir,
    maxBytes,
    sendDeadlineMs: settings.sendDeadlineMs,
    validate,
    io,
    seams,
    deps,
  });
}

/** The `--from` intake: read, validate, group by producer, append under the lock. */
async function intake(o: {
  readonly spoolDir: string;
  readonly runId: string;
  readonly onlyProducer: string | undefined;
  readonly from: string;
  readonly maxBytes: number;
  readonly validate: (line: string, sequence: number) => void;
  readonly stdin: AsyncIterable<Uint8Array>;
}): Promise<void> {
  const sourceName = o.from === stdinName ? 'stdin' : o.from;
  const groups = new Map<
    string,
    { readonly lines: string[]; first: number; last: number; bytes: number }
  >();
  // CR2/PF3/SC2: the run directory's size is read once here, and the byte total is kept
  // while the input streams, so the cap is refused as soon as the input could not be
  // spooled. Memory therefore stays bounded by about `maxBytes` instead of by the size of
  // the file or the stdin stream. The bytes counted are exactly what `append` writes:
  // `line + "\n"` in UTF-8 (`SpoolEvent.lineBytes` / `parseAppendLine`).
  const usedBytes = await runDirBytes(o.spoolDir, o.runId);
  let intakeBytes = 0;

  const source = o.from === stdinName ? o.stdin : createReadStream(o.from);
  try {
    for await (const raw of splitLines(source)) {
      const text = raw.text;
      const lineNumber = raw.lineNumber;
      const routed = routeLine(text, lineNumber, o.runId, o.onlyProducer);
      try {
        o.validate(text, routed.sequence);
      } catch (error) {
        if (error instanceof EnvelopeInvalidError) {
          throw envelopeInvalid(`--from line ${lineNumber} ${error.message}`);
        }
        throw error;
      }
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
        // Nothing has been written yet and nothing will be: the whole intake is refused
        // while the offending line is still the last one held (CR2). The range is what
        // was read for that producer so far, so it names the run and the producer the
        // operator has to act on.
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
      intakeBytes += lineBytes;
      if (group === undefined) {
        groups.set(routed.producerId, {
          lines: [text],
          first,
          last: routed.sequence,
          bytes: lineBytes,
        });
        continue;
      }
      group.lines.push(text);
      group.last = routed.sequence;
      group.bytes += lineBytes;
    }
  } catch (error) {
    if (error instanceof CliError) {
      throw error;
    }
    const code = (error as NodeJS.ErrnoException | undefined)?.code;
    const errno = typeof code === 'string' && /^[A-Z0-9_]{1,32}$/.test(code) ? code : 'unknown';
    throw inputError(`cannot read ${sourceName}: ${errno}`);
  }

  // CR3: ONE cap check over the TOTAL of every producer before the first append, so the
  // intake is all-or-nothing across producers. Without it, a run directory that grew
  // while the input streamed could have producer A appended and then fail on producer B,
  // leaving a half-applied intake that the same `--from` file could not be replayed into.
  // `append` re-checks per producer under the lock, which stays the authority for a
  // spool that another process grows concurrently.
  const usedBeforeAppend = await runDirBytes(o.spoolDir, o.runId);
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

  for (const [producerId, group] of groups) {
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
    // `append` checks the run-directory cap before writing (all-or-nothing per call), so
    // a full spool leaves the spool byte-identical and raises `spool_cap_exceeded` (AC3).
    // The continuation is re-checked under the lock, which is the authoritative read.
    await spool.withLock(async () => {
      const current = await nextExpectedSequence(spool);
      if (group.first !== current) {
        throw inputError(
          `producer ${producerId} moved to sequence ${current} while the intake was waiting ` +
            `for the lock (the input starts at ${group.first})`,
        );
      }
      await spool.append(group.lines);
    });
  }
}

/** The drain: one producer at a time, each send under that producer's exclusive lock. */
async function drain(o: {
  readonly config: ResolvedConfig;
  readonly runId: string;
  readonly onlyProducer: string | undefined;
  readonly spoolDir: string;
  readonly maxBytes: number;
  /** The one send deadline (D5), already resolved by the command's `validateConfig`. */
  readonly sendDeadlineMs: number;
  readonly validate: (line: string, sequence: number) => void;
  readonly io: CommandContext['io'];
  readonly seams: EventsSendSeams;
  readonly deps: ExchangeDeps;
}): Promise<ExitCode> {
  const producers =
    o.onlyProducer === undefined ? await listProducers(o.spoolDir, o.runId) : [o.onlyProducer];
  const sendDeps: SendDeps = {
    fetch: o.seams.fetch ?? fetch,
    sleep: o.seams.sleep ?? ((milliseconds: number) => delay(milliseconds)),
    now: o.seams.now ?? Date.now,
    random: o.seams.random ?? Math.random,
  };
  // The token is acquired at most once and only when there is something to send, so a
  // run whose spool is fully acked (or empty) makes no request and no token exchange.
  let tokenPromise: Promise<string> | undefined;
  const currentToken = async (): Promise<string> => {
    tokenPromise ??= getToken(o.config, o.deps).then((result) => result.token);
    return tokenPromise;
  };

  const reports: ProducerReport[] = [];
  for (const producerId of producers) {
    const spool = await ProducerSpool.open({
      spoolDir: o.spoolDir,
      runId: o.runId,
      producerId,
      maxBytes: o.maxBytes,
    });
    // The range only exists inside the lock; it is kept for the failure hint so an error
    // names the run, the producer and what is still unsent (AC3, T9 surfacing).
    let unsent: { readonly first: number; readonly last: number } | null = null;
    try {
      const outcome = await spool.withLock(async () => {
        const range = await spool.unsentRange();
        unsent = range;
        if (range === null) {
          return undefined;
        }
        // D4: a run directory already above the cap is reported before anything is sent,
        // naming run, unsent range and cap — never a silent drop.
        const usedBytes = await runDirBytes(o.spoolDir, o.runId);
        if (usedBytes > o.maxBytes) {
          throw capBreach({
            runId: o.runId,
            producerId,
            usedBytes,
            capBytes: o.maxBytes,
            first: range.first,
            last: range.last,
          });
        }
        return sendWithin(spool, {
          config: o.config,
          runId: o.runId,
          producerId,
          token: await currentToken(),
          deadlineMs: o.sendDeadlineMs,
          validate: o.validate,
          deps: sendDeps,
        });
      });
      if (outcome !== undefined) {
        reports.push({ producerId, ...outcome });
        if (!o.config.json) {
          o.io.stdout(
            `evidgate: sent run=${o.runId} producer=${producerId} batches=${outcome.batches} ` +
              `accepted=${outcome.accepted} duplicate=${outcome.duplicates}\n`,
          );
        }
      }
    } catch (error) {
      // CR7: the failure names the FULL unsent range as it stands after the send settled
      // (everything from the first unacked sequence to the last durable one), not only the
      // range of the batch that failed. A spool that cannot be re-read (corrupt, removed)
      // falls back to the range read before the send, so the message never loses the hint.
      const settled = await spool.unsentRange().catch(() => unsent);
      const mapped = await mapSendFailure(error, {
        config: o.config,
        runId: o.runId,
        producerId,
        unsent: settled,
      });
      throw mapped;
    }
  }

  const batches = reports.reduce((sum, report) => sum + report.batches, 0);
  const accepted = reports.reduce((sum, report) => sum + report.accepted, 0);
  const duplicates = reports.reduce((sum, report) => sum + report.duplicates, 0);
  if (o.config.json) {
    o.io.stdout(
      `${JSON.stringify({
        run_id: o.runId,
        producers: reports.map((report) => ({
          producer_id: report.producerId,
          batches: report.batches,
          accepted: report.accepted,
          duplicates: report.duplicates,
        })),
        batches,
        accepted,
        duplicates,
      })}\n`,
    );
  } else if (reports.length === 0) {
    o.io.stdout(`evidgate: nothing to send run=${o.runId}\n`);
  }
  return ExitCode.Ok;
}

/**
 * Sends one producer's unacked events and advances its cursor over the contiguous prefix.
 *
 * `sendBatches` delivers acks in completion order, and with up to four batches in flight
 * that order is not the sequence order. The cursor may only advance over a prefix with no
 * hole (D1/D7), so completed batches are held in a map keyed by their first sequence and
 * the cursor is advanced while the expected sequence is present. An ack that completes
 * behind a gap is therefore durable in the map and applied as soon as the gap fills, and
 * the `ack` call itself is monotonic and re-checks the sequence on disk.
 */
async function sendWithin(
  spool: ProducerSpool,
  o: {
    readonly config: ResolvedConfig;
    readonly runId: string;
    readonly producerId: string;
    readonly token: string;
    readonly deadlineMs: number;
    readonly validate: (line: string, sequence: number) => void;
    readonly deps: SendDeps;
  },
): Promise<{ batches: number; accepted: number; duplicates: number }> {
  const cursor = await spool.cursor();
  let expectedFirst = cursor.nextUnackedSequence;
  const completed = new Map<number, { readonly last: number; readonly count: number }>();

  const onBatchAcked = async (batch: Batch): Promise<void> => {
    completed.set(batch.firstSequence, {
      last: batch.lastSequence,
      count: batch.events.length,
    });
    let upTo = -1;
    let added = 0;
    for (;;) {
      const entry = completed.get(expectedFirst);
      if (entry === undefined) {
        break;
      }
      completed.delete(expectedFirst);
      upTo = entry.last;
      added += entry.count;
      expectedFirst = entry.last + 1;
    }
    if (added > 0) {
      await spool.ack(upTo, added);
    }
  };

  const batches = packBatches(spool.readUnacked(), {
    runId: o.runId,
    producerId: o.producerId,
    validate: o.validate,
  });
  return sendBatches(
    batches,
    {
      config: o.config,
      token: o.token,
      runId: o.runId,
      deadlineMs: o.deadlineMs,
    },
    o.deps,
    onBatchAcked,
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
 * Maps a send failure to the `CliError` the operator sees, keeping the server's
 * `error.code` and `request_id` verbatim (T9): nothing here re-words
 * `producer_slot_taken`, `unknown_producer`, `deadline_exceeded` or `transport_failed`.
 * The run, the producer and the range that did not advance are added, because the spool
 * is what the operator has to act on; that range is the FULL unsent range read after the
 * send settled (CR7), and the failing batch's own range is only the fallback for a spool
 * that cannot be read again. A `CliError` raised by the spool itself
 * (`spool_cap_exceeded`, `spool_corrupt`, `spool_locked`, `spool_missing`, …) is already
 * precise and is returned unchanged.
 */
async function mapSendFailure(
  error: unknown,
  o: {
    readonly config: ResolvedConfig;
    readonly runId: string;
    readonly producerId: string;
    readonly unsent: { readonly first: number; readonly last: number } | null;
  },
): Promise<CliError> {
  if (error instanceof CliError) {
    return error;
  }
  if (error instanceof SendFailedError) {
    if (error.status === 401) {
      // Best-effort, like `runs create`: the server's error is the one that must surface
      // (04.2.1 §6). A 403/422 keeps the token, because neither says it is dead.
      await deleteCachedToken(o.config).catch(() => undefined);
    }
    // CR7: `first..last` is the settled unsent range, so batches that were never
    // dispatched and acks held behind a gap are covered too; the failing batch's range is
    // used only when the spool could not be read after the failure.
    const first = o.unsent === null ? error.firstSequence : o.unsent.first;
    const last = o.unsent === null ? error.lastSequence : o.unsent.last;
    return new CliError(
      `${error.message} (run ${o.runId}, producer ${o.producerId}, ` +
        `unsent sequences ${first}..${last})`,
      {
        code: error.code,
        ...(error.requestId === undefined ? {} : { requestId: error.requestId }),
        hint:
          `the cursor did not advance past sequence ${last}; ` +
          `re-run 'evidgate events send --run ${o.runId}' once the cause is fixed`,
      },
    );
  }
  if (error instanceof OversizedEventError) {
    return new CliError(
      `run ${o.runId} producer ${o.producerId}: ${error.message}${unsentSuffix(o.unsent)}`,
      {
        code: 'event_too_large',
        hint:
          'split the event (its ext payload is the usual cause) so one batch body stays ' +
          'within 1 MiB',
      },
    );
  }
  if (error instanceof EnvelopeInvalidError) {
    return new CliError(
      `run ${o.runId} producer ${o.producerId}: ${error.message}${unsentSuffix(o.unsent)}`,
      {
        code: 'envelope_invalid',
        hint:
          'the spooled line is not a valid 00 §9.4 envelope; the server would reject the ' +
          'whole batch',
      },
    );
  }
  throw error;
}

/**
 * The still-unsent range, appended to a failure that happened after the range was read.
 * An empty/fully acked spool adds nothing, so the message never claims a phantom range.
 */
function unsentSuffix(range: { readonly first: number; readonly last: number } | null): string {
  return range === null ? '' : ` (unsent sequences ${range.first}..${range.last})`;
}

/**
 * Streams one JSONL source as lines. Only a bounded amount of text is held: the longest
 * line seen plus one read block. A line above {@link maxSpoolLineBytes} — the spool
 * reader's own bound (S54 N3) — is refused as input rather than buffered, and the bound is
 * measured in UTF-8 BYTES, not UTF-16 code units: a multibyte line that fits the unit count
 * but not the byte count would otherwise be spooled and then be `spool_corrupt` for the
 * reader, blocking that producer. A final line without a trailing newline IS accepted here
 * (intake is not the spool reader, whose torn-tail rule is D3).
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
      bufferedBytes -= Buffer.byteLength(text, 'utf8') + 1;
      lineNumber += 1;
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

/**
 * The D4 cap failure found before sending: the run directory is already over
 * `EVIDGATE_SPOOL_MAX_BYTES`, so nothing can be appended and the drain stops rather than
 * send part of a spool it cannot grow. The message names the run, the bytes in use, the
 * cap and the unsent range of the producer being drained (AC3).
 */
function capBreach(o: {
  readonly runId: string;
  readonly producerId: string;
  readonly usedBytes: number;
  readonly capBytes: number;
  readonly first: number;
  readonly last: number;
}): CliError {
  return new CliError(
    `spool cap exceeded for run ${o.runId}: ${o.usedBytes} bytes in use > ${o.capBytes} bytes ` +
      `(EVIDGATE_SPOOL_MAX_BYTES); unsent sequences ${o.first}..${o.last} of producer ` +
      `${o.producerId}`,
    {
      code: 'spool_cap_exceeded',
      hint:
        `raise EVIDGATE_SPOOL_MAX_BYTES (currently ${o.capBytes}), or send and finalize the ` +
        'spooled events before queueing more',
    },
  );
}

/** A `--from` input problem: unreadable, not JSON, a gap, a duplicate or an odd line. */
function inputError(message: string): CliError {
  return new CliError(message, {
    code: 'invalid_spool_input',
    hint: 'the intake reads one JSON event envelope per line, in ascending sequence order',
  });
}

/** A line the envelope schema refuses (or that is not an envelope at all). */
function envelopeInvalid(message: string): CliError {
  return new CliError(message, {
    code: 'envelope_invalid',
    hint: 'each spool line must be one 00 §9.4 event envelope',
  });
}

function usageError(message: string): CliError {
  return new CliError(message, { code: 'usage', hint: helpHint });
}

/**
 * The single value of one option, or `undefined` when it was absent. Every option is
 * declared `multiple: true`, so `parseArgs` reports a repetition as an array instead of
 * silently keeping the last value; two or more entries (or a non-string entry) are a
 * `usage` error that names the flag and never its value.
 */
function flagValue(values: ParsedValues, flag: string): string | undefined {
  const value = values[flag];
  if (value === undefined) {
    return undefined;
  }
  if (typeof value === 'string') {
    return value;
  }
  if (!Array.isArray(value)) {
    throw usageError(`--${flag} must be a single value`);
  }
  if (value.length !== 1) {
    throw usageError(`--${flag} may be given only once`);
  }
  const [only] = value;
  if (typeof only !== 'string') {
    throw usageError(`--${flag} must be a single value`);
  }
  return only;
}

/** A required string option; absent is a `usage` error, a repetition is refused. */
function requiredFlag(values: ParsedValues, flag: string): string {
  const value = flagValue(values, flag);
  if (value === undefined) {
    throw usageError(`missing required flag --${flag}`);
  }
  if (value.length === 0) {
    throw usageError(`--${flag} must be a single, non-empty value`);
  }
  return value;
}
