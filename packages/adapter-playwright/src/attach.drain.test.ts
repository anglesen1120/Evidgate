// The drain side: the signal and budget an upload is handed, and what a deadline does to an
// upload that never settles.
import { describe, expect, it } from 'vitest';

import type { AttachmentUploadResult } from '@evidgate/cli';

import { AttachmentPipeline } from './attach.js';
import {
  attachment,
  codesOf,
  configFor,
  endOfRun,
  fakeCli,
  fakeResult,
  fakeTest,
  fakeUploader,
  file,
  harness,
  input,
  makeReporter,
  readEnvelopesAt,
  recordOf,
  recordsOf,
  tempDir,
  warnOf,
} from './attach.testing.js';

describe('attachment pipeline (04.3.3-T2.2)', () => {
  it('hands_the_drain_signal_and_budget_to_the_upload (NFR-04-1)', async () => {
    const { pipeline, uploader, root } = harness(
      // Never resolves: only the drain deadline ends this job, and the fake already recorded
      // the context it was handed, so the abort is observable afterwards.
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
    // would be a guess — the guard's realpath/stat may not have finished yet.
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
});
