// The guard in front of the uploader: the per-kind byte cap and the allow-list refusals that
// happen before any request is made.
import { mkdirSync, realpathSync, symlinkSync } from 'node:fs';
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

import { AttachmentPipeline } from './attach.js';
import {
  attachment,
  codesOf,
  fakeCli,
  fakeUploader,
  file,
  harness,
  input,
  recordOf,
  tempDir,
} from './attach.testing.js';

describe('attachment pipeline (04.3.3-T2.2)', () => {
  it('passes_at_the_cap_and_skips_one_byte_over_it_without_uploading (AC6)', async () => {
    const { pipeline, uploader, root } = harness({
      kindMaxBytes: { trace: 16, video: undefined, screenshot: undefined },
    });
    const atCap = file(root, 'trace-at.zip', 16);
    const overCap = file(root, 'trace-over.zip', 17);

    const result = await pipeline.process(
      input([
        attachment(atCap.path, 'trace-at.zip', 'application/zip'),
        attachment(overCap.path, 'trace-over.zip', 'application/zip'),
      ]),
    );
    await pipeline.finish();

    expect(uploader.calls).toHaveLength(1);
    expect(recordOf(result, 0)).toMatchObject({ name: 'trace-at.zip', outcome: 'uploaded' });
    expect(recordOf(result, 1)).toMatchObject({
      name: 'trace-over.zip',
      outcome: 'skipped',
      code: 'attachment_over_cap',
    });
    expect(codesOf(result.warnings)).toContain('attachment_over_cap');
  });

  it('refuses_outside_the_roots_a_symlink_a_dot_dot_path_and_before_any_upload (AC6)', async () => {
    // The root is a CHILD of a temp parent, so `..` names a real file that exists and is
    // still outside the allow-list (a nonexistent one would only prove `unreadable`).
    const parent = tempDir('evidgate-attach-parent-');
    const root = join(parent, 'root');
    mkdirSync(root, { recursive: true });
    const uploader = fakeUploader();
    const pipeline = new AttachmentPipeline({
      roots: [root],
      cwd: root,
      env: {},
      importCli: () => Promise.resolve(fakeCli(() => uploader.uploader)),
    });
    const outsideFile = file(parent, 'trace-outside.zip');
    const dotdot = file(parent, 'trace-dotdot.zip');
    const inside = file(root, 'trace-inside.zip');
    const link = join(root, 'trace-link.zip');
    symlinkSync(outsideFile.path, link);

    const result = await pipeline.process(
      input([
        attachment(outsideFile.path, 'trace-outside.zip', 'application/zip'),
        attachment(link, 'trace-link.zip', 'application/zip'),
        attachment(join(root, '..', 'trace-dotdot.zip'), 'trace-dotdot.zip', 'application/zip'),
        attachment(inside.path, 'trace-inside.zip', 'application/zip'),
      ]),
    );
    await pipeline.finish();

    const refused = result.records.filter((record) => record.outcome === 'skipped');
    expect(refused).toHaveLength(3);
    for (const record of refused) {
      expect(record.code).toBe('attachment_path_refused');
    }
    expect(recordOf(result, 3)).toMatchObject({ name: 'trace-inside.zip', outcome: 'uploaded' });
    // Only the allowed file reached the uploader: a refusal costs no request.
    expect(uploader.calls).toHaveLength(1);
    expect(uploader.calls[0]?.file).toBe(realpathSync(inside.path));
    expect(JSON.stringify(result)).not.toContain(parent);
    expect(dotdot.size).toBeGreaterThan(0);
  });
});
