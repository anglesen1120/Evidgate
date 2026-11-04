// Shared scripted transport for the sender specs: a recording `fetch`, a virtual clock behind
// `sleep`/`now`, and the request, batch and ack builders the specs answer with. Nothing here
// touches the network or waits on a real timer, so the specs stay deterministic.
import type { Batch } from './batcher.js';
import { resolveConfig } from './config.js';
import { SendFailedError, type SendDeps, type SendOptions } from './sender.js';
import type { SpoolEvent } from './spool.js';

/** Assembled at runtime: the spec never stores a secret literal. */
export const token = ['tk', 'producer', 'value'].join('-');
export const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
const config = resolveConfig({
  env: { EVIDGATE_API_URL: 'http://127.0.0.1:9', EVIDGATE_PROJECT: 'proj-1' },
});
const defaultDeadlineMs = 30 * 60 * 1000;

interface CapturedCall {
  readonly request: Request;
  readonly body: Buffer;
  readonly key: string | null;
}

export interface Harness {
  readonly deps: SendDeps;
  readonly calls: CapturedCall[];
  readonly sleeps: number[];
  readonly clock: { now: number };
  /** Highest number of requests the responder saw overlapping. */
  readonly peak: number;
}

type Responder = (call: CapturedCall) => Response | Promise<Response>;

/** A scripted transport: it records every request and answers from `respond`. */
export function harness(respond: Responder, random: () => number = () => 0.5): Harness {
  const calls: CapturedCall[] = [];
  const sleeps: number[] = [];
  const clock = { now: 1_000 };
  const state = { peak: 0 };
  let active = 0;

  const fetchImpl: typeof fetch = async (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    const call: CapturedCall = {
      request,
      body: Buffer.from(await request.clone().arrayBuffer()),
      key: request.headers.get('idempotency-key'),
    };
    calls.push(call);
    active += 1;
    state.peak = Math.max(state.peak, active);
    try {
      return await respond(call);
    } finally {
      active -= 1;
    }
  };

  return {
    calls,
    sleeps,
    clock,
    get peak(): number {
      return state.peak;
    },
    deps: {
      fetch: fetchImpl,
      sleep: (milliseconds) => {
        sleeps.push(milliseconds);
        clock.now += milliseconds;
        return Promise.resolve();
      },
      now: () => clock.now,
      random,
    },
  };
}

export function sendOptions(
  overrides: { deadlineMs?: number; maxInFlight?: number } = {},
): SendOptions {
  return {
    config,
    token,
    runId,
    deadlineMs: overrides.deadlineMs ?? defaultDeadlineMs,
    ...(overrides.maxInFlight === undefined ? {} : { maxInFlight: overrides.maxInFlight }),
  };
}

export function jsonResponse(
  status: number,
  body: unknown,
  headers: Record<string, string> = {},
): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json', ...headers },
  });
}

/** An error envelope: `code`/`request_id` must reach the caller verbatim. */
export function refusal(
  status: number,
  code: string,
  options: { requestId?: string; retryable?: boolean; retryAfter?: string } = {},
): Response {
  const headers: Record<string, string> = {};
  if (options.retryAfter !== undefined) {
    headers['retry-after'] = options.retryAfter;
  }
  return jsonResponse(
    status,
    {
      error: {
        code,
        message: `the server refused the batch with ${code}`,
        request_id: options.requestId ?? 'req-1',
        retryable: options.retryable ?? false,
      },
    },
    headers,
  );
}

/** The `event_id`s the batch body carries (the sender never re-serialises them). */
export function eventIdsOf(body: Buffer): string[] {
  const parsed = JSON.parse(body.toString('utf8')) as { events: { event_id: string }[] };
  return parsed.events.map((event) => event.event_id);
}

interface SentAck {
  readonly event_id: string;
  readonly ack: string;
}

interface AckEntry {
  readonly index: number;
  readonly event_id: string;
  readonly ack: string;
}

interface AckPayload {
  readonly batch_id: string;
  readonly accepted: number;
  readonly duplicates: number;
  readonly acks: readonly AckEntry[];
}

/** One 200 body, in request order; `ack` is left a plain string so a spec can lie. */
export function ackBody(acks: readonly SentAck[]): AckPayload {
  return {
    batch_id: '00000000-0000-4000-8000-000000000001',
    accepted: acks.filter((ack) => ack.ack === 'accepted').length,
    duplicates: acks.filter((ack) => ack.ack === 'duplicate').length,
    acks: acks.map((ack, index) => ({ index, ...ack })),
  };
}

/** A correct 200 for the batch whose bytes were posted. */
export function acked(
  body: Buffer,
  mode: 'accepted' | 'duplicate' | 'mixed' = 'accepted',
): Response {
  const acks = eventIdsOf(body).map((event_id, index) => ({
    event_id,
    ack: mode === 'mixed' ? (index % 2 === 0 ? 'accepted' : 'duplicate') : mode,
  }));
  return jsonResponse(200, ackBody(acks));
}

/** A batch as the batcher builds it: pre-serialised bytes plus the batch key. */
export function makeBatch(eventIds: readonly string[], label: string, gzip = false): Batch {
  const events: SpoolEvent[] = eventIds.map((eventId, index) => ({
    sequence: index + 1,
    eventId,
    line: JSON.stringify({ event_id: eventId, sequence: index + 1 }),
    lineBytes: 1,
    chunk: '000001.jsonl',
    lineNumber: index + 1,
  }));
  const body = Buffer.from(
    JSON.stringify({
      events: events.map((event) => ({ event_id: event.eventId, sequence: event.sequence })),
    }),
    'utf8',
  );
  const batch = {
    events,
    firstSequence: 1,
    lastSequence: events.length,
    body,
    idempotencyKey: `batch:${label}`,
  };
  // The batcher decides gzip; the sender only forwards these bytes with the header.
  return gzip ? { ...batch, gzipBody: Buffer.from([0x1f, 0x8b, 0x08, 0x00]) } : batch;
}

export async function* batchStream(batches: readonly Batch[]): AsyncGenerator<Batch> {
  await Promise.resolve();
  yield* batches;
}

/** The `SendFailedError` a send raised; any other outcome fails the spec. */
export async function rejection(pending: Promise<unknown>): Promise<SendFailedError> {
  try {
    await pending;
  } catch (error) {
    if (error instanceof SendFailedError) {
      return error;
    }
    throw error;
  }
  throw new Error('expected the send to fail');
}

/**
 * One macrotask turn (no timer): every microtask already queued — including the request
 * chain of a batch the sender just dispatched — runs before this resolves, so a responder
 * that awaits it measures real overlap rather than racing the dispatcher.
 */
export function macrotaskTurn(): Promise<void> {
  return new Promise<void>((resolve) => {
    setImmediate(resolve);
  });
}

/** An `onBatchAcked` that ignores its batch and summary (the type needs a promise). */
export const ignoreAck = (): Promise<void> => Promise.resolve();
