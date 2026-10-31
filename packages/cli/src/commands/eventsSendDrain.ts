// The drain: for `--producer <p>` or every producer directory of the run, read the unacked
// events from `cursor.json`'s `next_unacked_sequence`, pack them into ≤ 500-event / ≤ 1 MiB
// batches with a derived `Idempotency-Key`, and send them with one deadline
// (`EVIDGATE_SEND_DEADLINE`) and jittered backoff. Each ack advances the cursor over the
// *contiguous* prefix only, so a restart resumes at the first unacked sequence and no event is
// skipped or re-sent under a different identity. The command never deletes spool data.
import { setTimeout as delay } from 'node:timers/promises';

import { getToken } from '../auth/getToken.js';
import { deleteCachedToken } from '../auth/tokenCache.js';
import { EnvelopeInvalidError, OversizedEventError, packBatches, type Batch } from '../batcher.js';
import { CliError } from '../errors.js';
import { ExitCode } from '../exitCodes.js';
import { SendFailedError, sendBatches, type SendDeps } from '../sender.js';
import { ProducerSpool, listProducers, runDirBytes } from '../spool.js';

import { capBreach } from './eventsSendErrors.js';

import type { ResolvedConfig } from '../config.js';
import type { ExchangeDeps } from '../auth/exchange.js';
import type { CommandContext } from './index.js';
import type { EventsSendSeams, ProducerReport } from './eventsSendTypes.js';

/** Everything the drain needs; the command has already resolved the deadline and the cap. */
export interface DrainOptions {
  readonly config: ResolvedConfig;
  readonly runId: string;
  readonly onlyProducer: string | undefined;
  readonly spoolDir: string;
  readonly maxBytes: number;
  /** The one send deadline, already resolved by the command's `validateConfig`. */
  readonly sendDeadlineMs: number;
  readonly validate: (line: string, sequence: number) => void;
  readonly io: CommandContext['io'];
  readonly seams: EventsSendSeams;
  readonly deps: ExchangeDeps;
}

/** The drain: one producer at a time, each send under that producer's exclusive lock. */
export async function drain(o: DrainOptions): Promise<ExitCode> {
  const producers =
    o.onlyProducer === undefined ? await listProducers(o.spoolDir, o.runId) : [o.onlyProducer];
  const sendDeps = createSendDeps(o.seams);
  // The token is acquired at most once and only when there is something to send, so a
  // run whose spool is fully acked (or empty) makes no request and no token exchange.
  const currentToken = createTokenSource(o);

  const reports: ProducerReport[] = [];
  for (const producerId of producers) {
    const report = await drainProducer(producerId, o, sendDeps, currentToken);
    if (report === undefined) {
      continue;
    }
    reports.push(report);
    if (!o.config.json) {
      o.io.stdout(
        `evidgate: sent run=${o.runId} producer=${producerId} batches=${report.batches} ` +
          `accepted=${report.accepted} duplicate=${report.duplicates}\n`,
      );
    }
  }

  reportSummary(o, reports);
  return ExitCode.Ok;
}

/** Builds the send's injected effects, falling back to the real clock, timer and fetch. */
function createSendDeps(seams: EventsSendSeams): SendDeps {
  return {
    fetch: seams.fetch ?? fetch,
    sleep: seams.sleep ?? ((milliseconds: number) => delay(milliseconds)),
    now: seams.now ?? Date.now,
    random: seams.random ?? Math.random,
  };
}

/** The lazy token source: one `getToken()` per invocation, and only when a send needs it. */
function createTokenSource(o: DrainOptions): () => Promise<string> {
  let tokenPromise: Promise<string> | undefined;
  return async () => {
    tokenPromise ??= getToken(o.config, o.deps).then((result) => result.token);
    return tokenPromise;
  };
}

/** Drains one producer under its exclusive lock; `undefined` when it has nothing to send. */
async function drainProducer(
  producerId: string,
  o: DrainOptions,
  sendDeps: SendDeps,
  currentToken: () => Promise<string>,
): Promise<ProducerReport | undefined> {
  const spool = await ProducerSpool.open({
    spoolDir: o.spoolDir,
    runId: o.runId,
    producerId,
    maxBytes: o.maxBytes,
  });
  // The range only exists inside the lock; it is kept for the failure hint so an error
  // names the run, the producer and what is still unsent.
  let unsent: { readonly first: number; readonly last: number } | null = null;
  try {
    // A live `writer.lock` makes the drain refuse with `spool_locked` (exit 3) before the
    // range is read, so nothing is acked and nothing is sent.
    const outcome = await spool.withLock(
      async () => {
        const range = await spool.unsentRange();
        unsent = range;
        if (range === null) {
          return undefined;
        }
        return sendProducerLocked(spool, producerId, range, o, sendDeps, currentToken);
      },
      { requireNoLiveWriter: true },
    );
    return outcome === undefined ? undefined : { producerId, ...outcome };
  } catch (error) {
    // The failure names the FULL unsent range as it stands after the send settled
    // (everything from the first unacked sequence to the last durable one), not only the
    // range of the batch that failed. A spool that cannot be re-read (corrupt, removed)
    // falls back to the range read before the send, so the message never loses the hint.
    const settled = await spool.unsentRange().catch(() => unsent);
    throw await mapSendFailure(error, {
      config: o.config,
      runId: o.runId,
      producerId,
      unsent: settled,
    });
  }
}

/**
 * The cap guard and the send, both under the producer's lock: a run directory already above
 * the cap is reported before anything is sent, naming run, unsent range and cap — never a
 * silent drop.
 */
async function sendProducerLocked(
  spool: ProducerSpool,
  producerId: string,
  range: { readonly first: number; readonly last: number },
  o: DrainOptions,
  sendDeps: SendDeps,
  currentToken: () => Promise<string>,
): Promise<{ batches: number; accepted: number; duplicates: number }> {
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
}

/** The invocation's summary: one JSON object, or the "nothing to send" line. */
function reportSummary(o: DrainOptions, reports: readonly ProducerReport[]): void {
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
    return;
  }
  if (reports.length === 0) {
    o.io.stdout(`evidgate: nothing to send run=${o.runId}\n`);
  }
}

/**
 * Sends one producer's unacked events and advances its cursor over the contiguous prefix.
 *
 * `sendBatches` delivers acks in completion order, and with up to four batches in flight
 * that order is not the sequence order. The cursor may only advance over a prefix with no
 * hole, so completed batches are held in a map keyed by their first sequence and
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
 * Maps a send failure to the `CliError` the operator sees, keeping the server's `error.code`
 * when it is a safe identifier and its `request_id` sanitized: nothing here re-words
 * `producer_slot_taken`, `unknown_producer`,
 * `deadline_exceeded` or `transport_failed`. The run, the producer and the range that did not
 * advance are added, because the spool is what the operator has to act on; that range is the
 * FULL unsent range read after the send settled, and the failing batch's own range is only
 * the fallback for a spool that cannot be read again. A `CliError` raised by the spool itself
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
      // Best-effort, like `runs create`: the server's error is the one that must surface.
      // A 403/422 keeps the token, because neither says it is dead.
      await deleteCachedToken(o.config).catch(() => undefined);
    }
    // `first..last` is the settled unsent range, so batches that were never dispatched and
    // acks held behind a gap are covered too; the failing batch's range is used only when
    // the spool could not be read after the failure.
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
          'the spooled line is not a valid event envelope; the server would reject the ' +
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
