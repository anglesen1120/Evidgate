// The events cap counts the wire body — the gzip bytes when the batch travels compressed,
// the raw bytes otherwise — against the server's 1 MiB-before-decompression rule, and equals
// what the packer may produce, so a legal batch is never refused on the hot path.
import { describe, expect, it } from 'vitest';

import { maxBatchBytes, packBatches } from './batcher.js';
import type { Batch } from './batcher.js';
import {
  bodyOverheadBytes,
  collect,
  envelopeOfBytes,
  noopValidate,
  options,
  sourceOf,
  spoolEvent,
} from './batcher.testing.js';
import { CliError } from './errors.js';
import { acked, harness, ignoreAck, sendOptions } from './sender.testing.js';
import { sendBatches } from './sender.js';

async function exactMaxBatch(): Promise<Batch> {
  const first = 600_000;
  const second = maxBatchBytes - bodyOverheadBytes - 1 - first;
  const batches = await collect(
    packBatches(
      sourceOf([
        spoolEvent(1, { line: envelopeOfBytes(1, first) }),
        spoolEvent(2, { line: envelopeOfBytes(2, second) }),
      ]),
      options({ validate: noopValidate }),
    ),
  );
  return batches[0]!;
}

async function send(batch: Batch, h: ReturnType<typeof harness>): Promise<unknown> {
  async function* one(): AsyncGenerator<Batch> {
    await Promise.resolve();
    yield batch;
  }
  return sendBatches(one(), sendOptions(), h.deps, ignoreAck).then(
    () => undefined,
    (error: unknown) => error,
  );
}

describe('events body cap', () => {
  it('sends the largest batch the packer can produce, identity and gzip', async () => {
    const packed = await exactMaxBatch();
    expect(packed.body.byteLength).toBe(maxBatchBytes);
    const identity: Batch = { ...packed };
    delete (identity as { gzipBody?: Buffer }).gzipBody;
    const gzip: Batch = { ...packed, gzipBody: Buffer.alloc(maxBatchBytes, 1) };
    for (const batch of [identity, gzip]) {
      const h = harness(() => acked(packed.body));
      expect(await send(batch, h)).toBeUndefined();
      expect(h.calls).toHaveLength(1);
      expect(h.calls[0]!.body.byteLength).toBe(
        batch.gzipBody === undefined ? maxBatchBytes : batch.gzipBody.byteLength,
      );
    }
  });

  it.each(['identity', 'gzip'] as const)(
    'refuses a %s wire body one byte over the cap with zero requests and one attempt',
    async (form) => {
      const packed = await exactMaxBatch();
      const over = Buffer.alloc(maxBatchBytes + 1, 1);
      const plain: Batch = { ...packed, body: over };
      delete (plain as { gzipBody?: Buffer }).gzipBody;
      const batch: Batch = form === 'gzip' ? { ...packed, gzipBody: over } : plain;
      const h = harness(() => acked(packed.body));
      const outcome = await send(batch, h);
      expect(outcome).toBeInstanceOf(CliError);
      expect((outcome as CliError).code).toBe('request_too_large');
      expect(h.calls).toHaveLength(0);
      expect(h.sleeps).toHaveLength(0);
    },
  );
});
