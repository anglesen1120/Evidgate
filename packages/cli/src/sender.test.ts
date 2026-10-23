// 04.2.3-T1.3: the sender under a scripted transport (task §9 `outage_then_delivery` and
// `retry_paths`, plus every failure path of 04.2.3 §2 AC1/AC5 and §8): a 10-minute
// virtual outage, `Retry-After` in both forms, full-jitter backoff bounds, the deadline,
// verbatim 401/403/409/413/422 surfacing with exactly one request, `retryable: false` on
// a 5xx, verified acks (`ack_mismatch`), the concurrency cap, acks delivered for settled
// batches after a fatal error, gzip only when the batch carries it, and the token never
// reaching a message.
//
// Determinism: no network and no waiting. `fetch` is a scripted responder, `sleep`
// advances a virtual clock, `random` is fixed at 0/0.5/1, and `now` reads that clock.
import { afterEach, describe, expect, it } from 'vitest';

import type { Batch } from './batcher.js';
import { resolveConfig } from './config.js';
import { resetSecrets } from './redact.js';
import {
  maxInFlight,
  sendBatches,
  SendFailedError,
  type BatchAckSummary,
  type SendDeps,
  type SendOptions,
} from './sender.js';
import type { SpoolEvent } from './spool.js';

/** Assembled at runtime: no secret literal is stored in the spec (04.2.3 §8). */
const token = ['tk', 'producer', 'value'].join('-');
const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
const config = resolveConfig({
  env: { EVIDGATE_API_URL: 'http://127.0.0.1:9', EVIDGATE_PROJECT: 'proj-1' },
});
const defaultDeadlineMs = 30 * 60 * 1000;
/** The batch key the sender must send unchanged on every attempt. */
const goodKey = 'batch:good';

interface CapturedCall {
  readonly request: Request;
  readonly body: Buffer;
  readonly key: string | null;
}

interface Harness {
  readonly deps: SendDeps;
  readonly calls: CapturedCall[];
  readonly sleeps: number[];
  readonly clock: { now: number };
  /** Highest number of requests the responder saw overlapping. */
  readonly peak: number;
}

type Responder = (call: CapturedCall) => Response | Promise<Response>;

/** A scripted transport: it records every request and answers from `respond`. */
function harness(respond: Responder, random: () => number = () => 0.5): Harness {
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

function sendOptions(overrides: { deadlineMs?: number; maxInFlight?: number } = {}): SendOptions {
  return {
    config,
    token,
    runId,
    deadlineMs: overrides.deadlineMs ?? defaultDeadlineMs,
    ...(overrides.maxInFlight === undefined ? {} : { maxInFlight: overrides.maxInFlight }),
  };
}

function jsonResponse(
  status: number,
  body: unknown,
  headers: Record<string, string> = {},
): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json', ...headers },
  });
}

/** A 00 §9.2 error envelope: `code`/`request_id` must reach the caller verbatim (T9). */
function refusal(
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
function eventIdsOf(body: Buffer): string[] {
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
function ackBody(acks: readonly SentAck[]): AckPayload {
  return {
    batch_id: '00000000-0000-4000-8000-000000000001',
    accepted: acks.filter((ack) => ack.ack === 'accepted').length,
    duplicates: acks.filter((ack) => ack.ack === 'duplicate').length,
    acks: acks.map((ack, index) => ({ index, ...ack })),
  };
}

/** A correct 200 for the batch whose bytes were posted. */
function acked(body: Buffer, mode: 'accepted' | 'duplicate' | 'mixed' = 'accepted'): Response {
  const acks = eventIdsOf(body).map((event_id, index) => ({
    event_id,
    ack: mode === 'mixed' ? (index % 2 === 0 ? 'accepted' : 'duplicate') : mode,
  }));
  return jsonResponse(200, ackBody(acks));
}

/** A batch as the batcher builds it: pre-serialised bytes plus the batch key. */
function makeBatch(eventIds: readonly string[], label: string, gzip = false): Batch {
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

async function* batchStream(batches: readonly Batch[]): AsyncGenerator<Batch> {
  await Promise.resolve();
  yield* batches;
}

/** The `SendFailedError` a send raised; any other outcome fails the spec. */
async function rejection(pending: Promise<unknown>): Promise<SendFailedError> {
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
function macrotaskTurn(): Promise<void> {
  return new Promise<void>((resolve) => {
    setImmediate(resolve);
  });
}

/** An `onBatchAcked` that ignores its batch and summary (the type needs a promise). */
const ignoreAck = (): Promise<void> => Promise.resolve();

afterEach(() => {
  resetSecrets();
});

describe('outage_then_delivery', () => {
  it('delivers every batch once across a 10-minute virtual outage, duplicates included', async () => {
    const batches = [
      makeBatch(['a1'], 'outage-1'),
      makeBatch(['b1', 'b2'], 'outage-2'),
      makeBatch(['c1', 'c2', 'c3'], 'outage-3'),
    ];
    const attempts = new Map<string, number>();
    const delivered = new Map<string, number>();
    const h = harness((call) => {
      const key = call.key ?? '';
      const attempt = (attempts.get(key) ?? 0) + 1;
      attempts.set(key, attempt);
      // The first minutes are a dead endpoint, then ten minutes of 503s with a
      // full-minute `Retry-After`, then the ACK of the batch's one delivery.
      if (attempt <= 4) {
        throw new TypeError('connect ECONNREFUSED 127.0.0.1:9');
      }
      if (attempt <= 14) {
        return refusal(503, 'unavailable', {
          requestId: `req-${attempt}`,
          retryable: true,
          retryAfter: '60',
        });
      }
      delivered.set(key, (delivered.get(key) ?? 0) + 1);
      return acked(call.body, 'duplicate');
    });

    const acks: { readonly batch: Batch; readonly summary: BatchAckSummary }[] = [];
    const result = await sendBatches(
      batchStream(batches),
      // The batches share one virtual clock: the thirty 60 s waits alone consume
      // 30 minutes of it, so the deadline must stay above that sum.
      sendOptions({ deadlineMs: 60 * 60 * 1000 }),
      h.deps,
      (batch, summary) => {
        acks.push({ batch, summary });
        return Promise.resolve();
      },
    );

    expect(h.clock.now - 1_000).toBeGreaterThanOrEqual(600_000);
    // Per batch: 4 jittered transport retries (random 0.5 of 500/1,000/2,000/4,000 ms),
    // then ten `Retry-After: 60` waits — ten virtual minutes of 503s, ×3 batches.
    const sleeps = [...h.sleeps].sort((left, right) => left - right);
    expect(sleeps).toEqual([
      ...new Array<number>(3).fill(250),
      ...new Array<number>(3).fill(500),
      ...new Array<number>(3).fill(1_000),
      ...new Array<number>(3).fill(2_000),
      ...new Array<number>(30).fill(60_000),
    ]);
    expect(result).toEqual({ batches: 3, accepted: 0, duplicates: 6 });
    // Every batch was answered exactly once: a resend is a duplicate, not a second event.
    expect([...delivered.entries()].sort()).toEqual([
      ['batch:outage-1', 1],
      ['batch:outage-2', 1],
      ['batch:outage-3', 1],
    ]);
    // Acks arrive in completion order, not dispatch order; the counts identify each one.
    const summaries = acks.map((ack) => ack.summary.duplicates).sort((a, b) => a - b);
    expect(summaries).toEqual([1, 2, 3]);
    expect(acks.every((ack) => ack.summary.accepted === 0)).toBe(true);
  });

  it('sends the request the route defines, with the batch key and the bearer', async () => {
    const h = harness((call) => acked(call.body));
    await sendBatches(batchStream([makeBatch(['d1'], 'shape')]), sendOptions(), h.deps, ignoreAck);

    const [call] = h.calls;
    expect(call?.request.url).toBe(`http://127.0.0.1:9/v1/runs/${runId}/events`);
    expect(call?.request.method).toBe('POST');
    expect(call?.key).toBe('batch:shape');
    expect(call?.request.headers.get('content-type')).toBe('application/json');
    expect(call?.request.headers.get('authorization')).toBe(`Bearer ${token}`);
    expect(call?.body.equals(makeBatch(['d1'], 'shape').body)).toBe(true);
  });
});

describe('retry_paths', () => {
  it('honours Retry-After delta-seconds', async () => {
    let attempts = 0;
    const h = harness((call) => {
      attempts += 1;
      return attempts === 1
        ? refusal(429, 'rate_limited', { requestId: 'req-429', retryable: true, retryAfter: '3' })
        : acked(call.body);
    });

    const result = await sendBatches(
      batchStream([makeBatch(['r1'], 'retry-seconds')]),
      sendOptions(),
      h.deps,
      ignoreAck,
    );

    expect(h.sleeps).toEqual([3_000]);
    expect(result.batches).toBe(1);
  });

  it('honours Retry-After as an HTTP-date against the injected clock', async () => {
    let attempts = 0;
    const h: Harness = harness((call) => {
      attempts += 1;
      if (attempts > 1) {
        return acked(call.body);
      }
      const at = new Date(h.clock.now + 4_000).toUTCString();
      return refusal(429, 'rate_limited', { retryable: true, retryAfter: at });
    });

    await sendBatches(
      batchStream([makeBatch(['r2'], 'retry-date')]),
      sendOptions(),
      h.deps,
      ignoreAck,
    );

    expect(h.sleeps).toEqual([4_000]);
  });

  it('retries a mid-batch 500 with the same bytes, key and event ids', async () => {
    let attempts = 0;
    const h = harness((call) => {
      attempts += 1;
      return attempts === 1 ? refusal(500, 'internal', { retryable: true }) : acked(call.body);
    });

    const batch = makeBatch(['m1', 'm2'], 'mid-batch');
    await sendBatches(batchStream([batch]), sendOptions(), h.deps, ignoreAck);

    expect(h.calls).toHaveLength(2);
    const [first, second] = h.calls;
    expect(first?.key).toBe('batch:mid-batch');
    expect(second?.key).toBe('batch:mid-batch');
    expect(second?.body.equals(first?.body ?? Buffer.alloc(0))).toBe(true);
    expect(second?.body.equals(batch.body)).toBe(true);
    expect(eventIdsOf(second?.body ?? Buffer.alloc(0))).toEqual(['m1', 'm2']);
    // random 0.5 of a 500 ms ceiling.
    expect(h.sleeps).toEqual([250]);
  });

  it('grows and caps the jittered backoff with a fixed random of 1', async () => {
    let attempts = 0;
    const h = harness(
      (call) => {
        attempts += 1;
        return attempts <= 8 ? refusal(503, 'unavailable', { retryable: true }) : acked(call.body);
      },
      () => 1,
    );

    await sendBatches(
      batchStream([makeBatch(['j1'], 'jitter-one')]),
      sendOptions(),
      h.deps,
      ignoreAck,
    );

    expect(h.sleeps).toEqual([500, 1_000, 2_000, 4_000, 8_000, 16_000, 30_000, 30_000]);
  });

  it('adds no delay when the jitter sample is 0 (full jitter lower bound)', async () => {
    let attempts = 0;
    const h = harness(
      (call) => {
        attempts += 1;
        return attempts <= 3 ? refusal(500, 'internal', { retryable: true }) : acked(call.body);
      },
      () => 0,
    );

    await sendBatches(
      batchStream([makeBatch(['j2'], 'jitter-zero')]),
      sendOptions(),
      h.deps,
      ignoreAck,
    );

    expect(h.sleeps).toEqual([0, 0, 0]);
    expect(h.calls).toHaveLength(4);
  });

  it('treats an aborted request (the 30 s timeout) as a retryable transport error', async () => {
    let attempts = 0;
    const h = harness((call) => {
      attempts += 1;
      if (attempts === 1) {
        throw new DOMException('the request was aborted', 'AbortError');
      }
      return acked(call.body);
    });

    const result = await sendBatches(
      batchStream([makeBatch(['t1'], 'timeout')]),
      sendOptions(),
      h.deps,
      ignoreAck,
    );

    expect(result.batches).toBe(1);
    expect(h.calls).toHaveLength(2);
    expect(h.sleeps).toEqual([250]);
  });
});

describe('deadline', () => {
  it('fails with deadline_exceeded naming the last error instead of waiting past it', async () => {
    const h = harness(() =>
      refusal(503, 'unavailable', { requestId: 'req-slow', retryable: true, retryAfter: '60' }),
    );

    const error = await rejection(
      sendBatches(
        batchStream([makeBatch(['x1', 'x2'], 'deadline')]),
        sendOptions({ deadlineMs: 30_000 }),
        h.deps,
        ignoreAck,
      ),
    );

    expect(error.code).toBe('deadline_exceeded');
    expect(error.status).toBe(503);
    expect(error.requestId).toBe('req-slow');
    expect(error.firstSequence).toBe(1);
    expect(error.lastSequence).toBe(2);
    expect(error.message).toContain('unavailable');
    expect(error.message).toContain('HTTP 503');
    expect(error.message).toContain('req-slow');
    expect(h.calls).toHaveLength(1);
    expect(h.sleeps).toEqual([]);
    expect(error.message).not.toContain(token);
  });

  it('stops before the first request when the deadline is already expired', async () => {
    const h = harness((call) => acked(call.body));
    const error = await rejection(
      sendBatches(
        batchStream([makeBatch(['y1'], 'expired')]),
        sendOptions({ deadlineMs: 0 }),
        h.deps,
        ignoreAck,
      ),
    );

    expect(error.code).toBe('deadline_exceeded');
    expect(h.calls).toHaveLength(0);
  });
});

describe('permanent refusals', () => {
  const cases = [
    { status: 403, code: 'producer_slot_taken' },
    { status: 422, code: 'unknown_producer' },
    { status: 422, code: 'event_invalid' },
    { status: 422, code: 'sequence_reused' },
    { status: 409, code: 'duplicate_different_payload' },
    { status: 409, code: 'run_closed' },
    { status: 413, code: 'batch_too_large' },
    { status: 401, code: 'token_expired' },
  ] as const;

  it.each(cases)(
    'fails fast on $status $code after exactly one request',
    async ({ status, code }) => {
      const h = harness(() => refusal(status, code, { requestId: 'req-verbatim' }));
      const acked: Batch[] = [];

      const error = await rejection(
        sendBatches(
          batchStream([makeBatch(['p1', 'p2'], 'permanent')]),
          sendOptions(),
          h.deps,
          (batch) => {
            acked.push(batch);
            return Promise.resolve();
          },
        ),
      );

      expect(h.calls).toHaveLength(1);
      expect(h.sleeps).toEqual([]);
      expect(acked).toEqual([]);
      expect(error.code).toBe(code);
      expect(error.requestId).toBe('req-verbatim');
      expect(error.status).toBe(status);
      expect(error.firstSequence).toBe(1);
      expect(error.lastSequence).toBe(2);
    },
  );

  it('stops retrying a 5xx whose envelope says retryable: false', async () => {
    const h = harness(() =>
      refusal(503, 'unavailable', { requestId: 'req-permanent', retryable: false }),
    );

    const error = await rejection(
      sendBatches(
        batchStream([makeBatch(['q1'], 'permanent-5xx')]),
        sendOptions(),
        h.deps,
        ignoreAck,
      ),
    );

    expect(h.calls).toHaveLength(1);
    expect(error.code).toBe('unavailable');
    expect(error.status).toBe(503);
    expect(error.requestId).toBe('req-permanent');
  });

  it('never puts the bearer token in the error text', async () => {
    const h = harness(() =>
      jsonResponse(403, {
        error: {
          code: 'producer_slot_taken',
          message: `the slot is bound to another identity (${token})`,
          request_id: 'req-echo',
          retryable: false,
        },
      }),
    );

    const error = await rejection(
      sendBatches(batchStream([makeBatch(['s1'], 'token-echo')]), sendOptions(), h.deps, ignoreAck),
    );

    expect(error.code).toBe('producer_slot_taken');
    expect(error.message).not.toContain(token);
    expect(String(error.stack)).not.toContain(token);
  });
});

describe('acknowledgements', () => {
  it('never exceeds maxInFlight requests and delivers all of them', async () => {
    const batches = Array.from({ length: 8 }, (_unused, index) =>
      makeBatch([`c${index}`], `concurrency-${index}`),
    );
    // Every request holds for two macrotask turns, so all of the batches dispatched in
    // one window have certainly reached the responder before any of them answers: the
    // measured overlap is the sender's dispatch overlap, and a sequential sender still
    // peaks at one instead of hanging.
    const h = harness(async (call) => {
      await macrotaskTurn();
      await macrotaskTurn();
      return acked(call.body);
    });

    const result = await sendBatches(batchStream(batches), sendOptions(), h.deps, ignoreAck);

    expect(h.peak).toBe(maxInFlight);
    expect(h.calls).toHaveLength(8);
    expect(result).toEqual({ batches: 8, accepted: 8, duplicates: 0 });
  });

  it('lowers the concurrency when the caller asks for it', async () => {
    const batches = Array.from({ length: 3 }, (_unused, index) =>
      makeBatch([`l${index}`], `lowered-${index}`),
    );
    const h = harness(async (call) => {
      await macrotaskTurn();
      await macrotaskTurn();
      return acked(call.body);
    });

    const result = await sendBatches(
      batchStream(batches),
      sendOptions({ maxInFlight: 1 }),
      h.deps,
      ignoreAck,
    );

    expect(h.peak).toBe(1);
    expect(result.batches).toBe(3);
  });

  /** A 200 whose per-event acks are wrong in exactly one way. */
  const brokenAcks = [
    {
      name: 'a missing acknowledgement',
      payload: (ids: readonly string[]) =>
        ackBody(ids.slice(1).map((id) => ({ event_id: id, ack: 'accepted' }))),
    },
    {
      name: 'another event id',
      payload: (ids: readonly string[]) =>
        ackBody(
          ids.map((id, index) => ({
            event_id: index === 0 ? 'other' : id,
            ack: 'accepted',
          })),
        ),
    },
    {
      name: 'an unknown status',
      payload: (ids: readonly string[]) =>
        ackBody(ids.map((id) => ({ event_id: id, ack: 'rejected' }))),
    },
    {
      name: 'counts that disagree with the acks',
      payload: (ids: readonly string[]) => ({
        ...ackBody(ids.map((id) => ({ event_id: id, ack: 'accepted' }))),
        duplicates: 1,
      }),
    },
  ];

  it.each(brokenAcks)(
    'fails with ack_mismatch on $name and advances nothing',
    async ({ payload }) => {
      const h = harness((call) => jsonResponse(200, payload(eventIdsOf(call.body))));
      const acked: Batch[] = [];

      const error = await rejection(
        sendBatches(
          batchStream([makeBatch(['k1', 'k2'], 'mismatch')]),
          sendOptions(),
          h.deps,
          (batch) => {
            acked.push(batch);
            return Promise.resolve();
          },
        ),
      );

      expect(h.calls).toHaveLength(1);
      expect(acked).toEqual([]);
      expect(error.code).toBe('ack_mismatch');
      expect(error.status).toBe(200);
      expect(error.firstSequence).toBe(1);
      expect(error.lastSequence).toBe(2);
    },
  );

  it('still delivers the acks of settled batches before throwing the first fatal error', async () => {
    const good = makeBatch(['g1', 'g2'], 'good');
    const bad = makeBatch(['x1'], 'bad');
    // The good batch answers a few turns later; the 403 answers at once, so the fatal
    // error exists while the good batch is still in flight.
    const h = harness(async (call) => {
      if (call.key === goodKey) {
        await macrotaskTurn();
        await macrotaskTurn();
        return acked(call.body);
      }
      return refusal(403, 'producer_slot_taken', { requestId: 'req-slot' });
    });

    const ackedKeys: string[] = [];
    const error = await rejection(
      sendBatches(batchStream([good, bad]), sendOptions(), h.deps, (batch) => {
        ackedKeys.push(batch.idempotencyKey);
        return Promise.resolve();
      }),
    );

    expect(error.code).toBe('producer_slot_taken');
    expect(ackedKeys).toEqual([goodKey]);
    expect(h.calls).toHaveLength(2);
  });

  it('counts duplicate acks as delivered for the batch summary', async () => {
    const h = harness((call) => acked(call.body, 'mixed'));

    const acks: BatchAckSummary[] = [];
    const result = await sendBatches(
      batchStream([makeBatch(['n1', 'n2', 'n3'], 'mixed')]),
      sendOptions(),
      h.deps,
      (_batch, summary) => {
        acks.push(summary);
        return Promise.resolve();
      },
    );

    expect(acks).toEqual([{ accepted: 2, duplicates: 1 }]);
    expect(result).toEqual({ batches: 1, accepted: 2, duplicates: 1 });
  });

  it('accepts an ack that spells a GUID in another case', async () => {
    const eventId = 'A1B2C3D4-0000-4000-8000-000000000001';
    // The envelope's `event_id` is `format: uuid` (any case); the server answers with a
    // Guid, which serialises lowercase — the same identity, so it must not be a mismatch.
    const h = harness(() =>
      jsonResponse(200, {
        acks: [{ index: 0, event_id: eventId.toLowerCase(), ack: 'accepted' }],
        accepted: 1,
        duplicates: 0,
      }),
    );

    const result = await sendBatches(
      batchStream([makeBatch([eventId], 'uuid-case')]),
      sendOptions(),
      h.deps,
      ignoreAck,
    );

    expect(result).toEqual({ batches: 1, accepted: 1, duplicates: 0 });
  });
});

describe('encoding', () => {
  it('sends the gzip bytes with Content-Encoding only when the batch has them', async () => {
    const plain = makeBatch(['e1'], 'plain');
    const compressed = makeBatch(['e2', 'e3'], 'gzip', true);
    // The gzip bytes are opaque here, so the ACK is derived from the batch they belong to.
    const h = harness((call) => acked(call.key === 'batch:gzip' ? compressed.body : plain.body));

    const result = await sendBatches(
      batchStream([plain, compressed]),
      sendOptions({ maxInFlight: 1 }),
      h.deps,
      ignoreAck,
    );

    expect(result.batches).toBe(2);
    const plainCall = h.calls[0];
    const gzipCall = h.calls[1];
    expect(plainCall?.request.headers.get('content-encoding')).toBeNull();
    expect(plainCall?.body.equals(plain.body)).toBe(true);
    expect(gzipCall?.request.headers.get('content-encoding')).toBe('gzip');
    expect(gzipCall?.request.headers.get('content-type')).toBe('application/json');
    expect(gzipCall?.body.equals(compressed.gzipBody ?? Buffer.alloc(0))).toBe(true);
    expect(gzipCall?.body.equals(compressed.body)).toBe(false);
  });
});
