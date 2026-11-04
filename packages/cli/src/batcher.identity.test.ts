// A batch's identity: the line identity a spool line must match (its producer directory and
// the run being sent, reported before the schema verdict), and the derived
// `Idempotency-Key` over the batch's `(sequence, event_id)` pairs.
import { describe, expect, it } from 'vitest';

import {
  EnvelopeInvalidError,
  createEnvelopeValidator,
  deriveBatchIdempotencyKey,
  packBatches,
} from './batcher.js';
import {
  collect,
  contractsDirectory,
  envelopeText,
  eventIdFor,
  expectedBody,
  failure,
  options,
  sourceOf,
  spoolEvent,
} from './batcher.testing.js';

describe('spool line identity (SC5)', () => {
  const validate = createEnvelopeValidator(contractsDirectory);

  it('rejects a line whose producer_id is not the directory it sits in', async () => {
    const events = [spoolEvent(1, { pad: 8 }), spoolEvent(2, { producerId: 'pw-shard-9' })];
    const error = await failure(collect(packBatches(sourceOf(events), options({ validate }))));

    expect(error).toBeInstanceOf(EnvelopeInvalidError);
    const thrown = error as EnvelopeInvalidError;
    expect(thrown.sequence).toBe(2);
    expect(thrown.eventId).toBe(eventIdFor(2));
    expect(thrown.details).toEqual([{ path: '/producer_id', reason: 'producer_mismatch' }]);
    // The message names the sequence and the trusted expectation — never the line's own value.
    expect(thrown.message).toContain('sequence 2');
    expect(thrown.message).toContain('the line is spooled under producer "pw-shard-0"');
    expect(thrown.message).not.toContain('pw-shard-9');
  });

  it('rejects a line that binds itself to another run', async () => {
    const line = envelopeText(3, { runId: '0197c9a4-0000-7000-8000-000000000000' });
    const error = await failure(
      collect(packBatches(sourceOf([spoolEvent(3, { line })]), options({ validate }))),
    );

    expect(error).toBeInstanceOf(EnvelopeInvalidError);
    const thrown = error as EnvelopeInvalidError;
    expect(thrown.sequence).toBe(3);
    expect(thrown.details).toEqual([{ path: '/run_id', reason: 'run_mismatch' }]);
    expect(thrown.message).toContain(options().runId);
    expect(thrown.message).not.toContain('0197c9a4-0000-7000-8000-000000000000');
  });

  it('reports the identity mismatch before the schema verdict', async () => {
    // Malformed in two ways: the line also carries a bad `type`. The identity row is
    // the actionable one, so it is the row the operator sees.
    const line = envelopeText(4, { producerId: 'pw-shard-9', type: 'Not-A-Type' });
    const error = await failure(
      collect(packBatches(sourceOf([spoolEvent(4, { line })]), options({ validate }))),
    );

    expect((error as EnvelopeInvalidError).details).toEqual([
      { path: '/producer_id', reason: 'producer_mismatch' },
    ]);
  });

  it('accepts a line that names its own producer directory and the run being sent', async () => {
    const line = envelopeText(5, { runId: options().runId });
    const batches = await collect(packBatches(sourceOf([spoolEvent(5, { line })]), options()));

    expect(batches).toHaveLength(1);
    expect(batches[0]!.body.toString('utf8')).toBe(expectedBody([spoolEvent(5, { line })]));
  });
});

describe('deriveBatchIdempotencyKey', () => {
  const runId = '0197c9a4-0f3a-7c2e-9b1d-4f8e2a6c5d10';
  const producerId = 'pw-shard-0';
  const events = [1, 2, 3].map((sequence) => spoolEvent(sequence));
  const key = deriveBatchIdempotencyKey(runId, producerId, events);

  it('is `batch:` + sha256 hex, inside the 128-char identity limit', () => {
    expect(key).toMatch(/^batch:[0-9a-f]{64}$/);
    expect(key.length).toBeLessThanOrEqual(128);
  });

  it('is stable for the same events — the restart case', () => {
    const rebuilt = [1, 2, 3].map((sequence) => spoolEvent(sequence));

    expect(deriveBatchIdempotencyKey(runId, producerId, rebuilt)).toBe(key);
    // Identity is `(sequence, event_id)`; the body itself is the server's hash.
    const otherText = [1, 2, 3].map((sequence) => spoolEvent(sequence, { pad: 5 }));
    expect(deriveBatchIdempotencyKey(runId, producerId, otherText)).toBe(key);
  });

  it('differs for any differing sequence, event_id, run, producer, order or length', () => {
    const keys = new Set([
      key,
      deriveBatchIdempotencyKey(`${runId}-other`, producerId, events),
      deriveBatchIdempotencyKey(runId, `${producerId}-other`, events),
      deriveBatchIdempotencyKey(runId, producerId, [spoolEvent(1), spoolEvent(2), spoolEvent(4)]),
      deriveBatchIdempotencyKey(runId, producerId, [
        spoolEvent(1, { eventId: '00000000-0000-4000-8000-0000000000ff' }),
        spoolEvent(2),
        spoolEvent(3),
      ]),
      deriveBatchIdempotencyKey(runId, producerId, [spoolEvent(1), spoolEvent(3), spoolEvent(2)]),
      deriveBatchIdempotencyKey(runId, producerId, [spoolEvent(1), spoolEvent(2)]),
      deriveBatchIdempotencyKey(runId, producerId, []),
    ]);

    expect(keys.size).toBe(8);
  });

  it('is derived per batch, so two batches never share a key', async () => {
    const events = Array.from({ length: 501 }, (_, index) => spoolEvent(index + 1));
    const batches = await collect(packBatches(sourceOf(events), options()));

    expect(batches).toHaveLength(2);
    expect(batches[0]!.idempotencyKey).not.toBe(batches[1]!.idempotencyKey);
    expect(batches[0]!.idempotencyKey).toBe(
      deriveBatchIdempotencyKey(
        '0197c9a4-0f3a-7c2e-9b1d-4f8e2a6c5d10',
        'pw-shard-0',
        events.slice(0, 500),
      ),
    );
  });
});
