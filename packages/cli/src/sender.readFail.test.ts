// A 2xx whose body could not be read to the end rejects the SDK call. The sender must
// treat that exactly like a lost transport: retry it under the existing backoff/deadline
// policy with the same bytes and event ids, and never report it as an ack mismatch.
import { afterEach, describe, expect, it } from 'vitest';

import { bodyCutResponse, bodyCutSecrets } from './bodyCut.testing.js';
import { resetSecrets } from './redact.js';
import { sendBatches } from './sender.js';
import {
  acked,
  batchStream,
  eventIdsOf,
  harness,
  ignoreAck,
  makeBatch,
  rejection,
  sendOptions,
} from './sender.testing.js';

afterEach(() => {
  resetSecrets();
});

describe('sender read failure', () => {
  it('retries a cut ack body as a transport failure and then delivers', async () => {
    let attempts = 0;
    const h = harness((call) => {
      attempts += 1;
      return attempts === 1 ? bodyCutResponse(200) : acked(call.body);
    });
    const batch = makeBatch(['rc1', 'rc2'], 'read-cut');

    const result = await sendBatches(batchStream([batch]), sendOptions(), h.deps, ignoreAck);

    expect(result.batches).toBe(1);
    expect(h.calls).toHaveLength(2);
    const [first, second] = h.calls;
    expect(first?.key).toBe('batch:read-cut');
    expect(second?.key).toBe('batch:read-cut');
    expect(second?.body.equals(batch.body)).toBe(true);
    expect(eventIdsOf(second?.body ?? Buffer.alloc(0))).toEqual(['rc1', 'rc2']);
    // random 0.5 of the 500 ms first-attempt ceiling: the existing policy, not a new one.
    expect(h.sleeps).toEqual([250]);
  });

  it('reports a repeatedly cut ack body as transport exhaustion, never ack_mismatch', async () => {
    const h = harness(() => bodyCutResponse(200));

    const error = await rejection(
      sendBatches(
        batchStream([makeBatch(['rx1'], 'read-cut-exhausted')]),
        sendOptions({ deadlineMs: 5_000 }),
        h.deps,
        ignoreAck,
      ),
    );

    expect(h.calls.length).toBeGreaterThanOrEqual(2);
    expect(h.sleeps.length).toBe(h.calls.length - 1);
    // The existing deadline answer, whose last-error note is the transport class.
    expect(error.code).toBe('deadline_exceeded');
    expect(error.message).toContain('transport_failed');
    expect(error.code).not.toBe('ack_mismatch');
    expect(h.calls.every((call) => call.key === 'batch:read-cut-exhausted')).toBe(true);
    const raised = `${error.message} ${error.code} ${error.requestId ?? ''} ${error.stack ?? ''}`;
    for (const secret of bodyCutSecrets) {
      expect(raised).not.toContain(secret);
    }
  });
});
