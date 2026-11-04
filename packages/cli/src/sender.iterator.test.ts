// The batch iterator the sender drains: it is closed exactly once, on the success path and on
// the failure path, so a spool reader never stays open after a send returns.
import { afterEach, describe, expect, it } from 'vitest';

import type { Batch } from './batcher.js';
import { resetSecrets } from './redact.js';
import { sendBatches } from './sender.js';
import {
  acked,
  harness,
  ignoreAck,
  makeBatch,
  rejection,
  refusal,
  sendOptions,
} from './sender.testing.js';

afterEach(() => {
  resetSecrets();
});

/** A batch source whose iterator records how often `return` closed it. */
function trackedStream(batches: readonly Batch[], state: { closed: number }): AsyncIterable<Batch> {
  return {
    [Symbol.asyncIterator]: (): AsyncIterator<Batch> => {
      let index = 0;
      return {
        next: (): Promise<IteratorResult<Batch>> => {
          const batch = batches[index];
          index += 1;
          return Promise.resolve(
            batch === undefined ? { done: true, value: undefined } : { done: false, value: batch },
          );
        },
        return: (): Promise<IteratorResult<Batch>> => {
          state.closed += 1;
          return Promise.resolve({ done: true, value: undefined });
        },
      };
    },
  };
}

describe('iterator lifecycle', () => {
  it('closes the batch iterator when the send completes (P3-10)', async () => {
    const state = { closed: 0 };
    const h = harness((call) => acked(call.body));

    const result = await sendBatches(
      trackedStream([makeBatch(['e1'], 'closes')], state),
      sendOptions(),
      h.deps,
      ignoreAck,
    );

    expect(result.batches).toBe(1);
    expect(state.closed).toBe(1);
  });

  it('closes the batch iterator when the send fails (P3-10)', async () => {
    const state = { closed: 0 };
    const h = harness(() => refusal(422, 'event_invalid', { requestId: 'req-close' }));

    const error = await rejection(
      sendBatches(
        trackedStream([makeBatch(['e2'], 'closes-fatal')], state),
        sendOptions(),
        h.deps,
        ignoreAck,
      ),
    );

    expect(error.code).toBe('event_invalid');
    expect(state.closed).toBe(1);
  });
});
