// 04.2.3-T1.2 (AC4): packing limits, the gzip decision, the derived batch
// `Idempotency-Key` and the streaming contract of `packBatches`, plus the
// per-line envelope validator's own failure surface. The corpus parity of the
// *schema registry* lives in parity.test.ts / schemaRegistry.test.ts; the
// batcher-level parity (validator verdicts against every single-event vector in
// fixtures/contracts/vectors/event-envelope.v1) lives in schemaParity.test.ts.
import { gunzipSync, gzipSync } from 'node:zlib';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import {
  EnvelopeInvalidError,
  OversizedEventError,
  createEnvelopeValidator,
  deriveBatchIdempotencyKey,
  maxBatchBytes,
  maxBatchEvents,
  packBatches,
  type Batch,
  type PackBatchesOptions,
} from './batcher.js';

import type { SpoolEvent } from './spool.js';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '../../..');
const contractsDirectory = join(repoRoot, 'contracts');

/** `{"events":[` is 11 bytes, `]}` is 2 — everything a body spends that is not an event line. */
const bodyOverheadBytes = 13;

/** A valid v4-shaped `event_id`, distinct per sequence. */
function eventIdFor(sequence: number): string {
  return `00000000-0000-4000-8000-${sequence.toString(16).padStart(12, '0')}`;
}

interface EnvelopeOverrides {
  readonly eventId?: string;
  readonly type?: string;
  readonly pad?: number;
  /** Overrides the required `producer_id`, to model a line planted in another producer's directory. */
  readonly producerId?: string;
  /** Adds a `run_id` member (not in the schema), to model a line that binds itself to another run. */
  readonly runId?: string;
}

/** One compact 00 §9.4 envelope: a single line, no newline (the reporter writes these). */
function envelopeText(sequence: number, o: EnvelopeOverrides = {}): string {
  const envelope: Record<string, unknown> = {
    schema_version: 1,
    event_id: o.eventId ?? eventIdFor(sequence),
    producer_id: o.producerId ?? 'pw-shard-0',
    sequence,
    execution_key: '0'.repeat(64),
    attempt_no: 1,
    type: o.type ?? 'run.log',
    occurred_at: '2026-01-01T00:00:00Z',
  };
  if (o.pad !== undefined) {
    envelope['ext'] = { schema: 'x'.repeat(o.pad) };
  }
  if (o.runId !== undefined) {
    envelope['run_id'] = o.runId;
  }
  return JSON.stringify(envelope);
}

/** A spool event as `ProducerSpool.readUnacked()` would hand it over. */
function spoolEvent(sequence: number, o: EnvelopeOverrides & { line?: string } = {}): SpoolEvent {
  const line = o.line ?? envelopeText(sequence, o);
  return {
    sequence,
    eventId: o.eventId ?? eventIdFor(sequence),
    line,
    lineBytes: Buffer.byteLength(line, 'utf8') + 1,
    chunk: '000001.jsonl',
    lineNumber: sequence,
  };
}

/**
 * A JSON envelope whose text is exactly `bytes` UTF-8 bytes: the padding lives in
 * `ext.schema`, so the line stays a single-line JSON object (a pad above the
 * schema's 128-character `maxLength` is only used where validation is a no-op —
 * the byte boundary is a transport property, checked before any schema work).
 */
function envelopeOfBytes(sequence: number, bytes: number): string {
  const base = envelopeText(sequence);
  // `,"ext":{"schema":""}` — the suffix inserted before the envelope's closing brace.
  const extField = ',"ext":{"schema":""}';
  const padding = bytes - base.length - extField.length;
  if (padding < 0) {
    throw new Error(
      `cannot build a ${bytes}-byte envelope: ${base.length + extField.length} is the minimum`,
    );
  }
  return `${base.slice(0, -1)},"ext":{"schema":"${'x'.repeat(padding)}"}` + '}';
}

/** `validate` stand-in for the size tests: the line is not under test there. */
const noopValidate = (): void => {};

/** One envelope line whose `ext.schema` carries `payload` verbatim (the schema landing spot for padding). */
function paddedLine(sequence: number, payload: string): string {
  return `${envelopeText(sequence).slice(0, -1)},"ext":{"schema":${JSON.stringify(payload)}}` + '}';
}

/**
 * A payload gzip shrinks by roughly 4:1 — enough for the 1 KiB saving floor while staying
 * well under the server's 20:1 expansion cap. Deterministic: a repeated phrase plus the
 * fixed-PRNG bytes; no `Math.random`.
 */
function moderatelyCompressibleText(): string {
  return `${'abcdefgh;'.repeat(10_000)}${pseudoRandomBytes(30_000).toString('base64')}`;
}

/** One envelope line plus a hostile extra member `name` (an `additional_properties` rejection). */
function withExtraMember(sequence: number, name: string): string {
  const envelope = JSON.parse(envelopeText(sequence)) as Record<string, unknown>;
  return JSON.stringify({ ...envelope, [name]: 1 });
}

/**
 * An `AsyncIterable` over a hand-rolled iterator. Deliberately NOT an async
 * generator: an async generator that only yields plain values (no `await`, no
 * `yield*` of an async iterable) is exactly what `@typescript-eslint/require-await`
 * reports, and the CLI lint gate runs with `--max-warnings 0`.
 */
function asyncSourceOf(iterate: () => Iterator<SpoolEvent>): AsyncIterable<SpoolEvent> {
  return {
    [Symbol.asyncIterator]: (): AsyncIterator<SpoolEvent> => {
      const iterator = iterate();
      return {
        next: (): Promise<IteratorResult<SpoolEvent>> => Promise.resolve(iterator.next()),
      };
    },
  };
}

function sourceOf(events: readonly SpoolEvent[]): AsyncIterable<SpoolEvent> {
  return asyncSourceOf(() => events[Symbol.iterator]());
}

/** A source that records how many events were actually pulled from it. */
function countingSource(events: readonly SpoolEvent[]): {
  readonly pulled: () => number;
  readonly iterable: AsyncIterable<SpoolEvent>;
} {
  const state = { pulled: 0 };
  const iterable = asyncSourceOf(() => {
    const iterator = events[Symbol.iterator]();
    return {
      next: (): IteratorResult<SpoolEvent> => {
        const result = iterator.next();
        if (!result.done) {
          state.pulled += 1;
        }
        return result;
      },
    };
  });
  return { pulled: () => state.pulled, iterable };
}

function options(overrides: Partial<PackBatchesOptions> = {}): PackBatchesOptions {
  return {
    runId: '0197c9a4-0f3a-7c2e-9b1d-4f8e2a6c5d10',
    producerId: 'pw-shard-0',
    validate: overrides.validate ?? noopValidate,
    // Spelled out rather than spread: `exactOptionalPropertyTypes` and the pack
    // defaults then never depend on how an absent override is modelled.
    maxEvents: overrides.maxEvents ?? maxBatchEvents,
    maxBytes: overrides.maxBytes ?? maxBatchBytes,
  };
}

async function collect(batches: AsyncIterable<Batch>): Promise<Batch[]> {
  const out: Batch[] = [];
  for await (const batch of batches) {
    out.push(batch);
  }
  return out;
}

/** Settles to the thrown value, so a test can assert on the error's fields. */
async function failure(promise: Promise<unknown>): Promise<unknown> {
  return promise.then(
    () => undefined,
    (error: unknown) => error,
  );
}

/** The value `fn` threw, or `undefined` when it returned normally. */
function thrownBy(fn: () => void): unknown {
  try {
    fn();
    return undefined;
  } catch (error) {
    return error;
  }
}

/** The bodies a batch would send, as text: `{"events":[<line>,…]}`. */
function expectedBody(events: readonly SpoolEvent[]): string {
  return `{"events":[${events.map((event) => event.line).join(',')}]}`;
}

/** A deterministic PRNG (xorshift32) — no `Math.random` in a reproducible test. */
function pseudoRandomBytes(length: number, seed = 0x9e3779b9): Buffer {
  let state = seed >>> 0;
  const bytes = Buffer.alloc(length);
  for (let index = 0; index < length; index += 1) {
    state ^= state << 13;
    state >>>= 0;
    state ^= state >>> 17;
    state ^= state << 5;
    state >>>= 0;
    bytes[index] = state & 0xff;
  }
  return bytes;
}

describe('packing_limits (AC4)', () => {
  const validate = createEnvelopeValidator(contractsDirectory);

  it('packs the 500th event into one batch and splits the 501st off', async () => {
    const events = Array.from({ length: maxBatchEvents }, (_, index) => spoolEvent(index + 1));
    const batches = await collect(packBatches(sourceOf(events), options({ validate })));

    expect(batches).toHaveLength(1);
    expect(batches[0]!.events).toHaveLength(maxBatchEvents);
    expect(batches[0]!.firstSequence).toBe(1);
    expect(batches[0]!.lastSequence).toBe(500);
    expect(batches[0]!.body.byteLength).toBeLessThanOrEqual(maxBatchBytes);

    const withOneMore = [...events, spoolEvent(501)];
    const split = await collect(packBatches(sourceOf(withOneMore), options({ validate })));

    expect(split.map((batch) => batch.events.length)).toEqual([500, 1]);
    expect(split.map((batch) => [batch.firstSequence, batch.lastSequence])).toEqual([
      [1, 500],
      [501, 501],
    ]);
    for (const batch of split) {
      expect(batch.body.byteLength).toBeLessThanOrEqual(maxBatchBytes);
    }
  });

  it('fills a body to exactly 1_048_576 bytes and splits the next event off', async () => {
    // 13 overhead + 600_000 + 1 separator + 448_562 = 1_048_576.
    const firstLineBytes = 600_000;
    const secondLineBytes = maxBatchBytes - bodyOverheadBytes - 1 - firstLineBytes;
    const first = spoolEvent(1, { line: envelopeOfBytes(1, firstLineBytes) });
    const second = spoolEvent(2, { line: envelopeOfBytes(2, secondLineBytes) });
    const third = spoolEvent(3, { line: envelopeOfBytes(3, 400) });

    expect(Buffer.byteLength(expectedBody([first, second]), 'utf8')).toBe(maxBatchBytes);

    const batches = await collect(
      packBatches(sourceOf([first, second, third]), options({ validate: noopValidate })),
    );

    expect(batches.map((batch) => batch.events.length)).toEqual([2, 1]);
    expect(batches[0]!.body.byteLength).toBe(maxBatchBytes);
    expect(batches[1]!.firstSequence).toBe(3);
  });

  it('splits one byte earlier when the pair no longer fits', async () => {
    const firstLineBytes = 600_000;
    const secondLineBytes = maxBatchBytes - bodyOverheadBytes - 1 - firstLineBytes + 1;
    const first = spoolEvent(1, { line: envelopeOfBytes(1, firstLineBytes) });
    const second = spoolEvent(2, { line: envelopeOfBytes(2, secondLineBytes) });

    const batches = await collect(
      packBatches(sourceOf([first, second]), options({ validate: noopValidate })),
    );

    expect(batches.map((batch) => batch.events.length)).toEqual([1, 1]);
    expect(batches[0]!.body.byteLength).toBe(bodyOverheadBytes + firstLineBytes);
  });

  it('fails a single oversized event, naming event_id and sequence', async () => {
    // One byte over: `{"events":[<line>]}` would be 1_048_577.
    const oversized = spoolEvent(7, { line: envelopeOfBytes(7, maxBatchBytes - 12) });
    const error = await failure(
      collect(packBatches(sourceOf([oversized]), options({ validate: noopValidate }))),
    );

    expect(error).toBeInstanceOf(OversizedEventError);
    const thrown = error as OversizedEventError;
    expect(thrown.eventId).toBe(eventIdFor(7));
    expect(thrown.sequence).toBe(7);
    expect(thrown.bytes).toBe(maxBatchBytes + 1);
    expect(thrown.message).toContain(eventIdFor(7));
    expect(thrown.message).toContain('sequence 7');
    expect(thrown.message).toContain(String(maxBatchBytes));
  });

  it('accepts a single event whose body is exactly at the limit', async () => {
    const atLimit = spoolEvent(7, { line: envelopeOfBytes(7, maxBatchBytes - bodyOverheadBytes) });
    const batches = await collect(
      packBatches(sourceOf([atLimit]), options({ validate: noopValidate })),
    );

    expect(batches).toHaveLength(1);
    expect(batches[0]!.body.byteLength).toBe(maxBatchBytes);
  });

  it('concatenates the exact spool line texts, one allocation, no re-serialisation', async () => {
    const events = [1, 2, 3].map((sequence) => spoolEvent(sequence, { pad: 40 }));
    // A line carrying unusual-but-legal spacing must survive verbatim.
    const spaced = ' { "schema_version": 1 ,"sequence" : 9 } ';
    const withSpaced = [...events.slice(0, 2), spoolEvent(9, { line: spaced })];

    const batches = await collect(packBatches(sourceOf(withSpaced), options()));
    const text = batches[0]!.body.toString('utf8');

    expect(text).toBe(`{"events":[${withSpaced.map((event) => event.line).join(',')}]}`);
    expect(text).toContain(spaced);
    expect(batches[0]!.body.byteLength).toBe(Buffer.byteLength(expectedBody(withSpaced), 'utf8'));
    expect(batches[0]!.body.byteLength).toBe(
      bodyOverheadBytes +
        withSpaced.reduce((sum, event) => sum + Buffer.byteLength(event.line, 'utf8'), 0) +
        (withSpaced.length - 1),
    );
  });

  it('applies gzip only when it saves more than 1 KiB', async () => {
    const compressible = spoolEvent(1, { line: paddedLine(1, moderatelyCompressibleText()) });
    const [big] = await collect(packBatches(sourceOf([compressible]), options()));

    expect(big!.gzipBody).toBeDefined();
    expect(big!.gzipBody!.byteLength).toBeLessThanOrEqual(big!.body.byteLength * 0.9);
    expect(big!.body.byteLength - big!.gzipBody!.byteLength).toBeGreaterThan(1024);
    // …and the kept body stays inside the server's expansion cap (20:1 by default).
    expect(big!.body.byteLength).toBeLessThanOrEqual(big!.gzipBody!.byteLength * 19);
    expect(gunzipSync(big!.gzipBody!).equals(big!.body)).toBe(true);

    // The same compressible text in a body small enough that the saving is under
    // the 1 KiB floor: the `Content-Encoding` hop is not worth it.
    const small = spoolEvent(2, { pad: 600 });
    const [tiny] = await collect(packBatches(sourceOf([small]), options()));

    expect(tiny!.body.byteLength).toBeLessThan(1024);
    expect(tiny!.gzipBody).toBeUndefined();
  });

  it('sends the raw body when gzip would exceed the server expansion cap', async () => {
    // 500 near-identical ~1.7 KiB events: one full batch that gzip squashes far past
    // the server's 20:1 decompression-bomb cap, which would be a non-retryable 413
    // `batch_too_large` — while the raw 1 MiB body is always accepted.
    const events = Array.from({ length: maxBatchEvents }, (_, index) =>
      spoolEvent(index + 1, { pad: 1500 }),
    );
    const batches = await collect(packBatches(sourceOf(events), options()));

    expect(batches).toHaveLength(1);
    const batch = batches[0]!;
    expect(batch.events).toHaveLength(maxBatchEvents);
    expect(batch.body.byteLength).toBeGreaterThan(800_000);
    expect(batch.body.byteLength).toBeLessThanOrEqual(maxBatchBytes);

    // Fixture guard: this body really does compress past the server cap, i.e. the
    // gzip decision that used to ship it would have been refused.
    const naive = gzipSync(batch.body, { level: 6 });
    expect(naive.byteLength * 20).toBeLessThan(batch.body.byteLength);

    expect(batch.gzipBody).toBeUndefined();
  });

  it('keeps gzip for a body whose ratio stays under the cap', async () => {
    const line = paddedLine(1, moderatelyCompressibleText());
    const [batch] = await collect(packBatches(sourceOf([spoolEvent(1, { line })]), options()));

    expect(batch!.gzipBody).toBeDefined();
    const ratio = batch!.body.byteLength / batch!.gzipBody!.byteLength;
    expect(ratio).toBeGreaterThan(1);
    expect(ratio).toBeLessThanOrEqual(19);
    expect(gunzipSync(batch!.gzipBody!).equals(batch!.body)).toBe(true);
  });

  it('still compresses a high-entropy body and round-trips it exactly', async () => {
    const random = pseudoRandomBytes(150_000).toString('base64');
    const line = `${envelopeText(1).slice(0, -1)},"ext":{"schema":"${random}"}` + '}';
    const [batch] = await collect(packBatches(sourceOf([spoolEvent(1, { line })]), options()));

    expect(batch!.gzipBody).toBeDefined();
    expect(batch!.body.byteLength - batch!.gzipBody!.byteLength).toBeGreaterThan(1024);
    expect(gunzipSync(batch!.gzipBody!).equals(batch!.body)).toBe(true);
    expect(batch!.body.toString('utf8')).toBe(`{"events":[${line}]}`);
  });

  it('holds at most one batch and pulls the source only as far as needed', async () => {
    const events = Array.from({ length: 1201 }, (_, index) => spoolEvent(index + 1));
    const { pulled, iterable } = countingSource(events);
    const batches = packBatches(iterable, options());

    const first = await batches.next();
    expect(first.done).toBe(false);
    expect(pulled()).toBe(500);

    const second = await batches.next();
    expect(second.done).toBe(false);
    expect(pulled()).toBe(1000);

    const third = await batches.next();
    if (third.done === true) {
      throw new Error('expected a third batch');
    }
    expect(third.value.events).toHaveLength(201);
    expect(pulled()).toBe(1201);

    const end = await batches.next();
    expect(end.done).toBe(true);
    expect(pulled()).toBe(1201);
  });

  it('stops at an invalid line without pulling the rest of the source', async () => {
    const events = [
      spoolEvent(1, { pad: 16 }),
      spoolEvent(2, { type: 'Not-A-Type' }),
      spoolEvent(3, { pad: 16 }),
    ];
    const { pulled, iterable } = countingSource(events);
    const batches = packBatches(iterable, options({ validate }));

    const error = await failure(batches.next());

    expect(error).toBeInstanceOf(EnvelopeInvalidError);
    expect((error as EnvelopeInvalidError).sequence).toBe(2);
    expect(pulled()).toBe(2);
  });

  it('refuses a nonsensical limit instead of looping', async () => {
    const tooFewEvents = packBatches(sourceOf([spoolEvent(1)]), options({ maxEvents: 0 }));
    const tooFewBytes = packBatches(sourceOf([spoolEvent(1)]), options({ maxBytes: 4 }));

    expect(await failure(collect(tooFewEvents))).toBeInstanceOf(RangeError);
    expect(await failure(collect(tooFewBytes))).toBeInstanceOf(RangeError);
  });
});

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

describe('createEnvelopeValidator', () => {
  const validate = createEnvelopeValidator(contractsDirectory);

  /** The `EnvelopeInvalidError` a rejected line throws (never a stray assertion error). */
  function rejection(line: string, sequence: number): EnvelopeInvalidError {
    const error = thrownBy(() => validate(line, sequence));
    expect(error).toBeInstanceOf(EnvelopeInvalidError);
    return error as EnvelopeInvalidError;
  }

  it('accepts a valid envelope line', () => {
    expect(() => validate(envelopeText(12), 12)).not.toThrow();
  });

  it('rejects a schema-invalid envelope, naming the sequence and the event_id', () => {
    const invalid = envelopeText(12).replace('"type":"run.log"', '"type":"Test.Log"');
    const thrown = rejection(invalid, 12);

    expect(thrown.sequence).toBe(12);
    expect(thrown.eventId).toBe(eventIdFor(12));
    expect(thrown.details).toEqual([{ path: '/type', reason: 'pattern' }]);
  });

  it('reports path + reason only — never an event value', () => {
    const secret = 'Sekret-Token-Value';
    const invalid = envelopeText(4).replace('"run.log"', `"${secret}"`);
    const thrown = rejection(invalid, 4);

    expect(thrown.details).toEqual([{ path: '/type', reason: 'pattern' }]);
    expect(thrown.message).not.toContain(secret);
    expect(JSON.stringify(thrown.details)).not.toContain(secret);
  });

  it('rejects a batch-shaped line: one spool line is one envelope', () => {
    const batchLine = `{"events":[${envelopeText(1)}]}`;
    const thrown = rejection(batchLine, 1);

    // The batch's `events` member is unknown to the closed event object; the
    // wrapped `/events/0` prefix never leaks into a row.
    expect(thrown.details).toContainEqual({ path: '/events', reason: 'additional_properties' });
    expect(thrown.details.every((detail) => !detail.path.startsWith('/events/'))).toBe(true);
    // The batch is not a valid event, so it carries no event_id to name.
    expect(thrown.eventId).toBeUndefined();
  });

  it('rejects a line that is not JSON at all', () => {
    const thrown = rejection('{"schema_version":1,', 5);

    expect(thrown.sequence).toBe(5);
    expect(thrown.details).toEqual([{ path: '', reason: 'invalid_json' }]);
    expect(thrown.eventId).toBeUndefined();
  });

  it('caps an echoed member name at 200 code points and escapes control characters', () => {
    const hostileName = `secret_${'a'.repeat(5_000)}`;
    const hostilePath = `/${hostileName}`;
    const thrown = rejection(withExtraMember(7, hostileName), 7);

    // The `details[]` row keeps the exact pointer (machine-readable)…
    expect(thrown.details).toEqual([{ path: hostilePath, reason: 'additional_properties' }]);
    // …while the message echoes at most the first 200 code points of it, then an ellipsis.
    expect(thrown.message).toContain(`${hostilePath.slice(0, 200)}…`);
    expect(thrown.message).not.toContain(hostileName);
    expect(thrown.message.length).toBeLessThan(1_000);

    // A member name carrying a control character is escaped, never emitted raw.
    const controlName = 'bad\u0007name';
    const controlThrown = rejection(withExtraMember(8, controlName), 8);

    expect(controlThrown.details).toEqual([
      { path: `/${controlName}`, reason: 'additional_properties' },
    ]);
    expect(controlThrown.message).not.toContain('\u0007');
    expect(controlThrown.message).toContain('\\u0007');
  });

  it('locates the contracts tree without an explicit directory', () => {
    const resolved = createEnvelopeValidator();

    expect(() => resolved(envelopeText(1), 1)).not.toThrow();
  });

  it('fails loud when no contracts tree can be found', () => {
    expect(() => createEnvelopeValidator(join(repoRoot, 'no-such-contracts'))).toThrow(
      /no contract schema/,
    );
  });
});
