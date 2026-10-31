// 04.3.3-T2.2: the attachment wiring seen through the REAL `EvidgateReporter` and its spool
// (AC5; plan §9 `attach.test.ts`/`drain.test.ts` behaviour). Nothing here fakes the reporter:
// the envelopes are read back from the JSONL the writer produced, so the case set pins what a
// downstream reader actually sees.
//
// What the spec pins:
// - AC5: `artifact_ids` carries ONLY the completed uploads, and `ext.attachments` carries one
//   record per attachment, in attachment order, with base names only;
// - sequences stay contiguous from 1 even though the batches are appended after the uploads
//   settled (held batches keep test-end order among themselves; an attempt WITHOUT
//   attachments is appended immediately and may precede a held one);
// - a raw Playwright `TestStep.attachments` never reaches a `test.step` envelope (SEC/F1: it
//   carries an absolute path and the body bytes, and only `test.finished` has our records);
// - a test WITHOUT attachments takes the 04.3.1 path unchanged: same members, same `ext` keys,
//   no CLI load at all, and two runs agree byte for byte apart from the volatile fields;
// - `onEnd` without pending uploads is still synchronous (a passing run returns no promise)
//   and reports a buffered spool failure as `{ status: 'failed' }` plus one
//   `[evidgate] reporter failure` stderr line;
// - the writer's once-per-run notices (`shards_floor_ahead`) reach the spool as warnings.
import {
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  realpathSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import type { FullConfig } from '@playwright/test/reporter';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { AttachmentUploader, AttachmentUploadInput } from '@evidgate/cli';

import type { AttachmentRecord, CliModule } from './attach.js';
import type { PlaywrightAttachment } from './attachMap.js';
import type { EventEnvelope } from './envelope.js';
import { EvidgateReporter } from './reporter.js';

const RUN_ID = 'run-attach-reporter-1';
const PRODUCER_ID = 'shard-1';
const EXECUTION_KEY = 'b2'.repeat(32);
const CASE_ID = '1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d';
const VARIANT_HASH = `sha256:${'ef'.repeat(32)}`;
const ARTIFACT_TRACE = '33333333-3333-4333-8333-333333333333';

const tempDirs: string[] = [];

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function tempDir(prefix: string): string {
  const dir = mkdtempSync(join(tmpdir(), prefix));
  tempDirs.push(dir);
  return dir;
}

function file(dir: string, name: string): string {
  const path = join(dir, name);
  writeFileSync(path, Buffer.alloc(8, 0x41));
  return path;
}

function attachment(path: string, name: string, contentType: string): PlaywrightAttachment {
  return { name, path, body: undefined, contentType };
}

/** An uploader that answers every trace upload with the same id, and can refuse others. */
function uploader(options: { failScreenshots?: boolean } = {}): AttachmentUploader {
  return {
    // The uploader's static caps: every kind the union names, `undefined` where the CLI has
    // no cap for that kind (the reporter treats `undefined` as max size, never a skip).
    kindMaxBytes: {
      trace: 1024,
      video: 1024,
      screenshot: 1024,
      raw_report: undefined,
      dom_snapshot: undefined,
      a11y_snapshot: undefined,
      patch: undefined,
      ui_hierarchy: undefined,
    },
    upload(input: AttachmentUploadInput) {
      if (options.failScreenshots === true && input.kind === 'screenshot') {
        return Promise.reject(
          Object.assign(new Error('the object store refused the upload (HTTP 500)'), {
            code: 'upload_put_failed',
            status: 500,
          }),
        );
      }
      return Promise.resolve({ artifactId: ARTIFACT_TRACE, bytes: 8 });
    },
  };
}

function cliFor(factory: () => AttachmentUploader): CliModule {
  return { createAttachmentUploader: () => factory() } as unknown as CliModule;
}

interface ReporterHarness {
  readonly reporter: EvidgateReporter;
  readonly spool: string;
}

function reporterHarness(
  spool: string,
  root: string,
  factory: () => AttachmentUploader,
): ReporterHarness {
  const reporter = new EvidgateReporter({
    spool_dir: spool,
    run_id: RUN_ID,
    producer_id: PRODUCER_ID,
    engineVersion: '1.63.0',
    attachment_dirs: [root],
    importCli: () => Promise.resolve(cliFor(factory)),
    identity: () => ({
      execution_key: EXECUTION_KEY,
      case_id: CASE_ID,
      variant_hash: VARIANT_HASH,
      target_env: 'test-env',
    }),
  });
  return { reporter, spool };
}

function configFor(root: string): FullConfig {
  return {
    rootDir: root,
    projects: [{ outputDir: join(root, 'test-results') }],
  } as unknown as FullConfig;
}

type TestArg = Parameters<EvidgateReporter['onTestEnd']>[0];
type ResultArg = Parameters<EvidgateReporter['onTestEnd']>[1];

function fakeTest(title: string, line: number): TestArg {
  return {
    id: `attach-${title}`,
    title,
    location: { file: 'tests/attach.spec.ts', line, column: 1 },
    annotations: [],
    expectedStatus: 'passed',
    parent: { project: () => ({ name: 'chromium' }) },
  } as unknown as TestArg;
}

function fakeResult(
  attachments: readonly PlaywrightAttachment[],
  steps: readonly unknown[] = [],
): ResultArg {
  return {
    status: 'passed',
    duration: 11,
    retry: 0,
    steps: [...steps],
    annotations: [],
    errors: [],
    attachments: [...attachments],
  } as unknown as ResultArg;
}

function fullResult(): Parameters<EvidgateReporter['onEnd']>[0] {
  return { status: 'passed', startTime: new Date(), duration: 1 };
}

/**
 * Awaits the async `onEnd` arm. `onEnd` returns `undefined` for a passing run and a promise
 * once it reports a failure, so the helper awaits whatever the callback returned.
 */
async function endOfRun(reporter: EvidgateReporter): Promise<unknown> {
  return await reporter.onEnd(fullResult());
}

function spoolLines(spool: string): string[] {
  const dir = join(spool, RUN_ID, PRODUCER_ID);
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

function envelopesOf(spool: string): EventEnvelope[] {
  return spoolLines(spool).map((line) => JSON.parse(line) as EventEnvelope);
}

function warnOf(envelope: EventEnvelope): { code?: string; message?: string; count?: number } {
  const warn = envelope.ext.warn;
  return typeof warn === 'object' && warn !== null ? warn : {};
}

function recordsOf(envelope: EventEnvelope | undefined): AttachmentRecord[] {
  const value: unknown = envelope?.ext.attachments;
  // The pipeline built these records and the `ext` member is open, so the shape is ours.
  return Array.isArray(value) ? (value as AttachmentRecord[]) : [];
}

/** True when the value is a promise-like (what the drained `onEnd` arm returns). */
function isThenable(value: unknown): boolean {
  if (typeof value !== 'object' || value === null || !('then' in value)) return false;
  return typeof value.then === 'function';
}

describe('attachments through the reporter (04.3.3-T2.2)', () => {
  it('carries_artifact_ids_for_the_completed_uploads_only (AC5)', async () => {
    const root = tempDir('evidgate-rep-root-');
    const spool = tempDir('evidgate-rep-spool-');
    const trace = file(root, 'trace-rep.zip');
    const shot = file(root, 'screenshot-rep.png');
    const { reporter } = reporterHarness(spool, root, () => uploader({ failScreenshots: true }));
    const test = fakeTest('uploads a trace and a screenshot', 7);
    const result = fakeResult([
      attachment(trace, 'trace-rep.zip', 'application/zip'),
      attachment(shot, 'screenshot-rep.png', 'image/png'),
    ]);

    reporter.onBegin(configFor(root));
    reporter.onTestBegin(test, result);
    reporter.onTestEnd(test, result);
    await endOfRun(reporter);

    const envelopes = envelopesOf(spool);
    const finished = envelopes.filter((item) => item.type === 'test.finished');
    expect(finished).toHaveLength(1);
    // One id, for the upload that completed; the failed one is never referenced.
    expect(finished[0]?.artifact_ids).toEqual([ARTIFACT_TRACE]);
    const records = recordsOf(finished[0]);
    expect(records.map((record) => [record.name, record.outcome])).toEqual([
      ['trace-rep.zip', 'uploaded'],
      ['screenshot-rep.png', 'failed'],
    ]);
    expect(records[1]?.code).toBe('upload_put_failed');
    // Base names only: no directory ever reaches the evidence (S3/DECISIONS §7).
    for (const line of spoolLines(spool)) {
      expect(line).not.toContain(root);
      expect(line).not.toContain(realpathSync(root));
    }
  });

  it('keeps_the_sequence_contiguous_across_held_batches', async () => {
    const root = tempDir('evidgate-rep-seq-');
    const spool = tempDir('evidgate-rep-seq-spool-');
    const first = file(root, 'trace-first.zip');
    const second = file(root, 'trace-second.zip');
    const { reporter } = reporterHarness(spool, root, () => uploader());

    reporter.onBegin(configFor(root));
    const testA = fakeTest('first with an attachment', 3);
    const resultA = fakeResult([attachment(first, 'trace-first.zip', 'application/zip')]);
    reporter.onTestBegin(testA, resultA);
    reporter.onTestEnd(testA, resultA);
    const testB = fakeTest('second with an attachment', 9);
    const resultB = fakeResult([attachment(second, 'trace-second.zip', 'application/zip')]);
    reporter.onTestBegin(testB, resultB);
    reporter.onTestEnd(testB, resultB);
    await endOfRun(reporter);

    const envelopes = envelopesOf(spool);
    expect(envelopes.map((item) => item.sequence)).toEqual(envelopes.map((_, index) => index + 1));
    const finished = envelopes.filter((item) => item.type === 'test.finished');
    expect(finished).toHaveLength(2);
    // The batches land in the order the tests ended, both before the `run_end` marker.
    const order = envelopes.map((item) => item.type);
    expect(order.lastIndexOf('test.finished')).toBeLessThan(order.lastIndexOf('run.log'));
    expect(finished[0]?.ext.title).toBe('first with an attachment');
    expect(finished[1]?.ext.title).toBe('second with an attachment');
    for (const envelope of finished) {
      expect(envelope.artifact_ids).toEqual([ARTIFACT_TRACE]);
      expect(recordsOf(envelope)).toHaveLength(1);
    }
  });

  it('keeps_a_raw_step_attachment_out_of_the_spool (SEC/F1)', async () => {
    const root = tempDir('evidgate-rep-step-');
    const spool = tempDir('evidgate-rep-step-spool-');
    const trace = file(root, 'trace-own.zip');
    const { reporter } = reporterHarness(spool, root, () => uploader());
    // A step carries Playwright's raw shape: an absolute path plus the body bytes in memory.
    // Neither member is one the reporter declares, and only `test.finished` may carry records.
    const stepPath = join(realpathSync(root), 'test-results', 'step-note.txt');
    const body = Buffer.from('STEP-BODY-MARKER-9f3a-04.3.3-T2');
    const test = fakeTest('a step with its own attachment', 6);
    // Playwright's `TestStep` shape, as a plain record: the reporter's own members come from
    // the real object, so a plain record with the same members is the seam under test.
    const stepRecord = {
      title: 'the user step',
      category: 'test.step',
      location: { file: 'tests/attach.spec.ts', line: 6, column: 3 },
      duration: 3,
      startTime: new Date(),
      steps: [],
      attachments: [{ name: 'step-note.txt', contentType: 'text/plain', path: stepPath, body }],
    };
    const own = attachment(trace, 'trace-own.zip', 'application/zip');
    const result = fakeResult([own], [stepRecord]);

    reporter.onBegin(configFor(root));
    reporter.onTestBegin(test, result);
    reporter.onTestEnd(test, result);
    await endOfRun(reporter);

    const envelopes = envelopesOf(spool);
    const steps = envelopes.filter((item) => item.type === 'test.step');
    expect(steps.length).toBeGreaterThan(0);
    for (const step of steps) {
      expect(step.ext).not.toHaveProperty('attachments');
      expect(step.ext.title).toBe('the user step');
    }

    // The attempt's own records are untouched: the base name of `result.attachments` only.
    const finished = envelopes.filter((item) => item.type === 'test.finished');
    expect(finished).toHaveLength(1);
    expect(recordsOf(finished[0]).map((record) => record.name)).toEqual(['trace-own.zip']);

    // Neither the step path, its base name, nor the body (raw or as a serialised Buffer) leaks.
    for (const line of spoolLines(spool)) {
      expect(line).not.toContain(stepPath);
      expect(line).not.toContain('step-note.txt');
      expect(line).not.toContain('STEP-BODY-MARKER');
      expect(line).not.toContain('"type":"Buffer"');
    }
  });

  it('leaves_a_test_without_attachments_byte_identical_and_never_loads_the_cli', () => {
    const root = tempDir('evidgate-rep-plain-');
    const runOnce = (): { spool: string; imported: () => boolean } => {
      const spool = tempDir('evidgate-rep-plain-spool-');
      let imported = false;
      const reporter = new EvidgateReporter({
        spool_dir: spool,
        run_id: RUN_ID,
        producer_id: PRODUCER_ID,
        engineVersion: '1.63.0',
        attachment_dirs: [root],
        importCli: () => {
          imported = true;
          return Promise.reject(
            new Error('the CLI must not be loaded by a run without attachments'),
          );
        },
        identity: () => ({
          execution_key: EXECUTION_KEY,
          case_id: CASE_ID,
          variant_hash: VARIANT_HASH,
          target_env: 'test-env',
        }),
      });
      reporter.onBegin(configFor(root));
      const test = fakeTest('no attachments at all', 5);
      const result = fakeResult([]);
      reporter.onTestBegin(test, result);
      reporter.onTestEnd(test, result);
      const returned = reporter.onEnd(fullResult());
      // Nothing uploadable: the synchronous 04.3.1 shape, no promise.
      expect(isThenable(returned)).toBe(false);
      return { spool, imported: () => imported };
    };

    const first = runOnce();
    const second = runOnce();
    expect(first.imported()).toBe(false);
    expect(second.imported()).toBe(false);

    const envelopes = envelopesOf(first.spool);
    const finished = envelopes.filter((item) => item.type === 'test.finished');
    expect(finished).toHaveLength(1);
    const envelope = finished[0];
    // The pre-04.3.3-T2 member set, exactly: no `artifact_ids`, no `ext.attachments`.
    expect(Object.keys(envelope ?? {}).sort()).toEqual([
      'attempt_no',
      'duration_ms',
      'event_id',
      'execution_key',
      'ext',
      'occurred_at',
      'producer_id',
      'schema_version',
      'sequence',
      'status',
      'type',
    ]);
    expect(envelope?.ext).not.toHaveProperty('attachments');
    expect(envelope?.artifact_ids).toBeUndefined();

    // Two independent runs agree byte for byte apart from the volatile members.
    const strip = (items: readonly EventEnvelope[]): unknown[] =>
      items.map((item) => ({ ...item, event_id: 'x', occurred_at: 'x' }));
    expect(strip(envelopes)).toEqual(strip(envelopesOf(second.spool)));
  });

  it('fails_a_buffered_spool_failure_when_nothing_is_pending', async () => {
    const root = tempDir('evidgate-rep-fail-');
    const spool = tempDir('evidgate-rep-fail-spool-');
    const { reporter } = reporterHarness(spool, root, () => uploader());
    const previous = process.env.EVIDGATE_SPOOL_MAX_BYTES;
    const write = vi.spyOn(process.stderr, 'write').mockImplementation(() => true);
    // One byte for the whole run directory: the very first flush must refuse the batch.
    process.env.EVIDGATE_SPOOL_MAX_BYTES = '1';
    try {
      reporter.onBegin(configFor(root));
      const test = fakeTest('cannot write', 2);
      const result = fakeResult([]);
      reporter.onTestEnd(test, result);
      // The failure is buffered, and `onEnd` reports it through the awaited return value.
      await expect(reporter.onEnd(fullResult())).resolves.toEqual({ status: 'failed' });
      expect(String(write.mock.calls[0]?.[0])).toContain('[evidgate] reporter failure');
    } finally {
      write.mockRestore();
      if (previous === undefined) delete process.env.EVIDGATE_SPOOL_MAX_BYTES;
      else process.env.EVIDGATE_SPOOL_MAX_BYTES = previous;
    }
  });

  it('turns_the_writers_shard_notices_into_once_per_run_warnings', () => {
    const root = tempDir('evidgate-rep-notice-');
    const spool = tempDir('evidgate-rep-notice-spool-');
    // A `shards.json` whose floor is ahead of the (empty) chunks: a record no writer of ours
    // mints, exactly the legacy/hand-edited state 04.3.3-T2 must report once.
    const producerDir = join(spool, RUN_ID, PRODUCER_ID);
    mkdirSync(producerDir, { recursive: true });
    writeFileSync(
      join(producerDir, 'shards.json'),
      JSON.stringify({ producer_id: PRODUCER_ID, shard_index: 1, sequence_next: 99 }),
    );
    const { reporter } = reporterHarness(spool, root, () => uploader());

    reporter.onBegin(configFor(root));
    const test = fakeTest('after a floor-ahead record', 4);
    const result = fakeResult([]);
    reporter.onTestEnd(test, result);
    void reporter.onEnd(fullResult());

    const notices = envelopesOf(spool).filter((item) => warnOf(item).code === 'shards_floor_ahead');
    expect(notices).toHaveLength(1);
    const notice = notices[0];
    expect(notice === undefined ? '' : warnOf(notice).message).toContain('ahead of the highest');
    // Path-free: the writer's own message names the numbers, never the spool directory.
    expect(notice === undefined ? '' : warnOf(notice).message).not.toContain(spool);
  });
});
