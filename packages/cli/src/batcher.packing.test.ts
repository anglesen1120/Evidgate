// `packBatches` behaviour: the 500-event and 1 MiB body limits, the gzip decision against
// the server's 20:1 expansion cap, and the streaming contract — one batch held at a time and
// the source pulled only as far as the batch in hand needs.
import { gunzipSync, gzipSync } from 'node:zlib';

import { describe, expect, it } from 'vitest';

import {
  EnvelopeInvalidError,
  OversizedEventError,
  createEnvelopeValidator,
  maxBatchBytes,
  maxBatchEvents,
  packBatches,
} from './batcher.js';
import {
  bodyOverheadBytes,
  collect,
  contractsDirectory,
  countingSource,
  envelopeOfBytes,
  envelopeText,
  eventIdFor,
  expectedBody,
  failure,
  moderatelyCompressibleText,
  noopValidate,
  options,
  paddedLine,
  pseudoRandomBytes,
  sourceOf,
  spoolEvent,
} from './batcher.testing.js';

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
