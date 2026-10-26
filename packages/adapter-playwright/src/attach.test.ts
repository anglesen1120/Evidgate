// 04.3.3-T2.2: the attachment pipeline (AC5/AC7, plan §9 `attach.test.ts`) — the upload happy
// path, the failure paths that must never lose a test result, the AC7 sensitivity rules, the
// ceiling of the refusals (allow-list, per-kind cap), the concurrency bound and the drain
// deadline. The CLI is never loaded for real: `importCli` is the seam the reporter gives the
// pipeline (Q1), so each case injects a fake module surface and a fake uploader and the only
// real I/O is a temp directory holding the attachment files.
//
// What the spec pins, one bullet per acceptance row of the task:
// - AC5: an accepted attachment yields the artifact id on the record and in `artifact_ids`;
// - AC5: a 422 unplanned attempt and a forced 5xx are recorded `failed` with the CLI/server
//   code and NO id — the `test.finished` is still emitted with the real status and result;
// - AC7: the annotation makes a record `sensitive`, a trace is sensitive by default, and an
//   annotated non-trace kind emits `attachment_sensitivity_not_carried` (name + kind only);
// - Q3: an unconfigured CLI is ONE `attachments_unconfigured` per run, whatever the count;
// - Q1: a failed `@evidgate/cli` import is ONE `attachments_unavailable` per run;
// - AC6: over the cap, outside the roots, through a symlink and through `..` are all refused
//   BEFORE the uploader is called (the fake counts its calls);
// - §7 bounds: at most `MAX_ATTACHMENTS_PER_TEST` per test and `MAX_ATTACHMENTS_PER_RUN` per
//   run are considered, the overflow never reaches the uploader, and the `attachment_limit`
//   summary is emitted ONCE per run;
// - §7 Performance: never more than two uploads in flight;
// - §8 S3: no presigned URL, token or directory path ever reaches an envelope;
// - NFR-04-1: a hung upload is cut off by `attachment_drain_deadline_ms`; the abandoned
//   attachment has no artifact id, while an attempt's completed uploads keep theirs.
import {
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  realpathSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import type { FullConfig } from '@playwright/test/reporter';
import { afterEach, describe, expect, it } from 'vitest';

import type {
  AttachmentUploadContext,
  AttachmentUploadInput,
  AttachmentUploadResult,
  AttachmentUploader,
} from '@evidgate/cli';

import {
  AttachmentPipeline,
  MAX_ATTACHMENTS_PER_TEST,
  type AttachmentProcessResult,
  type AttachmentWarning,
  type CliModule,
} from './attach.js';
import type { AttachmentRecord } from './attach.js';
import type { PlaywrightAttachment } from './attachMap.js';
import type { EventEnvelope } from './envelope.js';
import { EvidgateReporter, type EvidgateReporterOptions } from './reporter.js';

const RUN_ID = 'run-attach-1';
const PRODUCER_ID = 'shard-1';
const EXECUTION_KEY = 'a1'.repeat(32);
const CASE_ID = '0f9a6c3e-1b2d-4c5e-8f70-a1b2c3d4e5f6';
const VARIANT_HASH = `sha256:${'cd'.repeat(32)}`;
/** Two artifact ids in canonical UUID form; the fake uploader answers with these. */
const ARTIFACT_A = '11111111-1111-4111-8111-111111111111';
const ARTIFACT_B = '22222222-2222-4222-8222-222222222222';
/** Assembled at runtime so no token- or URL-shaped literal is stored in the spec (T78). */
const fakeToken = ['attach', 'test', 'token'].join('-');
const fakePresigned = `https://store.test/put?X-Amz-Signature=${'abc123'}`;

const tempDirs: string[] = [];

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function tempDir(prefix: string): string {
  const dir = mkdtempSync(join(tmpdir(), prefix));
  tempDirs.push(dir);
  return dir;
}

/** A real file under `dir`; returns its path and byte length. */
function file(dir: string, name: string, size = 8): { path: string; size: number } {
  const path = join(dir, name);
  writeFileSync(path, Buffer.alloc(size, 0x50));
  return { path, size };
}

function attachment(path: string, name: string, contentType: string): PlaywrightAttachment {
  return { name, path, body: undefined, contentType };
}

interface FakeUploader {
  uploader: AttachmentUploader;
  /** Every input the uploader was asked for, in call order. */
  calls: AttachmentUploadInput[];
  /** The caller context of each call (drain signal and per-upload budget), in call order. */
  contexts: AttachmentUploadContext[];
  /** A LIVE read: the count keeps rising while the uploads run, never a snapshot at zero. */
  readonly peak: number;
}

interface FakeUploaderOptions {
  readonly uploads?: (
    input: AttachmentUploadInput,
    index: number,
  ) => Promise<AttachmentUploadResult>;
  readonly kindMaxBytes?: Readonly<Record<string, number | undefined>>;
}

function fakeUploader(options: FakeUploaderOptions = {}): FakeUploader {
  const calls: AttachmentUploadInput[] = [];
  const contexts: AttachmentUploadContext[] = [];
  let inFlight = 0;
  let peak = 0;
  const answer =
    options.uploads ??
    ((_input: AttachmentUploadInput, index: number): Promise<AttachmentUploadResult> =>
      Promise.resolve({
        artifactId: index === 0 ? ARTIFACT_A : ARTIFACT_B,
        bytes: 8,
      }));
  const uploader: AttachmentUploader = {
    kindMaxBytes: options.kindMaxBytes ?? { trace: 1024, video: 1024, screenshot: 1024 },
    async upload(input, context) {
      calls.push(input);
      contexts.push(context ?? {});
      inFlight += 1;
      peak = Math.max(peak, inFlight);
      try {
        return await answer(input, calls.length - 1);
      } finally {
        inFlight -= 1;
      }
    },
  };
  return {
    uploader,
    calls,
    contexts,
    get peak() {
      return peak;
    },
  };
}

/**
 * A release gate for uploads that must overlap. Deterministic: nothing waits on the wall
 * clock, a case releases the gate when the condition it asserts has been observed. (The
 * repo targets ES2022, so the executor form is the only way to keep the resolver.)
 */
function gate(): { readonly opened: Promise<void>; open: () => void } {
  let release: () => void = () => undefined;
  const opened = new Promise<void>((resolve) => {
    release = resolve;
  });
  return { opened, open: () => release() };
}

/** The `@evidgate/cli` surface as the pipeline sees it: only the uploader factory is used. */
function fakeCli(factory: () => AttachmentUploader): CliModule {
  return {
    createAttachmentUploader: () => factory(),
  } as unknown as CliModule;
}

interface Harness {
  readonly pipeline: AttachmentPipeline;
  readonly uploader: FakeUploader;
  readonly root: string;
}

/** A pipeline whose allow-list is one temp directory and whose CLI is the fake above. */
function harness(
  options: FakeUploaderOptions = {},
  overrides: { deadlineMs?: number } = {},
): Harness {
  const root = tempDir('evidgate-attach-');
  const uploader = fakeUploader(options);
  const pipeline = new AttachmentPipeline({
    roots: [root],
    cwd: root,
    env: {},
    importCli: () => Promise.resolve(fakeCli(() => uploader.uploader)),
    ...(overrides.deadlineMs === undefined ? {} : { deadlineMs: overrides.deadlineMs }),
  });
  return { pipeline, uploader, root };
}

function input(
  attachments: readonly PlaywrightAttachment[],
  overrides: { sensitiveAnnotated?: boolean; attemptNo?: number } = {},
): Parameters<AttachmentPipeline['process']>[0] {
  return {
    attachments,
    sensitiveAnnotated: overrides.sensitiveAnnotated ?? false,
    runId: RUN_ID,
    producerId: PRODUCER_ID,
    executionKey: EXECUTION_KEY,
    attemptNo: overrides.attemptNo ?? 1,
  };
}

function codesOf(warnings: readonly AttachmentWarning[]): string[] {
  return warnings.map((warning) => warning.code);
}

function recordOf(result: AttachmentProcessResult, index = 0): AttachmentRecord {
  const record = result.records[index];
  if (record === undefined) throw new Error(`no attachment record at index ${String(index)}`);
  return record;
}

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
      // AC7/T57: a trace is sensitive by default, with no annotation anywhere.
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
      // The upload reads the guard's REAL path, never the nominal one (AC6).
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
    // The server's message can embed a presigned URL; it is never forwarded (S3).
    expect(failure?.message).not.toContain('X-Amz-Signature');
    expect(JSON.stringify(result)).not.toContain(fakePresigned);
  });

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
    // Name and kind only: no directory, no URL (S3/DECISIONS §7).
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
      // The module loads; the CLI's own config resolution is what fails (Q3).
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

  it('keeps_at_most_two_uploads_in_flight (AC5/§7)', async () => {
    // The gate opens when the SECOND upload enters, so both are provably in flight at once and
    // a third can never be (the drain's pump is synchronous). `peak` is read live: with the
    // snapshot it used to return, an immediately-resolving fake made the old assertion pass at
    // zero (D-QA-1). If the ceiling were 1 this case hangs and fails on its timeout instead of
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

  it('hands_the_drain_signal_and_budget_to_the_upload (NFR-04-1)', async () => {
    const { pipeline, uploader, root } = harness(
      // Never resolves: only the drain deadline ends this job, and the fake already recorded
      // the context it was handed, so the abort is observable afterwards (F2/SEC-1/P-1).
      { uploads: () => new Promise<AttachmentUploadResult>(() => undefined) },
      { deadlineMs: 50 },
    );
    const trace = file(root, 'trace-signal.zip');

    const processing = pipeline.process(
      input([attachment(trace.path, 'trace-signal.zip', 'application/zip')]),
    );
    const drained = pipeline.finish();
    const [result, finished] = await Promise.all([processing, drained]);

    const context = uploader.contexts[0];
    if (context?.signal === undefined) {
      throw new Error('the upload was handed no drain signal');
    }
    // The very signal the drain aborts is what the uploader got, and it is aborted now.
    expect(context.signal.aborted).toBe(true);
    // The facade's budget is the drain deadline (or what is left of it), never "unbounded".
    expect(context.deadlineMs).toBeGreaterThan(0);
    expect(context.deadlineMs).toBeLessThanOrEqual(50);
    expect(finished).toMatchObject({ timedOut: true, abandoned: 1, deadlineAttempts: 1 });
    expect(recordOf(result)).toMatchObject({
      name: 'trace-signal.zip',
      outcome: 'failed',
      code: 'attachment_drain_deadline',
    });
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

  it('cuts_a_hung_upload_off_at_the_drain_deadline (NFR-04-1)', async () => {
    const root = tempDir('evidgate-attach-hung-');
    const trace = file(root, 'trace-hung.zip');
    const spool = tempDir('evidgate-attach-hung-spool-');
    const reporter = makeReporter(
      spool,
      root,
      () =>
        fakeUploader({
          // Never resolves: only the drain deadline can end this attempt. The executor form
          // stays because `Promise.withResolvers` is ES2024 and the workspace targets ES2022.
          uploads: () => new Promise<AttachmentUploadResult>(() => undefined),
        }).uploader,
      { attachment_drain_deadline_ms: 50 },
    );
    const test = fakeTest();
    const result = fakeResult([attachment(trace.path, 'trace-hung.zip', 'application/zip')]);

    reporter.onBegin(configFor(root));
    reporter.onTestBegin(test, result);
    reporter.onTestEnd(test, result);
    const started = Date.now();
    await endOfRun(reporter);
    const elapsed = Date.now() - started;

    // The deadline is 50 ms; a hung upload must not hold the run open past a small bound.
    expect(elapsed).toBeLessThan(5_000);
    const envelopes = readEnvelopesAt(spool);
    const finished = envelopes.find((item) => item.type === 'test.finished');
    expect(finished?.status).toBe('passed');
    expect(finished?.artifact_ids).toBeUndefined();
    expect(recordsOf(finished)[0]).toMatchObject({
      name: 'trace-hung.zip',
      outcome: 'failed',
      code: 'attachment_drain_deadline',
    });
    const deadlineWarnings = envelopes.filter(
      (item) => warnOf(item).code === 'attachment_drain_deadline',
    );
    expect(deadlineWarnings).toHaveLength(1);
    const deadlineWarning = deadlineWarnings[0];
    expect(deadlineWarning === undefined ? undefined : warnOf(deadlineWarning).count).toBe(1);
  });

  it('carries_the_run_level_uploader_warning_when_its_job_is_abandoned (F5)', async () => {
    // The uploader state resolves, then the deadline fires BEFORE that job's step can settle:
    // the job ends as a deadline and its step is discarded. The once-per-run warning must
    // survive that — it belongs to the attempt, not to the job that observed the state.
    const root = tempDir('evidgate-attach-f5-');
    const trace = file(root, 'trace-f5.zip');
    const missingConfig = Object.assign(new Error('missing configuration: EVIDGATE_PROJECT'), {
      name: 'CliError',
      code: 'missing_config',
    });
    let releaseLoad: () => void = () => undefined;
    const load = new Promise<void>((resolve) => {
      releaseLoad = resolve;
    });
    // Resolved when the JOB reached the uploader load: the only deterministic point from which
    // releasing the gate means "the job is parked here". A fixed number of event-loop turns
    // would be a guess — the guard's realpath/stat may not have finished yet (review note).
    let importCalled: () => void = () => undefined;
    const called = new Promise<void>((resolve) => {
      importCalled = resolve;
    });
    const cli = fakeCli(() => {
      throw missingConfig;
    });
    let fireDeadline: () => void = () => undefined;
    const pipeline = new AttachmentPipeline({
      roots: [root],
      cwd: root,
      env: {},
      deadlineMs: 50,
      // A virtual clock: the deadline fires where this case says so, never 50 ms later.
      timers: {
        set: (_milliseconds, fn) => {
          fireDeadline = fn;
          return 'deadline';
        },
        clear: () => undefined,
      },
      importCli: () => {
        importCalled();
        return load.then(() => cli);
      },
    });
    // Registered on `load` BEFORE the pipeline awaits its own `load.then(...)`, so this
    // callback runs first and queues the deadline as the next microtask: the drain abandons the
    // job once the uploader state is computed and before the job's step can settle, which is
    // the window in which a consumed run-level warning used to be lost.
    void load.then(() => {
      queueMicrotask(() => {
        fireDeadline();
      });
    });

    const processing = pipeline.process(
      input([attachment(trace.path, 'trace-f5.zip', 'application/zip')]),
    );
    const drained = pipeline.finish();
    await called;
    releaseLoad();
    const [result, finished] = await Promise.all([processing, drained]);

    expect(recordOf(result)).toMatchObject({
      name: 'trace-f5.zip',
      outcome: 'failed',
      code: 'attachment_drain_deadline',
    });
    // Exactly once, and never lost with the discarded step.
    expect(codesOf(result.warnings)).toEqual(['attachments_unconfigured']);
    expect(finished).toMatchObject({ timedOut: true, deadlineAttempts: 1 });
  });

  it('skips_every_attachment_of_an_attempt_without_a_planned_execution_key', () => {
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
    // No upload is pending, so `onEnd` is the synchronous shape and returns no promise.
    const returned = reporter.onEnd(fullResult());
    expect(isThenable(returned)).toBe(false);

    const envelopes = readEnvelopesAt(spool);
    expect(envelopes.find((item) => item.type === 'test.finished')?.artifact_ids).toBeUndefined();
    expect(warningCodes(envelopes)).toContain('attachment_no_execution_key');
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

/** The reporter with the attachment seams; `identity` supplies a real planned key (T17). */
function makeReporter(
  spool: string,
  root: string,
  factory: () => AttachmentUploader,
  overrides: Partial<EvidgateReporterOptions> = {},
): EvidgateReporter {
  return new EvidgateReporter({
    spool_dir: spool,
    run_id: RUN_ID,
    producer_id: PRODUCER_ID,
    engineVersion: '1.63.0',
    attachment_dirs: [root],
    importCli: () => Promise.resolve(fakeCli(factory)),
    identity: () => ({
      execution_key: EXECUTION_KEY,
      case_id: CASE_ID,
      variant_hash: VARIANT_HASH,
      target_env: 'test-env',
    }),
    ...overrides,
  });
}

function configFor(root: string): FullConfig {
  return {
    rootDir: root,
    projects: [{ outputDir: join(root, 'test-results') }],
  } as unknown as FullConfig;
}

function fakeTest(): Parameters<EvidgateReporter['onTestEnd']>[0] {
  return {
    id: 'attach-test-1',
    title: 'attaches evidence',
    location: { file: 'tests/attach.spec.ts', line: 4, column: 1 },
    annotations: [],
    expectedStatus: 'passed',
    parent: { project: () => ({ name: 'chromium' }) },
  } as unknown as Parameters<EvidgateReporter['onTestEnd']>[0];
}

function fakeResult(
  attachments: readonly PlaywrightAttachment[],
): Parameters<EvidgateReporter['onTestEnd']>[1] {
  return {
    status: 'passed',
    duration: 9,
    retry: 0,
    steps: [],
    annotations: [],
    errors: [],
    attachments: [...attachments],
  } as unknown as Parameters<EvidgateReporter['onTestEnd']>[1];
}

function fullResult(): Parameters<EvidgateReporter['onEnd']>[0] {
  return { status: 'passed', startTime: new Date(), duration: 1 };
}

/**
 * Awaits the async `onEnd` arm. The declaration stays `void` so the pre-existing bare
 * `reporter.onEnd(...)` call sites remain legal; the Promise the callback actually returns is
 * the value awaited here.
 */
function endOfRun(reporter: EvidgateReporter): Promise<unknown> {
  return reporter.onEnd(fullResult()) as unknown as Promise<unknown>;
}

function spoolDirOf(spool: string): string {
  return join(spool, RUN_ID, PRODUCER_ID);
}

function readSpoolLinesAt(spool: string): string[] {
  const dir = spoolDirOf(spool);
  const lines: string[] = [];
  for (const entry of readdirSync(dir)
    .filter((name) => name.endsWith('.jsonl'))
    .sort()) {
    for (const line of readFileSync(join(dir, entry), 'utf8').split('\n')) {
      if (line.length > 0) lines.push(line);
    }
  }
  return lines;
}

function readEnvelopesAt(spool: string): EventEnvelope[] {
  return readSpoolLinesAt(spool).map((line) => JSON.parse(line) as EventEnvelope);
}

/** The `ext.warn` payload of an envelope, narrowed without trusting its shape. */
function warnOf(envelope: EventEnvelope): { code?: string; message?: string; count?: number } {
  const warn = envelope.ext.warn;
  return typeof warn === 'object' && warn !== null ? warn : {};
}

/** The `ext.attachments` records of an envelope (empty for every other event). */
function recordsOf(envelope: EventEnvelope | undefined): AttachmentRecord[] {
  const value: unknown = envelope?.ext.attachments;
  // The pipeline built these records and the `ext` member is open, so the shape is ours.
  return Array.isArray(value) ? (value as AttachmentRecord[]) : [];
}

function warningCodes(envelopes: readonly EventEnvelope[]): string[] {
  return envelopes
    .map((envelope) => warnOf(envelope).code)
    .filter((code): code is string => code !== undefined);
}

function isThenable(value: unknown): boolean {
  if (typeof value !== 'object' || value === null || !('then' in value)) return false;
  return typeof value.then === 'function';
}
