// Acks and concurrency: the `maxInFlight` cap, verification of the 200 ack list
// (`ack_mismatch`), the batch summary, and settled acks delivered before a fatal error.
import { afterEach, describe, expect, it } from 'vitest';

import type { Batch } from './batcher.js';
import { resetSecrets } from './redact.js';
import { maxInFlight, sendBatches, type BatchAckSummary } from './sender.js';
import {
  ackBody,
  acked,
  batchStream,
  eventIdsOf,
  harness,
  ignoreAck,
  jsonResponse,
  macrotaskTurn,
  makeBatch,
  rejection,
  refusal,
  sendOptions,
} from './sender.testing.js';

/** The batch key the sender must send unchanged on every attempt. */
const goodKey = 'batch:good';

afterEach(() => {
  resetSecrets();
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
