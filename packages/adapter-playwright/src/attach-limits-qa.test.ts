// Per-run and per-test attachment bounds, per-attempt bookkeeping, artifact-id ordering and
// the two-upload ceiling.
import { describe, expect, it } from 'vitest';

import {
  MAX_ATTACHMENTS_PER_RUN,
  MAX_ATTACHMENTS_PER_TEST,
  type AttachmentProcessResult,
} from './attach.js';
import {
  ARTIFACT_A,
  ARTIFACT_B,
  EXECUTION_KEY,
  PRODUCER_ID,
  attachment,
  file,
  gate,
  harness,
  input,
  png,
  recordAt,
} from './attach-qa.testing.js';

describe('QA probe: §7 bounds and per-attempt bookkeeping', () => {
  it('considers exactly 1000 attachments per run and warns once for the 1001st', async () => {
    const { pipeline, uploader, root } = harness();
    const fifty = Array.from({ length: MAX_ATTACHMENTS_PER_TEST }, (_value, index) =>
      png(root, index),
    );

    const results: AttachmentProcessResult[] = [];
    for (let attempt = 1; attempt <= 21; attempt += 1) {
      results.push(await pipeline.process(input(fifty, { attemptNo: attempt })));
    }
    await pipeline.finish();

    const considered = results.reduce((sum, result) => sum + result.records.length, 0);
    expect(considered).toBe(MAX_ATTACHMENTS_PER_RUN);
    expect(uploader.calls).toHaveLength(MAX_ATTACHMENTS_PER_RUN);
    const limits = results
      .flatMap((result) => result.warnings)
      .filter((warning) => warning.code === 'attachment_limit');
    // 21 × 50 = 1050 attachments: the 21st attempt contributes 50 overflow entries, and the
    // 50-per-test bound never fires on its own (no warning for attempts 1..20).
    expect(limits).toHaveLength(1);
    expect(limits[0]?.count).toBe(50);
    expect(results[20]?.records).toHaveLength(0);
  });

  it('uploads the same file once per attempt that references it', async () => {
    const { pipeline, uploader, root } = harness();
    const shot = file(root, 'shot-shared.png');
    const reference = attachment(shot.path, 'shot-shared.png', 'image/png');

    const first = await pipeline.process(input([reference], { attemptNo: 1 }));
    const second = await pipeline.process(input([reference], { attemptNo: 2 }));
    await pipeline.finish();

    expect(uploader.calls.map((call) => call.attemptNo)).toEqual([1, 2]);
    expect(uploader.calls.map((call) => call.executionKey)).toEqual([EXECUTION_KEY, EXECUTION_KEY]);
    expect(uploader.calls.map((call) => call.producerId)).toEqual([PRODUCER_ID, PRODUCER_ID]);
    expect(first.artifactIds).toEqual([ARTIFACT_A]);
    expect(second.artifactIds).toEqual([ARTIFACT_B]);
  });

  it('orders artifact_ids by attachment, not by settle order', async () => {
    // The FIRST attachment's upload is released only after the second one answered: the
    // second settles first, so an id list built from settle order would come back reversed.
    // Keyed on the file, never on the call index: two guards resolve concurrently. Hence the
    // call-order note: `drain.ts:16-17` promises FIFO *start*, and `attach.ts` awaits
    // `guard.check()` before `uploader.upload()`, so the PUT calls themselves can be issued
    // in the opposite order (a call-index keyed fake saw the second attachment first).
    const firstGate = gate();
    const { pipeline, root } = harness({
      uploads: async (uploadInput) => {
        if (uploadInput.file.endsWith('shot-1.png')) {
          await firstGate.opened;
          return { artifactId: ARTIFACT_A, bytes: 8 };
        }
        firstGate.open();
        return { artifactId: ARTIFACT_B, bytes: 8 };
      },
    });
    const result = await pipeline.process(input([png(root, 1), png(root, 2)]));
    await pipeline.finish();

    expect(result.artifactIds).toEqual([ARTIFACT_A, ARTIFACT_B]);
    expect(result.records.map((record) => record.outcome)).toEqual(['uploaded', 'uploaded']);
  });

  it('keeps the two-upload ceiling across concurrent attempts', async () => {
    // The gate opens when the SECOND upload enters, so both are provably in flight at once
    // and a third can never be (the drain's pump is synchronous). If the ceiling were 1 this
    // hangs and the spec fails on its timeout instead of passing vacuously.
    const startGate = gate();
    let started = 0;
    const { pipeline, uploader, root } = harness({
      uploads: async (_input, index) => {
        started += 1;
        if (started >= 2) startGate.open();
        await startGate.opened;
        return { artifactId: index % 2 === 0 ? ARTIFACT_A : ARTIFACT_B, bytes: 8 };
      },
    });
    await Promise.all([
      pipeline.process(input([png(root, 1), png(root, 2), png(root, 3)], { attemptNo: 1 })),
      pipeline.process(input([png(root, 4), png(root, 5), png(root, 6)], { attemptNo: 2 })),
    ]);
    await pipeline.finish();

    expect(uploader.calls).toHaveLength(6);
    expect(uploader.peak).toBe(2);
  });

  it('refuses an upload result whose artifact id is not a UUID', async () => {
    const { pipeline, root } = harness({
      uploads: () => Promise.resolve({ artifactId: 'not-a-uuid', bytes: 8 }),
    });
    const result = await pipeline.process(input([png(root, 1)]));
    await pipeline.finish();

    expect(result.artifactIds).toEqual([]);
    expect(recordAt(result)).toMatchObject({
      outcome: 'failed',
      code: 'attachment_upload_failed',
    });
  });
});
