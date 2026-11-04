// Shared fixtures for the attachment-pipeline specs: the pipeline harness over a temp root,
// the fake uploader and CLI surface, and the reporter seams that read the spool back.
//
// This file is not a spec: the shared Vitest base collects `src/**/*.test.ts` only, and
// tsconfig.json excludes it from the emitting project. tsconfig.spec.json type-checks it.
import { mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import type { FullConfig } from '@playwright/test/reporter';
import { afterEach } from 'vitest';

import type {
  AttachmentUploadContext,
  AttachmentUploadInput,
  AttachmentUploadResult,
  AttachmentUploader,
} from '@evidgate/cli';

import {
  AttachmentPipeline,
  type AttachmentProcessResult,
  type AttachmentWarning,
  type CliModule,
} from './attach.js';
import type { AttachmentRecord } from './attach.js';
import type { PlaywrightAttachment } from './attachMap.js';
import type { EventEnvelope } from './envelope.js';
import { EvidgateReporter, type EvidgateReporterOptions } from './reporter.js';

export const RUN_ID = 'run-attach-1';
export const PRODUCER_ID = 'shard-1';
export const EXECUTION_KEY = 'a1'.repeat(32);
export const CASE_ID = '0f9a6c3e-1b2d-4c5e-8f70-a1b2c3d4e5f6';
export const VARIANT_HASH = `sha256:${'cd'.repeat(32)}`;
/** Two artifact ids in canonical UUID form; the fake uploader answers with these. */
export const ARTIFACT_A = '11111111-1111-4111-8111-111111111111';
export const ARTIFACT_B = '22222222-2222-4222-8222-222222222222';
/** Assembled at runtime so no token- or URL-shaped literal is stored in the spec. */
export const fakeToken = ['attach', 'test', 'token'].join('-');
export const fakePresigned = `https://store.test/put?X-Amz-Signature=${'abc123'}`;

const tempDirs: string[] = [];

/** Removes every directory `tempDir` created; the hook below runs it after each case. */
export function cleanupTempDirs(): void {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
}

afterEach(cleanupTempDirs);

export function tempDir(prefix: string): string {
  const dir = mkdtempSync(join(tmpdir(), prefix));
  tempDirs.push(dir);
  return dir;
}

/** A real file under `dir`; returns its path and byte length. */
export function file(dir: string, name: string, size = 8): { path: string; size: number } {
  const path = join(dir, name);
  writeFileSync(path, Buffer.alloc(size, 0x50));
  return { path, size };
}

export function attachment(path: string, name: string, contentType: string): PlaywrightAttachment {
  return { name, path, body: undefined, contentType };
}

export interface FakeUploader {
  uploader: AttachmentUploader;
  /** Every input the uploader was asked for, in call order. */
  calls: AttachmentUploadInput[];
  /** The caller context of each call (drain signal and per-upload budget), in call order. */
  contexts: AttachmentUploadContext[];
  /** A LIVE read: the count keeps rising while the uploads run, never a snapshot at zero. */
  readonly peak: number;
}

export interface FakeUploaderOptions {
  readonly uploads?: (
    input: AttachmentUploadInput,
    index: number,
  ) => Promise<AttachmentUploadResult>;
  readonly kindMaxBytes?: Readonly<Record<string, number | undefined>>;
}

export function fakeUploader(options: FakeUploaderOptions = {}): FakeUploader {
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
export function gate(): { readonly opened: Promise<void>; open: () => void } {
  let release: () => void = () => undefined;
  const opened = new Promise<void>((resolve) => {
    release = resolve;
  });
  return { opened, open: () => release() };
}

/** The `@evidgate/cli` surface as the pipeline sees it: only the uploader factory is used. */
export function fakeCli(factory: () => AttachmentUploader): CliModule {
  return {
    createAttachmentUploader: () => factory(),
  } as unknown as CliModule;
}

export interface Harness {
  readonly pipeline: AttachmentPipeline;
  readonly uploader: FakeUploader;
  readonly root: string;
}

/** A pipeline whose allow-list is one temp directory and whose CLI is the fake above. */
export function harness(
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

export function codesOf(warnings: readonly AttachmentWarning[]): string[] {
  return warnings.map((warning) => warning.code);
}

export function recordOf(result: AttachmentProcessResult, index = 0): AttachmentRecord {
  const record = result.records[index];
  if (record === undefined) throw new Error(`no attachment record at index ${String(index)}`);
  return record;
}

/** The reporter with the attachment seams; `identity` supplies a real planned key. */
export function makeReporter(
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

export function configFor(root: string): FullConfig {
  return {
    rootDir: root,
    projects: [{ outputDir: join(root, 'test-results') }],
  } as unknown as FullConfig;
}

export function fakeTest(): Parameters<EvidgateReporter['onTestEnd']>[0] {
  return {
    id: 'attach-test-1',
    title: 'attaches evidence',
    location: { file: 'tests/attach.spec.ts', line: 4, column: 1 },
    annotations: [],
    expectedStatus: 'passed',
    parent: { project: () => ({ name: 'chromium' }) },
  } as unknown as Parameters<EvidgateReporter['onTestEnd']>[0];
}

export function fakeResult(
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

export function fullResult(): Parameters<EvidgateReporter['onEnd']>[0] {
  return { status: 'passed', startTime: new Date(), duration: 1 };
}

/**
 * Awaits the async `onEnd` arm. `onEnd` returns `undefined` for a passing run and a promise
 * once it reports a failure, so the helper awaits whatever the callback returned.
 */
export async function endOfRun(reporter: EvidgateReporter): Promise<unknown> {
  return await reporter.onEnd(fullResult());
}

export function spoolDirOf(spool: string): string {
  return join(spool, RUN_ID, PRODUCER_ID);
}

export function readSpoolLinesAt(spool: string): string[] {
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

export function readEnvelopesAt(spool: string): EventEnvelope[] {
  return readSpoolLinesAt(spool).map((line) => JSON.parse(line) as EventEnvelope);
}

/** The `ext.warn` payload of an envelope, narrowed without trusting its shape. */
export function warnOf(envelope: EventEnvelope): {
  code?: string;
  message?: string;
  count?: number;
} {
  const warn = envelope.ext.warn;
  return typeof warn === 'object' && warn !== null ? warn : {};
}

/** The `ext.attachments` records of an envelope (empty for every other event). */
export function recordsOf(envelope: EventEnvelope | undefined): AttachmentRecord[] {
  const value: unknown = envelope?.ext.attachments;
  // The pipeline built these records and the `ext` member is open, so the shape is ours.
  return Array.isArray(value) ? (value as AttachmentRecord[]) : [];
}

export function warningCodes(envelopes: readonly EventEnvelope[]): string[] {
  return envelopes
    .map((envelope) => warnOf(envelope).code)
    .filter((code): code is string => code !== undefined);
}
