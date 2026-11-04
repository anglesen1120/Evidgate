// Fixtures for the attachment QA probes. The probes were written against their own values (run
// id, execution key, caps), so this file keeps those and reuses the shared helper only for the
// generic temp-dir, file and reporter utilities.
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

import type {
  AttachmentUploadInput,
  AttachmentUploadResult,
  AttachmentUploader,
} from '@evidgate/cli';

import { AttachmentPipeline, type CliModule } from './attach.js';
import {
  ARTIFACT_A,
  ARTIFACT_B,
  attachment,
  fakeResult as sharedFakeResult,
  file,
  recordsOf,
  tempDir,
  warnOf,
} from './attach.testing.js';
import type { PlaywrightAttachment } from './attachMap.js';
import type { EventEnvelope } from './envelope.js';
import { EvidgateReporter, type EvidgateReporterOptions } from './reporter.js';

export {
  ARTIFACT_A,
  ARTIFACT_B,
  attachment,
  configFor,
  endOfRun,
  file,
  fullResult,
  gate,
  recordOf as recordAt,
  recordsOf,
  tempDir,
  warnOf,
} from './attach.testing.js';

/**
 * The QA probes were written against `duration: 11`; the shared helper defaults to 9 for the
 * delivered attach specs. The value lives here so every QA probe keeps its original fixture.
 */
export function fakeResult(
  attachments: readonly PlaywrightAttachment[],
): Parameters<EvidgateReporter['onTestEnd']>[1] {
  return sharedFakeResult(attachments, 11);
}

export const RUN_ID = 'run-qa-s87';
export const PRODUCER_ID = 'shard-1';
export const EXECUTION_KEY = 'c3'.repeat(32);
export const CASE_ID = '9f8e7d6c-5b4a-4392-8170-6f5e4d3c2b1a';
export const VARIANT_HASH = `sha256:${'ab'.repeat(32)}`;
/** Assembled at runtime so no token- or URL-shaped literal sits in the spec. */
export const fakeToken = ['qa', 's87', 'token'].join('-');
export const fakePresigned = `https://store.test/put?X-Amz-Signature=${'deadbeef'}`;

export const ALL_CAPS: AttachmentUploader['kindMaxBytes'] = {
  trace: 1024,
  video: 1024,
  screenshot: 1024,
  raw_report: undefined,
  dom_snapshot: undefined,
  a11y_snapshot: undefined,
  patch: undefined,
  ui_hierarchy: undefined,
};

export interface FakeUploader {
  uploader: AttachmentUploader;
  calls: AttachmentUploadInput[];
  /** A live read: the count keeps rising while the uploads run. */
  readonly peak: number;
}

export interface FakeUploaderOptions {
  readonly uploads?: (
    input: AttachmentUploadInput,
    index: number,
  ) => Promise<AttachmentUploadResult>;
  readonly caps?: Partial<AttachmentUploader['kindMaxBytes']>;
}

export function fakeUploader(options: FakeUploaderOptions = {}): FakeUploader {
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

export function fakeCli(factory: () => AttachmentUploader): CliModule {
  return { createAttachmentUploader: () => factory() } as unknown as CliModule;
}

export interface Harness {
  readonly pipeline: AttachmentPipeline;
  readonly uploader: FakeUploader;
  readonly root: string;
}

export function harness(options: FakeUploaderOptions = {}, deadlineMs?: number): Harness {
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

export function input(
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

export function png(dir: string, index: number): PlaywrightAttachment {
  const name = `shot-${String(index)}.png`;
  return attachment(file(dir, name).path, name, 'image/png');
}

export function fakeTest(
  title: string,
  line: number,
): Parameters<EvidgateReporter['onTestEnd']>[0] {
  return {
    id: `qa-${title}`,
    title,
    location: { file: 'tests/qa.spec.ts', line, column: 1 },
    annotations: [],
    expectedStatus: 'passed',
    parent: { project: () => ({ name: 'chromium' }) },
  } as unknown as Parameters<EvidgateReporter['onTestEnd']>[0];
}

export interface ReporterHarness {
  readonly reporter: EvidgateReporter;
  readonly spool: string;
}

export function reporterHarness(
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

export function spoolLines(spool: string): string[] {
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

export function envelopesOf(spool: string): EventEnvelope[] {
  return spoolLines(spool).map((line) => JSON.parse(line) as EventEnvelope);
}

/** The `ext.marker` of an envelope (the run-end marker is a `run.log` with `marker`). */
export function markerOf(envelope: EventEnvelope): string | undefined {
  const marker: unknown = envelope.ext.marker;
  return typeof marker === 'string' ? marker : undefined;
}

/** The `code` of every `ext.attachments` record, narrowed without trusting the shape. */
export function recordCodesOf(envelope: EventEnvelope | undefined): Array<string | undefined> {
  const records: readonly unknown[] = recordsOf(envelope);
  return records.map((record) =>
    typeof record === 'object' &&
    record !== null &&
    'code' in record &&
    typeof record.code === 'string'
      ? record.code
      : undefined,
  );
}

export function codesIn(spool: string): string[] {
  return envelopesOf(spool).flatMap((item) => {
    const { code } = warnOf(item);
    return code === undefined ? [] : [code];
  });
}
