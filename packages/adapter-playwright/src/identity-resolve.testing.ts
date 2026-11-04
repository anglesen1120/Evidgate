// Shared fixtures for the identity-resolve specs: fake Playwright test, suite and result
// objects, a fake sidecar index, a temp spool and temp sidecar directories, envelope readers
// for a reporter spool, and the env guards plus temp-dir cleanup every spec installs once.
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import type { FullResult, Suite, TestCase, TestResult } from '@playwright/test/reporter';
import { Ajv2020 } from 'ajv/dist/2020.js';
import type { ValidateFunction } from 'ajv/dist/2020.js';
import ajvFormats from 'ajv-formats';
import { afterEach, beforeEach } from 'vitest';

import type { EventEnvelope } from './envelope.js';
import { fake } from './fakes.testing.js';
import type { WarningPayload } from './identity.js';
import { EvidgateReporter, type EvidgateReporterOptions } from './reporter.js';
import type { SidecarIndex, SidecarWarning } from './sidecar.js';

const contractsDir = join(dirname(fileURLToPath(import.meta.url)), '../../../contracts');

const ajv = new Ajv2020({ allErrors: true, strict: false });
ajvFormats.default(ajv);

function compileContract(relativePath: string): ValidateFunction {
  const document = JSON.parse(readFileSync(join(contractsDir, relativePath), 'utf8')) as Record<
    string,
    unknown
  >;
  document['$id'] = `urn:evidgate:contract:${basename(relativePath, '.json')}`;
  return ajv.compile(document);
}

export const validateEnvelope = compileContract('schemas/event-envelope.v1.json');

/** RFC 8785 of `{}` is `{}`, so the empty-params hash is fixed and computable here. */
export const EMPTY_VARIANT_HASH = `sha256:${createHash('sha256').update('{}').digest('hex')}`;

export const CASE_A = '11111111-1111-1111-1111-111111111111';
export const CASE_B = '22222222-2222-2222-2222-222222222222';

const ENV_KEYS = ['EVIDGATE_TARGET_ENV', 'EVIDGATE_ALLOW_UNMAPPED'] as const;
const savedEnv: Record<string, string | undefined> = {};
const tempDirs: string[] = [];

export function useIdentityResolveSpec(): void {
  beforeEach(() => {
    for (const key of ENV_KEYS) {
      savedEnv[key] = process.env[key];
      delete process.env[key];
    }
  });

  afterEach(() => {
    for (const key of ENV_KEYS) {
      const value = savedEnv[key];
      if (value === undefined) delete process.env[key];
      else process.env[key] = value;
    }
    for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
  });
}

function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-identity-resolve-'));
  tempDirs.push(dir);
  return dir;
}

export interface FakeProject {
  name: string;
  metadata?: unknown;
  use?: unknown;
}

export interface FakeTestInput {
  id?: string;
  title?: string;
  file?: string;
  line?: number;
  column?: number;
  annotations?: Array<{ type: string; description?: string }>;
  project?: FakeProject;
}

export function fakeTest(input: FakeTestInput = {}): TestCase {
  const project = input.project ?? { name: 'chromium' };
  return fake<TestCase>({
    id: input.id ?? 'test-1',
    title: input.title ?? 'adds two numbers',
    location: {
      file: input.file ?? 'tests/math.spec.ts',
      line: input.line ?? 3,
      column: input.column ?? 1,
    },
    annotations: input.annotations ?? [],
    parent: { project: () => project },
  });
}

export function fakeResult(overrides: Record<string, unknown> = {}): TestResult {
  return fake<TestResult>({
    status: 'passed',
    duration: 1,
    retry: 0,
    steps: [],
    ...overrides,
  });
}

export function fakeSuite(tests: TestCase[]): Suite {
  return fake<Suite>({ allTests: () => tests });
}

/** A real temp directory holding the given files, so the sidecar loader's lstat sees them. */
export function sidecarDir(files: Record<string, string>): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-sidecar-'));
  tempDirs.push(dir);
  for (const [name, content] of Object.entries(files)) writeFileSync(join(dir, name), content);
  return dir;
}

export function fakeIndex(
  entries: Record<string, string>,
  warnings: SidecarWarning[] = [],
): SidecarIndex {
  return { lookup: (file: string) => entries[file], warnings, droppedWarnings: 0 };
}

export function makeReporter(overrides: Partial<EvidgateReporterOptions> = {}): {
  reporter: EvidgateReporter;
  spool: string;
} {
  const spool = tempSpool();
  const reporter = new EvidgateReporter({
    spool_dir: spool,
    run_id: 'run-1',
    producer_id: 'pw-0',
    engineVersion: '1.63.0',
    ...overrides,
  });
  return { reporter, spool };
}

export function readEnvelopes(spool: string): EventEnvelope[] {
  const dir = join(spool, 'run-1', 'pw-0');
  const envelopes: EventEnvelope[] = [];
  for (const file of readdirSync(dir)
    .filter((entry) => entry.endsWith('.jsonl'))
    .sort()) {
    for (const line of readFileSync(join(dir, file), 'utf8').split('\n')) {
      if (line.length > 0) envelopes.push(JSON.parse(line) as EventEnvelope);
    }
  }
  return envelopes;
}

export function warningsOf(envelopes: EventEnvelope[]): WarningPayload[] {
  return envelopes
    .filter((item) => item.type === 'run.log' && item.ext.warn !== undefined)
    .map((item) => item.ext.warn as WarningPayload);
}

export const fullResult = { status: 'passed' as const, startTime: new Date(), duration: 1 };

export function fullResultWith(status: FullResult['status']): FullResult {
  return { status, startTime: new Date(), duration: 1 };
}
