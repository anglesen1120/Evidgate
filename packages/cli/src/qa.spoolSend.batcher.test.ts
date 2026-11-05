// The batcher boundaries the unit spec does not pin down: the 500/501 event edge, the exact
// 1 MiB single-event edge, order over varied sizes, a stop at the first invalid line, and the
// gzip floors on both sides. Sizes come from a fixed seed, so nothing flakes.
import { gunzipSync } from 'node:zlib';

import { describe, expect, it } from 'vitest';

import { OversizedEventError, maxBatchBytes, maxBatchEvents, packBatches } from './batcher.js';

import {
  eventIdFor,
  eventsOf,
  moderatelyCompressiblePad,
  noValidate,
  producer,
  runId,
  synth,
} from './qa.spoolSend.testing.js';
import { collect } from './spool.testing.js';

describe('batcher: boundaries (04.2.3 AC4, contracts/cli.md §11)', () => {
  it.each([
    [499, [499]],
    [500, [500]],
    [501, [500, 1]],
  ])('packs %i small events into batches of %j', async (count, sizes) => {
    const events = Array.from({ length: count }, (_, i) => synth(i + 1, 0));
    const batches = await collect(
      packBatches(eventsOf(events), { runId, producerId: producer, validate: noValidate }),
    );
    expect(batches.map((b) => b.events.length)).toEqual(sizes);
    expect(batches.flatMap((b) => b.events.map((e) => e.sequence))).toEqual(
      events.map((e) => e.sequence),
    );
  });

  it('finds the exact 1 MiB single-event edge: the largest fitting line packs, one byte more is oversized', async () => {
    const fits = async (pad: number): Promise<boolean> => {
      try {
        await collect(
          packBatches(eventsOf([synth(1, pad)]), {
            runId,
            producerId: producer,
            validate: noValidate,
          }),
        );
        return true;
      } catch (error) {
        if (error instanceof OversizedEventError) {
          return false;
        }
        throw error;
      }
    };
    let lo = 0;
    let hi = maxBatchBytes;
    while (lo < hi) {
      const mid = Math.ceil((lo + hi) / 2);
      if (await fits(mid)) {
        lo = mid;
      } else {
        hi = mid - 1;
      }
    }
    const [batch] = await collect(
      packBatches(eventsOf([synth(1, lo)]), { runId, producerId: producer, validate: noValidate }),
    );
    expect(batch?.body.length ?? Number.POSITIVE_INFINITY).toBeLessThanOrEqual(maxBatchBytes);
    expect(await fits(lo + 1)).toBe(false);
    await expect(
      collect(
        packBatches(eventsOf([synth(1, lo + 1)]), {
          runId,
          producerId: producer,
          validate: noValidate,
        }),
      ),
    ).rejects.toMatchObject({ eventId: eventIdFor(1), sequence: 1 });
  });

  it('keeps every batch within the byte cap and the original order over varied sizes', async () => {
    // A fixed-seed LCG: deterministic sizes, no flake, no RNG in the spec.
    let state = 0x2f6e2b1;
    const next = (): number => {
      state = (Math.imul(state, 1664525) + 1013904223) >>> 0;
      return state / 0x100000000;
    };
    const events = Array.from({ length: 1500 }, (_, i) =>
      synth(i + 1, Math.floor(next() * 40_000)),
    );
    const batches = await collect(
      packBatches(eventsOf(events), { runId, producerId: producer, validate: noValidate }),
    );
    for (const batch of batches) {
      expect(batch.body.length).toBeLessThanOrEqual(maxBatchBytes);
      expect(batch.events.length).toBeLessThanOrEqual(maxBatchEvents);
    }
    expect(batches.flatMap((b) => b.events.map((e) => e.sequence))).toEqual(
      events.map((e) => e.sequence),
    );
  });

  it('stops at the first invalid line: no batch containing it or anything after it is yielded', async () => {
    const events = [synth(1, 0), synth(2, 0), synth(3, 0), synth(4, 0)];
    const seen: number[][] = [];
    const bad = packBatches(eventsOf(events), {
      runId,
      producerId: producer,
      maxEvents: 2,
      validate: (_line, sequence) => {
        if (sequence === 3) {
          throw new Error('schema refused sequence 3');
        }
      },
    });
    await expect(
      (async () => {
        for await (const batch of bad) {
          seen.push(batch.events.map((e) => e.sequence));
        }
      })(),
    ).rejects.toThrow('schema refused sequence 3');
    expect(seen).toEqual([[1, 2]]);
  });

  it('gzips a moderately compressible body (about 5:1) and the payload decodes back to the exact body', async () => {
    const pad = moderatelyCompressiblePad();
    const [batch] = await collect(
      packBatches(eventsOf([synth(1, pad.length, pad)]), {
        runId,
        producerId: producer,
        validate: noValidate,
      }),
    );
    expect(batch?.gzipBody).toBeDefined();
    expect(batch?.gzipBody?.length).toBeLessThan(batch?.body.length ?? 0);
    expect(
      gunzipSync(batch?.gzipBody ?? Buffer.alloc(0)).equals(batch?.body ?? Buffer.alloc(1)),
    ).toBe(true);
  });

  it('drops gzip when it would expand more than 20:1 (server MaxExpansionRatio)', async () => {
    const [batch] = await collect(
      packBatches(eventsOf([synth(1, 200_000)]), {
        runId,
        producerId: producer,
        validate: noValidate,
      }),
    );
    expect(batch?.body.length).toBeGreaterThan(200_000);
    expect(batch?.gzipBody).toBeUndefined();
  });

  it('does not gzip a small body: the saving is under the 1 KiB floor', async () => {
    const [batch] = await collect(
      packBatches(eventsOf([synth(1, 0)]), { runId, producerId: producer, validate: noValidate }),
    );
    expect(batch?.gzipBody).toBeUndefined();
  });
});
