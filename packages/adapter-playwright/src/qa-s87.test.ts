// QA probe (S87, 04.3.3-T2): negative, boundary and exploratory cases written by the QA agent
// that did NOT write the implementation. Throwaway by intent: the orchestrator reviews and
// commits it (or drops it) after the reviewed sha.
//
// Every case below is either a boundary the task file names but no delivered spec pins
// (the 1001-attachment per-run bound, the empty `EVIDGATE_ATTACHMENT_DIRS` segment, a FIFO),
// or an exploratory probe of a documented invariant (secret hygiene of an upload error,
// attachment order of `artifact_ids`, `onEnd` twice, merge-replay with attachments).
import { execFileSync } from 'node:child_process';
import {
  existsSync,
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
  AttachmentUploadInput,
  AttachmentUploadResult,
  AttachmentUploader,
} from '@evidgate/cli';

import {
  AttachmentPipeline,
  MAX_ATTACHMENTS_PER_RUN,
  MAX_ATTACHMENTS_PER_TEST,
  type AttachmentProcessResult,
  type CliModule,
} from './attach.js';
import type { PlaywrightAttachment } from './attachMap.js';
import { isOverCap, mapAttachment } from './attachMap.js';
import { createPathGuard, defaultAttachmentRoots } from './attachPaths.js';
import type { EventEnvelope } from './envelope.js';
import { EvidgateReporter, type EvidgateReporterOptions } from './reporter.js';

const RUN_ID = 'run-qa-s87';
const PRODUCER_ID = 'shard-1';
const EXECUTION_KEY = 'c3'.repeat(32);
const CASE_ID = '9f8e7d6c-5b4a-4392-8170-6f5e4d3c2b1a';
const VARIANT_HASH = `sha256:${'ab'.repeat(32)}`;
const ARTIFACT_A = '11111111-1111-4111-8111-111111111111';
const ARTIFACT_B = '22222222-2222-4222-8222-222222222222';
/** Assembled at runtime so no token- or URL-shaped literal sits in the spec (T78). */
const fakeToken = ['qa', 's87', 'token'].join('-');
const fakePresigned = `https://store.test/put?X-Amz-Signature=${'deadbeef'}`;

const tempDirs: string[] = [];

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function tempDir(prefix: string): string {
  const dir = mkdtempSync(join(tmpdir(), prefix));
  tempDirs.push(dir);
  return dir;
}

function file(dir: string, name: string, size = 8): { path: string; size: number } {
  const path = join(dir, name);
  writeFileSync(path, Buffer.alloc(size, 0x50));
  return { path, size };
}

function attachment(path: string, name: string, contentType: string): PlaywrightAttachment {
  return { name, path, body: undefined, contentType };
}

/**
 * A release gate for the uploads that must settle out of order. Deterministic: nothing waits
 * on the wall clock, a test releases the gate when the condition it asserts has been observed.
 * (The repo targets ES2022, so the executor form is the only way to keep the resolver.)
 */
function gate(): { readonly opened: Promise<void>; open: () => void } {
  let release: () => void = () => undefined;
  const opened = new Promise<void>((resolve) => {
    release = resolve;
  });
  return { opened, open: () => release() };
}

interface FakeUploader {
  uploader: AttachmentUploader;
  calls: AttachmentUploadInput[];
  /** A live read: the count keeps rising while the uploads run. */
  readonly peak: number;
}

interface FakeUploaderOptions {
  readonly uploads?: (
    input: AttachmentUploadInput,
    index: number,
  ) => Promise<AttachmentUploadResult>;
  readonly caps?: Partial<AttachmentUploader['kindMaxBytes']>;
}

const ALL_CAPS: AttachmentUploader['kindMaxBytes'] = {
  trace: 1024,
  video: 1024,
  screenshot: 1024,
  raw_report: undefined,
  dom_snapshot: undefined,
  a11y_snapshot: undefined,
  patch: undefined,
  ui_hierarchy: undefined,
};

function fakeUploader(options: FakeUploaderOptions = {}): FakeUploader {
  const calls: AttachmentUploadInput[] = [];
  let inFlight = 0;
  let peak = 0;
  const answer =
    options.uploads ??
    ((_input: AttachmentUploadInput, index: number): Promise<AttachmentUploadResult> =>
      Promise.resolve({ artifactId: index === 0 ? ARTIFACT_A : ARTIFACT_B, bytes: 8 }));
  const uploader: AttachmentUploader = {
    kindMaxBytes: { ...ALL_CAPS, ...options.caps },
    async upload(input) {
      calls.push(input);
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
    get peak() {
      return peak;
    },
  };
}

function fakeCli(factory: () => AttachmentUploader): CliModule {
  return { createAttachmentUploader: () => factory() } as unknown as CliModule;
}

interface Harness {
  readonly pipeline: AttachmentPipeline;
  readonly uploader: FakeUploader;
  readonly root: string;
}

function harness(options: FakeUploaderOptions = {}, deadlineMs?: number): Harness {
  const root = tempDir('qa-s87-attach-');
  const uploader = fakeUploader(options);
  const pipeline = new AttachmentPipeline({
    roots: [root],
    cwd: root,
    env: {},
    importCli: () => Promise.resolve(fakeCli(() => uploader.uploader)),
    ...(deadlineMs === undefined ? {} : { deadlineMs }),
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

function recordAt(
  result: AttachmentProcessResult,
  index = 0,
): AttachmentProcessResult['records'][number] {
  const record = result.records[index];
  if (record === undefined) throw new Error(`no record at ${String(index)}`);
  return record;
}

function png(dir: string, index: number): PlaywrightAttachment {
  const name = `shot-${String(index)}.png`;
  return attachment(file(dir, name).path, name, 'image/png');
}

interface ReporterHarness {
  readonly reporter: EvidgateReporter;
  readonly spool: string;
}

function reporterHarness(
  spool: string,
  root: string,
  factory: () => AttachmentUploader,
  overrides: Partial<EvidgateReporterOptions> = {},
): ReporterHarness {
  const reporter = new EvidgateReporter({
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
    id: `qa-${title}`,
    title,
    location: { file: 'tests/qa.spec.ts', line, column: 1 },
    annotations: [],
    expectedStatus: 'passed',
    parent: { project: () => ({ name: 'chromium' }) },
  } as unknown as TestArg;
}

function fakeResult(attachments: readonly PlaywrightAttachment[]): ResultArg {
  return {
    status: 'passed',
    duration: 11,
    retry: 0,
    steps: [],
    annotations: [],
    errors: [],
    attachments: [...attachments],
  } as unknown as ResultArg;
}

function fullResult(): Parameters<EvidgateReporter['onEnd']>[0] {
  return { status: 'passed', startTime: new Date(), duration: 1 };
}

function endOfRun(reporter: EvidgateReporter): Promise<unknown> {
  return reporter.onEnd(fullResult()) as unknown as Promise<unknown>;
}

function spoolLines(spool: string): string[] {
  const dir = join(spool, RUN_ID, PRODUCER_ID);
  if (!existsSync(dir)) return [];
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

function recordsOf(envelope: EventEnvelope | undefined): unknown[] {
  const value: unknown = envelope?.ext.attachments;
  return Array.isArray(value) ? value : [];
}

/** The `ext.marker` of an envelope (the run-end marker is a `run.log` with `marker`). */
function markerOf(envelope: EventEnvelope): string | undefined {
  const marker: unknown = envelope.ext.marker;
  return typeof marker === 'string' ? marker : undefined;
}

/** The `ext.warn` members the probes assert on, narrowed without trusting the shape. */
function warnOf(envelope: EventEnvelope): { code?: string; count?: number; message?: string } {
  const warn: unknown = envelope.ext.warn;
  if (typeof warn !== 'object' || warn === null) return {};
  const code = 'code' in warn && typeof warn.code === 'string' ? warn.code : undefined;
  const count = 'count' in warn && typeof warn.count === 'number' ? warn.count : undefined;
  const message = 'message' in warn && typeof warn.message === 'string' ? warn.message : undefined;
  return {
    ...(code === undefined ? {} : { code }),
    ...(count === undefined ? {} : { count }),
    ...(message === undefined ? {} : { message }),
  };
}

/** The `code` of every `ext.attachments` record, narrowed the same way. */
function recordCodesOf(envelope: EventEnvelope | undefined): Array<string | undefined> {
  return recordsOf(envelope).map((record) =>
    typeof record === 'object' &&
    record !== null &&
    'code' in record &&
    typeof record.code === 'string'
      ? record.code
      : undefined,
  );
}

function codesIn(spool: string): string[] {
  return envelopesOf(spool).flatMap((item) => {
    const { code } = warnOf(item);
    return code === undefined ? [] : [code];
  });
}

describe('QA probe: AC4 mapping boundaries', () => {
  it('bounds a very long base name and never splits a surrogate', () => {
    const longName = `${'a'.repeat(5000)}.png`;
    const outcome = mapAttachment(
      { name: longName, path: `/root/${longName}`, body: undefined, contentType: 'image/png' },
      { sensitive: false },
    );
    expect(outcome.ok).toBe(true);
    if (!outcome.ok) return;
    expect(outcome.attachment.name.length).toBeLessThanOrEqual(120);
    const last = outcome.attachment.name.charCodeAt(outcome.attachment.name.length - 1);
    expect(last >= 0xd800 && last <= 0xdbff).toBe(false);
    expect(outcome.attachment.name).not.toContain('/root');

    // A cut that would land inside an astral character: 119 ASCII + one surrogate pair.
    const astral = `${'a'.repeat(119)}😀.png`;
    const cut = mapAttachment(
      { name: astral, path: `/root/${astral}`, body: undefined, contentType: 'image/png' },
      { sensitive: false },
    );
    expect(cut.ok).toBe(true);
    if (!cut.ok) return;
    const cutLast = cut.attachment.name.charCodeAt(cut.attachment.name.length - 1);
    expect(cutLast >= 0xd800 && cutLast <= 0xdbff).toBe(false);
  });

  it('escapes control and bidi characters in a record name and in a skip message', () => {
    const name = 'shot\u0007\n\u202eevil.png';
    const mapped = mapAttachment(
      { name, path: `/root/${name}`, body: undefined, contentType: 'image/png' },
      { sensitive: false },
    );
    expect(mapped.ok).toBe(true);
    if (!mapped.ok) return;
    const shown = mapped.attachment.name;
    expect(shown).toContain('\\u{7}');
    expect(shown).toContain('\\n');
    expect(shown).toContain('\\u{202e}');
    expect(/[\p{Cc}\p{Cf}]/u.test(shown)).toBe(false);

    const skipped = mapAttachment(
      {
        name,
        path: `/root/${name.replace('.png', '.bin')}`,
        body: undefined,
        contentType: 'application/octet-stream',
      },
      { sensitive: false },
    );
    expect(skipped.ok).toBe(false);
    if (skipped.ok) return;
    expect(skipped.code).toBe('attachment_unmapped_type');
    expect(/[\p{Cc}\p{Cf}]/u.test(skipped.message)).toBe(false);
    expect(skipped.message).not.toContain('/root');
  });

  it('skips an undefined, empty, wildcard or comma-list content type as a mismatch', () => {
    const values: unknown[] = [
      undefined,
      '',
      'application/octet-stream',
      'image/*',
      'image/png, image/jpeg',
    ];
    for (const contentType of values) {
      const outcome = mapAttachment(
        {
          name: 'shot.png',
          path: '/root/shot.png',
          body: undefined,
          contentType: contentType as string,
        },
        { sensitive: false },
      );
      expect(outcome.ok).toBe(false);
      if (!outcome.ok) expect(outcome.code).toBe('attachment_type_mismatch');
    }
  });
});

describe('QA probe: AC6 path guard boundaries', () => {
  it('refuses a FIFO, a directory and a symlink to a directory without hanging', async () => {
    const root = tempDir('qa-s87-fifo-');
    const fifo = join(root, 'trace-fifo.zip');
    execFileSync('mkfifo', [fifo]);
    mkdirSync(join(root, 'sub'));
    const dirLink = join(root, 'shot-dirlink.png');
    symlinkSync(join(root, 'sub'), dirLink);
    const guard = createPathGuard({ roots: [root], cwd: root });

    const started = Date.now();
    const fifoVerdict = await guard.check(fifo);
    const dirVerdict = await guard.check(dirLink);
    expect(Date.now() - started).toBeLessThan(2_000);

    expect(fifoVerdict.ok).toBe(false);
    if (!fifoVerdict.ok) {
      expect(fifoVerdict.code).toBe('attachment_path_refused');
      expect(fifoVerdict.message).toContain('not a regular file');
    }
    expect(dirVerdict.ok).toBe(false);
    if (!dirVerdict.ok) expect(dirVerdict.code).toBe('attachment_path_refused');
  });

  it('refuses a two-hop symlink chain that ends outside the root', async () => {
    const root = tempDir('qa-s87-chain-');
    const outside = tempDir('qa-s87-outside-');
    const secret = file(outside, 'trace-secret.zip');
    symlinkSync(secret.path, join(root, 'hop1.zip'));
    symlinkSync(join(root, 'hop1.zip'), join(root, 'trace-hop2.zip'));
    const guard = createPathGuard({ roots: [root], cwd: root });

    const verdict = await guard.check(join(root, 'trace-hop2.zip'));
    expect(verdict.ok).toBe(false);
    if (!verdict.ok) expect(verdict.code).toBe('attachment_path_refused');
  });

  it('accepts a dot-dot segment that resolves back inside the root', async () => {
    const root = tempDir('qa-s87-dotdot-');
    mkdirSync(join(root, 'sub'));
    const inside = file(join(root, 'sub'), 'shot.png');
    const guard = createPathGuard({ roots: [root], cwd: root });

    const verdict = await guard.check(`${root}/sub/../sub/shot.png`);
    expect(verdict.ok).toBe(true);
    if (verdict.ok) expect(verdict.realPath).toBe(realpathSync(inside.path));
  });

  it('drops empty and blank entries when the roots are built', () => {
    const root = tempDir('qa-s87-roots-');
    const roots = defaultAttachmentRoots({
      outputDirs: ['', '   '],
      rootDir: root,
      extraDirs: ['', '  '],
    });
    expect(roots).toEqual([join(root, 'test-results')]);
  });

  it('treats zero as a valid cap and refuses the first byte over it', () => {
    expect(isOverCap('screenshot', 0, { screenshot: 0 })).toBe(false);
    expect(isOverCap('screenshot', 1, { screenshot: 0 })).toBe(true);
    expect(isOverCap('screenshot', -1, { screenshot: 10 })).toBe(true);
    expect(isOverCap('screenshot', Number.NaN, { screenshot: 10 })).toBe(true);
    expect(isOverCap('screenshot', Number.POSITIVE_INFINITY, { screenshot: 10 })).toBe(true);
  });

  it('uploads a file exactly at its kind cap and skips cap+1 without an upload', async () => {
    const { pipeline, uploader, root } = harness({ caps: { screenshot: 4 } });
    const at = file(root, 'shot-at.png', 4);
    const over = file(root, 'shot-over.png', 5);

    const result = await pipeline.process(
      input([
        attachment(at.path, 'shot-at.png', 'image/png'),
        attachment(over.path, 'shot-over.png', 'image/png'),
      ]),
    );
    await pipeline.finish();

    expect(result.records.map((record) => record.outcome)).toEqual(['uploaded', 'skipped']);
    expect(recordAt(result, 1).code).toBe('attachment_over_cap');
    expect(uploader.calls).toHaveLength(1);
  });
});

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
    // Keyed on the file, never on the call index: two guards resolve concurrently. That is
    // also the observation behind O-4: `drain.ts:16-17` promises FIFO *start*, and `attach.ts`
    // awaits `guard.check()` before `uploader.upload()`, so the PUT calls themselves can be
    // issued in the opposite order (a call-index keyed fake saw the second attachment first).
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

describe('QA probe: reporter wiring beyond the delivered specs', () => {
  it('keeps a healthy attempt while another attempt hits the drain deadline', async () => {
    const root = tempDir('qa-s87-mixed-root-');
    const spool = tempDir('qa-s87-mixed-spool-');
    const hung = file(root, 'trace-hung.zip');
    const good = file(root, 'shot-good.png');
    const uploader = fakeUploader({
      uploads: (_input, index) =>
        index === 0
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

  it('skips placeholder-key attachments with one warning carrying the count', () => {
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
    // Nothing uploadable was pending, so the synchronous arm answered (no promise).
    expect(typeof returned === 'object' && returned !== null && 'then' in returned).toBe(false);
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
      uploads: async (_input, index) => {
        if (index === 0) await settleGate.opened;
        else settleGate.open();
        return { artifactId: index === 0 ? ARTIFACT_A : ARTIFACT_B, bytes: 8 };
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
    let secondError: unknown;
    try {
      await (reporter.onEnd(fullResult()) as unknown as Promise<unknown>);
    } catch (error) {
      secondError = error;
    }
    const envelopes = envelopesOf(spool);
    // Whatever the second call does, it must not append a second run-end marker or a batch.
    expect(envelopes.filter((item) => markerOf(item) === 'run_end')).toHaveLength(1);
    expect(envelopes.length).toBe(afterFirst);
    expect(envelopes.map((item) => item.sequence)).toEqual(envelopes.map((_, index) => index + 1));
    // Pinned observation: the closed writer makes the second call throw its buffered failure.
    expect(secondError instanceof Error).toBe(true);
  });
});
