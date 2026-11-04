// The send deadline: a retry that would sleep past it fails with `deadline_exceeded` naming
// the last error instead of spinning, and an already expired deadline makes no request at all.
import { afterEach, describe, expect, it } from 'vitest';

import { resetSecrets } from './redact.js';
import { sendBatches } from './sender.js';
import {
  acked,
  batchStream,
  harness,
  ignoreAck,
  makeBatch,
  rejection,
  refusal,
  sendOptions,
  token,
} from './sender.testing.js';

afterEach(() => {
  resetSecrets();
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
