// The spool side of the pipeline: the test result a failed upload must not lose, the secret
// shapes that must never reach a line, and an attempt with no planned execution key.
import { realpathSync } from 'node:fs';

import { describe, expect, it } from 'vitest';

import {
  PRODUCER_ID,
  RUN_ID,
  attachment,
  configFor,
  endOfRun,
  fakePresigned,
  fakeResult,
  fakeTest,
  fakeToken,
  fakeUploader,
  file,
  fullResult,
  makeReporter,
  readEnvelopesAt,
  readSpoolLinesAt,
  recordsOf,
  tempDir,
  warningCodes,
} from './attach.testing.js';
import { EvidgateReporter } from './reporter.js';

describe('attachment pipeline (04.3.3-T2.2)', () => {
  it('keeps_the_test_result_when_the_upload_fails (AC5, forced 5xx)', async () => {
    const serverError = Object.assign(new Error('the artifact upload failed (HTTP 503)'), {
      code: 'request_failed',
      status: 503,
    });
    const root = tempDir('evidgate-attach-out-');
    const spool = tempDir('evidgate-attach-spool-');
    const trace = file(root, 'trace-5xx.zip');
    const reporter = makeReporter(
      spool,
      root,
      () => fakeUploader({ uploads: async () => Promise.reject(serverError) }).uploader,
    );

    reporter.onBegin(configFor(root));
    const test = fakeTest();
    const result = fakeResult([attachment(trace.path, 'trace-5xx.zip', 'application/zip')]);
    reporter.onTestBegin(test, result);
    reporter.onTestEnd(test, result);
    await endOfRun(reporter);

    const envelopes = readEnvelopesAt(spool);
    const finished = envelopes.filter((item) => item.type === 'test.finished');
    expect(finished).toHaveLength(1);
    // The result is preserved: the real status, and no id for a failed upload.
    expect(finished[0]?.status).toBe('passed');
    expect(finished[0]?.artifact_ids).toBeUndefined();
    const records = recordsOf(finished[0]);
    expect(records).toHaveLength(1);
    expect(records[0]).toMatchObject({
      name: 'trace-5xx.zip',
      outcome: 'failed',
      code: 'request_failed',
    });
    expect(warningCodes(envelopes)).toContain('attachment_upload_failed');
    expect(envelopes.map((item) => item.sequence)).toEqual(envelopes.map((_, index) => index + 1));
  });

  it('never_leaks_a_presigned_url_the_token_or_a_directory_into_the_spool (§8 S3)', async () => {
    const root = tempDir('evidgate-attach-secret-');
    const spool = tempDir('evidgate-attach-secret-spool-');
    const trace = file(root, 'trace-secret.zip');
    const leaky = Object.assign(
      new Error(`failed: ${fakePresigned} with ${fakeToken} in ${root}`),
      { code: 'request_failed', status: 500 },
    );
    const reporter = makeReporter(
      spool,
      root,
      () => fakeUploader({ uploads: async () => Promise.reject(leaky) }).uploader,
    );
    const test = fakeTest();
    const result = fakeResult([attachment(trace.path, 'trace-secret.zip', 'application/zip')]);

    reporter.onBegin(configFor(root));
    reporter.onTestBegin(test, result);
    reporter.onTestEnd(test, result);
    await endOfRun(reporter);

    const lines = readSpoolLinesAt(spool);
    expect(lines.length).toBeGreaterThan(0);
    for (const line of lines) {
      expect(line).not.toContain('X-Amz-Signature');
      expect(line).not.toContain(fakeToken);
      expect(line).not.toContain(root);
      expect(line).not.toContain(realpathSync(root));
    }
  });

  it('skips_every_attachment_of_an_attempt_without_a_planned_execution_key', async () => {
    const root = tempDir('evidgate-attach-placeholder-');
    const trace = file(root, 'trace-placeholder.zip');
    const spool = tempDir('evidgate-attach-placeholder-spool-');
    const reporter = new EvidgateReporter({
      spool_dir: spool,
      run_id: RUN_ID,
      producer_id: PRODUCER_ID,
      engineVersion: '1.63.0',
      importCli: () =>
        Promise.reject(new Error('the pipeline must not load the CLI for a placeholder attempt')),
    });
    const test = fakeTest();
    const result = fakeResult([attachment(trace.path, 'trace-placeholder.zip', 'application/zip')]);

    reporter.onBegin(configFor(root));
    reporter.onTestBegin(test, result);
    reporter.onTestEnd(test, result);
    // No upload is pending, so the end sequence already wrote every envelope; this
    // unannotated run then trips the identity gate, whose failed status the promise carries.
    const returned = reporter.onEnd(fullResult());

    const envelopes = readEnvelopesAt(spool);
    expect(envelopes.find((item) => item.type === 'test.finished')?.artifact_ids).toBeUndefined();
    expect(warningCodes(envelopes)).toContain('attachment_no_execution_key');
    await expect(returned).resolves.toEqual({ status: 'failed' });
  });
});
