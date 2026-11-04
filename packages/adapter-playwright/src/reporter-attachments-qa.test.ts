// Reporter wiring the delivered specs leave open: a hung attempt beside a healthy one, secret
// hygiene of an upload error, placeholder keys, merge-replay silence, environment attachment
// dirs, sequence contiguity while batches are held and a repeated `onEnd`.
import { existsSync, mkdirSync, realpathSync } from 'node:fs';
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

import type { AttachmentUploadResult, AttachmentUploader } from '@evidgate/cli';

import type { PlaywrightAttachment } from './attachMap.js';
import { EvidgateReporter } from './reporter.js';
import {
  ALL_CAPS,
  ARTIFACT_A,
  ARTIFACT_B,
  CASE_ID,
  EXECUTION_KEY,
  PRODUCER_ID,
  RUN_ID,
  VARIANT_HASH,
  attachment,
  codesIn,
  configFor,
  endOfRun,
  envelopesOf,
  fakeCli,
  fakePresigned,
  fakeResult,
  fakeTest,
  fakeToken,
  fakeUploader,
  file,
  fullResult,
  gate,
  markerOf,
  recordCodesOf,
  recordsOf,
  reporterHarness,
  spoolLines,
  tempDir,
  warnOf,
} from './attach-qa.testing.js';

describe('QA probe: reporter wiring beyond the delivered specs', () => {
  it('keeps a healthy attempt while another attempt hits the drain deadline', async () => {
    const root = tempDir('qa-s87-mixed-root-');
    const spool = tempDir('qa-s87-mixed-spool-');
    const hung = file(root, 'trace-hung.zip');
    const good = file(root, 'shot-good.png');
    const uploader = fakeUploader({
      // Keyed on the file, not on the call index: the guard's async checks may interleave.
      uploads: (upload) =>
        upload.file.includes('trace-hung')
          ? new Promise<AttachmentUploadResult>(() => undefined)
          : Promise.resolve({ artifactId: ARTIFACT_B, bytes: 8 }),
    });
    const { reporter } = reporterHarness(spool, root, () => uploader.uploader, {
      attachment_drain_deadline_ms: 50,
    });

    reporter.onBegin(configFor(root));
    const testA = fakeTest('hangs', 3);
    const resultA = fakeResult([attachment(hung.path, 'trace-hung.zip', 'application/zip')]);
    reporter.onTestBegin(testA, resultA);
    reporter.onTestEnd(testA, resultA);
    const testB = fakeTest('settles', 9);
    const resultB = fakeResult([attachment(good.path, 'shot-good.png', 'image/png')]);
    reporter.onTestBegin(testB, resultB);
    reporter.onTestEnd(testB, resultB);
    await endOfRun(reporter);

    const envelopes = envelopesOf(spool);
    const finished = envelopes.filter((item) => item.type === 'test.finished');
    expect(finished).toHaveLength(2);
    expect(finished[0]?.artifact_ids).toBeUndefined();
    expect(recordsOf(finished[0])[0]).toMatchObject({
      outcome: 'failed',
      code: 'attachment_drain_deadline',
    });
    expect(finished[1]?.artifact_ids).toEqual([ARTIFACT_B]);
    expect(codesIn(spool).filter((code) => code === 'attachment_drain_deadline')).toHaveLength(1);
    // Nothing may land after the run-end marker.
    const last = envelopes[envelopes.length - 1];
    expect(last === undefined ? undefined : markerOf(last)).toBe('run_end');
    expect(envelopes.map((item) => item.sequence)).toEqual(envelopes.map((_, index) => index + 1));
  });

  it('never forwards an upload error message that carries a presigned URL or the token', async () => {
    const root = tempDir('qa-s87-secret-root-');
    const spool = tempDir('qa-s87-secret-spool-');
    const trace = file(root, 'trace-secret.zip');
    const shot = file(root, 'shot-secret.png');
    const uploader: AttachmentUploader = {
      kindMaxBytes: { ...ALL_CAPS },
      upload(input) {
        if (input.kind === 'screenshot') {
          return Promise.reject(
            Object.assign(new Error(`PUT ${fakePresigned} rejected bearer ${fakeToken}`), {
              code: 'upload_put_failed',
              status: 500,
            }),
          );
        }
        // A `code` that is not a bare token (here: the presigned URL itself).
        return Promise.reject(
          Object.assign(new Error(`token ${fakeToken} refused`), { code: fakePresigned }),
        );
      },
    };
    const { reporter } = reporterHarness(spool, root, () => uploader);

    reporter.onBegin(configFor(root));
    const test = fakeTest('leaks nothing', 4);
    const result = fakeResult([
      attachment(trace.path, 'trace-secret.zip', 'application/zip'),
      attachment(shot.path, 'shot-secret.png', 'image/png'),
    ]);
    reporter.onTestBegin(test, result);
    reporter.onTestEnd(test, result);
    await endOfRun(reporter);

    for (const line of spoolLines(spool)) {
      expect(line).not.toContain('X-Amz');
      expect(line).not.toContain('store.test');
      expect(line).not.toContain(fakeToken);
      expect(line).not.toContain(root);
      expect(line).not.toContain(realpathSync(root));
    }
    const finished = envelopesOf(spool).find((item) => item.type === 'test.finished');
    expect(finished?.artifact_ids).toBeUndefined();
    expect(recordCodesOf(finished)).toEqual(['unknown', 'upload_put_failed']);
  });

  it('skips placeholder-key attachments with one warning carrying the count', async () => {
    const root = tempDir('qa-s87-placeholder-root-');
    const spool = tempDir('qa-s87-placeholder-spool-');
    let imported = false;
    const reporter = new EvidgateReporter({
      spool_dir: spool,
      run_id: RUN_ID,
      producer_id: PRODUCER_ID,
      engineVersion: '1.63.0',
      attachment_dirs: [root],
      importCli: () => {
        imported = true;
        return Promise.resolve(fakeCli(() => fakeUploader().uploader));
      },
    });
    const trace = file(root, 'trace-placeholder.zip');
    const shot = file(root, 'shot-placeholder.png');

    reporter.onBegin(configFor(root));
    const test = fakeTest('placeholder key', 5);
    const result = fakeResult([
      attachment(trace.path, 'trace-placeholder.zip', 'application/zip'),
      attachment(shot.path, 'shot-placeholder.png', 'image/png'),
    ]);
    reporter.onTestBegin(test, result);
    reporter.onTestEnd(test, result);
    const returned = reporter.onEnd(fullResult());
    expect(imported).toBe(false);

    const envelopes = envelopesOf(spool);
    const warnings = envelopes.filter(
      (item) => warnOf(item).code === 'attachment_no_execution_key',
    );
    expect(warnings).toHaveLength(1);
    expect(warnings[0] === undefined ? undefined : warnOf(warnings[0]).count).toBe(2);
    const finished = envelopes.find((item) => item.type === 'test.finished');
    expect(finished?.artifact_ids).toBeUndefined();
    expect(recordsOf(finished)).toHaveLength(0);
    // Nothing uploadable was pending, so the end sequence wrote every envelope above; the
    // unannotated run then trips the identity gate, whose failed status the promise carries.
    await expect(returned).resolves.toEqual({ status: 'failed' });
  });

  it('emits nothing in a merge-reports replay, even when a test has attachments', () => {
    const root = tempDir('qa-s87-merge-root-');
    const spool = tempDir('qa-s87-merge-spool-');
    let imported = false;
    const reporter = new EvidgateReporter({
      spool_dir: spool,
      run_id: RUN_ID,
      producer_id: PRODUCER_ID,
      engineVersion: '1.63.0',
      argv: ['merge-reports', 'blob-report'],
      attachment_dirs: [root],
      importCli: () => {
        imported = true;
        return Promise.resolve(fakeCli(() => fakeUploader().uploader));
      },
      identity: () => ({
        execution_key: EXECUTION_KEY,
        case_id: CASE_ID,
        variant_hash: VARIANT_HASH,
        target_env: 'test-env',
      }),
    });
    const trace = file(root, 'trace-merge.zip');

    reporter.onBegin(configFor(root));
    const test = fakeTest('merged replay', 6);
    const result = fakeResult([attachment(trace.path, 'trace-merge.zip', 'application/zip')]);
    reporter.onTestBegin(test, result);
    reporter.onTestEnd(test, result);
    const returned = reporter.onEnd(fullResult());

    expect(imported).toBe(false);
    expect(spoolLines(spool)).toEqual([]);
    expect(existsSync(join(spool, RUN_ID))).toBe(false);
    expect(typeof returned === 'object' && returned !== null && 'then' in returned).toBe(false);
  });

  it('resolves relative attachment dirs against rootDir and drops empty env segments', async () => {
    const root = tempDir('qa-s87-dirs-root-');
    const absolute = tempDir('qa-s87-dirs-abs-');
    const spool = tempDir('qa-s87-dirs-spool-');
    mkdirSync(join(root, 'extra'));
    const direct = file(root, 'shot-direct.png');
    const relative = file(join(root, 'extra'), 'shot-relative.png');
    const absoluteFile = file(absolute, 'shot-absolute.png');
    const uploader = fakeUploader();

    const previous = process.env.EVIDGATE_ATTACHMENT_DIRS;
    // Empty segments on both sides and a whitespace-padded relative entry: the empty ones
    // must be dropped (a kept empty segment would make rootDir itself an allowed root).
    process.env.EVIDGATE_ATTACHMENT_DIRS = `: extra :${absolute}::`;
    try {
      const { reporter } = reporterHarness(spool, root, () => uploader.uploader, {
        // No `attachment_dirs`: only the env list may allow a directory, so a kept empty
        // segment (which would resolve to rootDir) is visible as an upload instead of a skip.
        attachment_dirs: [],
      });
      reporter.onBegin(configFor(root));
      const cases: Array<[string, PlaywrightAttachment, number]> = [
        ['direct', attachment(direct.path, 'shot-direct.png', 'image/png'), 1],
        ['relative', attachment(relative.path, 'shot-relative.png', 'image/png'), 2],
        ['absolute', attachment(absoluteFile.path, 'shot-absolute.png', 'image/png'), 3],
      ];
      for (const [title, reference, line] of cases) {
        const test = fakeTest(title, line);
        const result = fakeResult([reference]);
        reporter.onTestBegin(test, result);
        reporter.onTestEnd(test, result);
      }
      await endOfRun(reporter);
    } finally {
      if (previous === undefined) delete process.env.EVIDGATE_ATTACHMENT_DIRS;
      else process.env.EVIDGATE_ATTACHMENT_DIRS = previous;
    }

    const finished = envelopesOf(spool).filter((item) => item.type === 'test.finished');
    expect(finished).toHaveLength(3);
    expect(recordsOf(finished[0])[0]).toMatchObject({
      name: 'shot-direct.png',
      outcome: 'skipped',
      code: 'attachment_path_refused',
    });
    expect(recordsOf(finished[1])[0]).toMatchObject({
      name: 'shot-relative.png',
      outcome: 'uploaded',
    });
    expect(recordsOf(finished[2])[0]).toMatchObject({
      name: 'shot-absolute.png',
      outcome: 'uploaded',
    });
  });

  it('keeps sequences contiguous when a no-attachment test sits between held batches', async () => {
    const root = tempDir('qa-s87-order-root-');
    const spool = tempDir('qa-s87-order-spool-');
    const first = file(root, 'trace-first.zip');
    const third = file(root, 'trace-third.zip');
    // A's upload settles only after C's answered, so the held batches cannot be in flight order.
    const settleGate = gate();
    const uploader = fakeUploader({
      uploads: async (upload) => {
        const isFirst = upload.file.includes('trace-first');
        if (isFirst) await settleGate.opened;
        else settleGate.open();
        return { artifactId: isFirst ? ARTIFACT_A : ARTIFACT_B, bytes: 8 };
      },
    });
    const { reporter } = reporterHarness(spool, root, () => uploader.uploader);

    reporter.onBegin(configFor(root));
    const testA = fakeTest('A held', 1);
    const resultA = fakeResult([attachment(first.path, 'trace-first.zip', 'application/zip')]);
    reporter.onTestBegin(testA, resultA);
    reporter.onTestEnd(testA, resultA);
    const testB = fakeTest('B plain', 2);
    const resultB = fakeResult([]);
    reporter.onTestBegin(testB, resultB);
    reporter.onTestEnd(testB, resultB);
    const testC = fakeTest('C held', 3);
    const resultC = fakeResult([attachment(third.path, 'trace-third.zip', 'application/zip')]);
    reporter.onTestBegin(testC, resultC);
    reporter.onTestEnd(testC, resultC);
    await endOfRun(reporter);

    const envelopes = envelopesOf(spool);
    expect(envelopes.map((item) => item.sequence)).toEqual(envelopes.map((_, index) => index + 1));
    const finished = envelopes.filter((item) => item.type === 'test.finished');
    expect(finished).toHaveLength(3);
    // Observation pinned for the report: a test WITHOUT attachments appends synchronously and
    // therefore overtakes a still-uploading earlier test. Contiguity holds; test-end order does
    // not (reporter.ts comment: "batches are appended in test-end order").
    expect(finished.map((item) => item.ext.title)).toEqual(['B plain', 'A held', 'C held']);
    const last = envelopes[envelopes.length - 1];
    expect(last === undefined ? undefined : markerOf(last)).toBe('run_end');
  });

  it('observes a second onEnd call after the drained arm resolved', async () => {
    const root = tempDir('qa-s87-onend-root-');
    const spool = tempDir('qa-s87-onend-spool-');
    const trace = file(root, 'trace-onend.zip');
    const { reporter } = reporterHarness(spool, root, () => fakeUploader().uploader);

    reporter.onBegin(configFor(root));
    const test = fakeTest('onEnd twice', 7);
    const result = fakeResult([attachment(trace.path, 'trace-onend.zip', 'application/zip')]);
    reporter.onTestBegin(test, result);
    reporter.onTestEnd(test, result);
    await endOfRun(reporter);

    const afterFirst = envelopesOf(spool).length;
    const second = await reporter.onEnd(fullResult());
    const envelopes = envelopesOf(spool);
    // Whatever the second call does, it must not append a second run-end marker or a batch.
    expect(envelopes.filter((item) => markerOf(item) === 'run_end')).toHaveLength(1);
    expect(envelopes.length).toBe(afterFirst);
    expect(envelopes.map((item) => item.sequence)).toEqual(envelopes.map((_, index) => index + 1));
    // Pinned observation: the closed writer refuses every append, so the second call reports
    // that buffered failure as a failed status instead of appending or throwing.
    expect(second).toEqual({ status: 'failed' });
  });
});
