// How the sender retries: `Retry-After` in both forms, the jittered backoff bounds, a
// transport retry with the same bytes, and the stop of every sibling retry once a batch
// failed fatally.
import { afterEach, describe, expect, it } from 'vitest';

import { resetSecrets } from './redact.js';
import { sendBatches } from './sender.js';
import {
  acked,
  batchStream,
  eventIdsOf,
  harness,
  ignoreAck,
  macrotaskTurn,
  makeBatch,
  rejection,
  refusal,
  sendOptions,
  type Harness,
} from './sender.testing.js';

afterEach(() => {
  resetSecrets();
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

  it('still backs off when Retry-After says zero (P3-6)', async () => {
    let attempts = 0;
    const h = harness(
      (call) => {
        attempts += 1;
        return attempts <= 2
          ? refusal(429, 'rate_limited', { retryable: true, retryAfter: '0' })
          : acked(call.body);
      },
      () => 1,
    );

    await sendBatches(
      batchStream([makeBatch(['z1'], 'retry-zero')]),
      sendOptions(),
      h.deps,
      ignoreAck,
    );

    // The jittered backoff is the floor under `Retry-After`, so a zero never becomes a
    // tight retry loop: 500 ms then 1 s with a full sample.
    expect(h.sleeps).toEqual([500, 1_000]);
    expect(h.calls).toHaveLength(3);
  });

  it('still backs off when Retry-After names an HTTP-date already past (P3-6)', async () => {
    let attempts = 0;
    const h: Harness = harness(
      (call) => {
        attempts += 1;
        if (attempts > 1) {
          return acked(call.body);
        }
        return refusal(429, 'rate_limited', {
          retryable: true,
          retryAfter: new Date(h.clock.now - 5_000).toUTCString(),
        });
      },
      () => 0.5,
    );

    await sendBatches(
      batchStream([makeBatch(['z2'], 'retry-past-date')]),
      sendOptions(),
      h.deps,
      ignoreAck,
    );

    // A past date is 0 ms, so the floor decides: random 0.5 of a 500 ms ceiling.
    expect(h.sleeps).toEqual([250]);
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

  it('stops a sibling batch retrying once another batch failed fatally (P3-4)', async () => {
    const fatal = makeBatch(['f1'], 'fatal');
    const retrying = makeBatch(['r1', 'r2'], 'retrying');
    // The 422 is answered at once and the 503 only a few turns later, so the fatal error
    // exists while the retrying batch is about to back off.
    const h = harness(async (call) => {
      if (call.key === 'batch:fatal') {
        return refusal(422, 'event_invalid', { requestId: 'req-fatal' });
      }
      await macrotaskTurn();
      await macrotaskTurn();
      return refusal(503, 'unavailable', { retryable: true, retryAfter: '5' });
    });

    const error = await rejection(
      sendBatches(
        batchStream([fatal, retrying]),
        sendOptions({ maxInFlight: 2 }),
        h.deps,
        ignoreAck,
      ),
    );

    // The first fatal error is the one reported, and the sibling neither slept nor made a
    // second attempt: the send does not wait for its 5 s backoff (or the deadline).
    expect(error.code).toBe('event_invalid');
    expect(error.requestId).toBe('req-fatal');
    expect(h.sleeps).toEqual([]);
    expect(h.calls.map((call) => call.key)).toEqual(['batch:fatal', 'batch:retrying']);
  });
});
