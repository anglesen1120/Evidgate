// The sender over a scripted transport: a ten-minute outage delivered once per batch, the
// exact request the route defines, and the gzip bytes forwarded only when a batch carries them.
import { afterEach, describe, expect, it } from 'vitest';

import type { Batch } from './batcher.js';
import { resetSecrets } from './redact.js';
import { sendBatches, type BatchAckSummary } from './sender.js';
import {
  acked,
  batchStream,
  harness,
  ignoreAck,
  makeBatch,
  refusal,
  runId,
  sendOptions,
  token,
} from './sender.testing.js';

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
