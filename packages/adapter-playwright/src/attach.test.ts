// The attachment pipeline on its own: the upload happy path, the failure paths that must
// never lose a test result, and the per-kind sensitivity rules.
import { realpathSync } from 'node:fs';

import { describe, expect, it } from 'vitest';

import { AttachmentPipeline, type CliModule } from './attach.js';
import {
  ARTIFACT_A,
  EXECUTION_KEY,
  PRODUCER_ID,
  RUN_ID,
  attachment,
  codesOf,
  fakePresigned,
  fakeToken,
  file,
  harness,
  input,
  recordOf,
  tempDir,
} from './attach.testing.js';

describe('attachment pipeline (04.3.3-T2.2)', () => {
  it('uploads_one_attachment_and_returns_the_artifact_id (AC5)', async () => {
    const { pipeline, uploader, root } = harness();
    const trace = file(root, 'trace-attach.zip');

    const result = await pipeline.process(
      input([attachment(trace.path, 'trace-attach.zip', 'application/zip')]),
    );
    await pipeline.finish();

    expect(result.artifactIds).toEqual([ARTIFACT_A]);
    expect(recordOf(result)).toEqual({
      name: 'trace-attach.zip',
      kind: 'trace',
      // A trace is sensitive by default, with no annotation anywhere.
      sensitive: true,
      outcome: 'uploaded',
      artifact_id: ARTIFACT_A,
    });
    expect(result.warnings).toEqual([]);
    expect(uploader.calls).toHaveLength(1);
    expect(uploader.calls[0]).toEqual({
      runId: RUN_ID,
      producerId: PRODUCER_ID,
      executionKey: EXECUTION_KEY,
      attemptNo: 1,
      kind: 'trace',
      mediaType: 'application/zip',
      // The upload reads the guard's REAL path, never the nominal one.
      file: realpathSync(trace.path),
    });
    expect(pipeline.hasPending).toBe(false);
  });

  it('records_an_unplanned_attempt_as_failed_without_an_id (AC5, 422)', async () => {
    const refusal = Object.assign(new Error(`422 unknown_execution_key for ${fakePresigned}`), {
      code: 'unknown_execution_key',
      status: 422,
      name: 'UploadFailedError',
    });
    const { pipeline, root } = harness({ uploads: async () => Promise.reject(refusal) });
    const trace = file(root, 'trace-422.zip');

    const result = await pipeline.process(
      input([attachment(trace.path, 'trace-422.zip', 'application/zip')]),
    );
    await pipeline.finish();

    expect(result.artifactIds).toEqual([]);
    expect(recordOf(result)).toMatchObject({
      name: 'trace-422.zip',
      outcome: 'failed',
      code: 'unknown_execution_key',
    });
    expect(recordOf(result).artifact_id).toBeUndefined();
    const failure = result.warnings.find((warning) => warning.code === 'attachment_upload_failed');
    expect(failure?.message).toContain('trace-422.zip');
    expect(failure?.message).toContain('unknown_execution_key');
    // The server's message can embed a presigned URL; it is never forwarded.
    expect(failure?.message).not.toContain('X-Amz-Signature');
    expect(JSON.stringify(result)).not.toContain(fakePresigned);
  });

  it('marks_an_annotated_attachment_sensitive_and_reports_the_wire_gap (AC7)', async () => {
    const { pipeline, root } = harness();
    const shot = file(root, 'screenshot-sensitive.png');

    const result = await pipeline.process(
      input([attachment(shot.path, 'screenshot-sensitive.png', 'image/png')], {
        sensitiveAnnotated: true,
      }),
    );
    await pipeline.finish();

    expect(recordOf(result).sensitive).toBe(true);
    expect(recordOf(result).outcome).toBe('uploaded');
    const gap = result.warnings.find(
      (warning) => warning.code === 'attachment_sensitivity_not_carried',
    );
    expect(gap).toBeDefined();
    // Name and kind only: no directory, no URL.
    expect(gap?.message).toContain('screenshot-sensitive.png');
    expect(gap?.message).toContain('screenshot');
    expect(gap?.message).not.toContain(root);
  });

  it('does_not_warn_when_the_kind_is_sensitive_by_default (AC7)', async () => {
    const { pipeline, root } = harness();
    const trace = file(root, 'trace-default.zip');

    const result = await pipeline.process(
      input([attachment(trace.path, 'trace-default.zip', 'application/zip')], {
        sensitiveAnnotated: true,
      }),
    );
    await pipeline.finish();

    expect(recordOf(result).sensitive).toBe(true);
    expect(codesOf(result.warnings)).toEqual([]);
  });

  it('warns_once_for_the_run_when_the_cli_is_unconfigured (Q3)', async () => {
    const root = tempDir('evidgate-attach-unconfigured-');
    let factoryCalls = 0;
    const missingConfig = Object.assign(new Error(`missing configuration: ${fakeToken}`), {
      name: 'CliError',
      code: 'missing_config',
    });
    const pipeline = new AttachmentPipeline({
      roots: [root],
      cwd: root,
      env: {},
      // The module loads; the CLI's own config resolution is what fails.
      importCli: () => {
        factoryCalls += 1;
        return Promise.resolve({
          createAttachmentUploader: () => {
            throw missingConfig;
          },
        } as unknown as CliModule);
      },
    });
    const first = [file(root, 'trace-u1.zip'), file(root, 'trace-u2.zip')];
    const second = [file(root, 'trace-u3.zip')];

    const a = await pipeline.process(
      input(first.map((entry) => attachment(entry.path, 'trace-u1.zip', 'application/zip'))),
    );
    const b = await pipeline.process(
      input(second.map((entry) => attachment(entry.path, 'trace-u3.zip', 'application/zip'))),
    );
    await pipeline.finish();

    const warnings = [...a.warnings, ...b.warnings];
    expect(codesOf(warnings).filter((code) => code === 'attachments_unconfigured')).toHaveLength(1);
    for (const result of [a, b]) {
      expect(result.artifactIds).toEqual([]);
      for (const record of result.records) {
        expect(record.outcome).toBe('skipped');
        expect(record.code).toBe('attachments_unconfigured');
      }
    }
    // The import is attempted once (the memoised load), never once per attachment.
    expect(factoryCalls).toBe(1);
    expect(JSON.stringify(warnings)).not.toContain(fakeToken);
  });

  it('warns_once_for_the_run_when_the_cli_import_fails (Q1)', async () => {
    const root = tempDir('evidgate-attach-noimport-');
    const pipeline = new AttachmentPipeline({
      roots: [root],
      cwd: root,
      env: {},
      importCli: () => Promise.reject(new Error(`Cannot find module '@evidgate/cli' from ${root}`)),
    });
    const files = [file(root, 'trace-i1.zip'), file(root, 'trace-i2.zip')];

    const result = await pipeline.process(
      input(files.map((entry) => attachment(entry.path, 'trace-i1.zip', 'application/zip'))),
    );
    await pipeline.finish();

    expect(codesOf(result.warnings)).toEqual(['attachments_unavailable']);
    expect(result.artifactIds).toEqual([]);
    // The import error's text (which names module paths) is never forwarded.
    expect(JSON.stringify(result)).not.toContain(root);
    expect(JSON.stringify(result)).not.toContain('Cannot find module');
  });
});
