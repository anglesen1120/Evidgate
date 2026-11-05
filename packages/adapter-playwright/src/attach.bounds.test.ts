// The bounds of the pipeline: how many uploads may be in flight, and how many attachments a
// test and a run may carry before the overflow is skipped.
import { describe, expect, it } from 'vitest';

import { MAX_ATTACHMENTS_PER_TEST } from './attach.js';
import type { PlaywrightAttachment } from './attachMap.js';
import { attachment, codesOf, file, gate, harness, input } from './attach.testing.js';

describe('attachment pipeline (04.3.3-T2.2)', () => {
  it('keeps_at_most_two_uploads_in_flight (AC5/§7)', async () => {
    // The gate opens when the SECOND upload enters, so both are provably in flight at once and
    // a third can never be (the drain's pump is synchronous). `peak` is read live: with the
    // snapshot it used to return, an immediately-resolving fake made the old assertion pass at
    // zero. If the ceiling were 1 this case hangs and fails on its timeout instead of
    // passing vacuously.
    const startGate = gate();
    let started = 0;
    const { pipeline, uploader, root } = harness({
      uploads: async (_uploadInput, index) => {
        started += 1;
        if (started >= 2) startGate.open();
        await startGate.opened;
        return {
          artifactId: `0000000${String(index)}-0000-4000-8000-000000000000`,
          bytes: 8,
        };
      },
    });
    const files = Array.from({ length: 6 }, (_, index) =>
      file(root, `trace-c${String(index)}.zip`),
    );

    const result = await pipeline.process(
      input(
        files.map((entry, index) =>
          attachment(entry.path, `trace-c${String(index)}.zip`, 'application/zip'),
        ),
      ),
    );
    await pipeline.finish();

    expect(uploader.calls).toHaveLength(6);
    expect(uploader.peak).toBe(2);
    expect(result.artifactIds).toHaveLength(6);
  });

  it('bounds_one_test_and_the_run_with_one_attachment_limit_warning (§7 bounds)', async () => {
    const { pipeline, uploader, root } = harness();
    // One attachment over the per-test bound: the overflow is skipped, never uploaded.
    const png = (index: number): PlaywrightAttachment => {
      const name = `shot-${String(index)}.png`;
      return attachment(file(root, name).path, name, 'image/png');
    };
    const overBound = Array.from({ length: MAX_ATTACHMENTS_PER_TEST + 1 }, (_value, index) =>
      png(index),
    );

    const first = await pipeline.process(input(overBound));
    expect(first.records).toHaveLength(MAX_ATTACHMENTS_PER_TEST);
    expect(first.records.every((record) => record.outcome === 'uploaded')).toBe(true);
    expect(uploader.calls).toHaveLength(MAX_ATTACHMENTS_PER_TEST);
    const limits = first.warnings.filter((warning) => warning.code === 'attachment_limit');
    expect(limits).toHaveLength(1);
    expect(limits[0]?.count).toBe(1);

    // A second overflowing test in the same run skips its overflow too, but the summary is
    // ONE per run: a writer would otherwise repeat it for every test of a long suite.
    const second = await pipeline.process(input(overBound));
    await pipeline.finish();
    expect(second.records).toHaveLength(MAX_ATTACHMENTS_PER_TEST);
    expect(codesOf(second.warnings)).not.toContain('attachment_limit');
    expect(uploader.calls).toHaveLength(MAX_ATTACHMENTS_PER_TEST * 2);
  });
});
