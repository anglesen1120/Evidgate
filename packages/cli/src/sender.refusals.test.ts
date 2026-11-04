// A refusal the sender must not retry: the error envelope surfaces verbatim after exactly one
// request, a 5xx that says `retryable: false` stops, and the token never reaches the text.
import { afterEach, describe, expect, it } from 'vitest';

import type { Batch } from './batcher.js';
import { resetSecrets } from './redact.js';
import { sendBatches } from './sender.js';
import {
  batchStream,
  harness,
  ignoreAck,
  jsonResponse,
  makeBatch,
  rejection,
  refusal,
  sendOptions,
  token,
} from './sender.testing.js';

afterEach(() => {
  resetSecrets();
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
