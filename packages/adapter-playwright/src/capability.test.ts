// 04.3.1-T1.3: the capability document and every emitted envelope validate against the
// contracts/ schemas with Ajv 8 (2020-12 + ajv-formats), the CLI half of the T42
// validator-parity rule (04 §7). Schemas are loaded by path relative to this test file.
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import type { TestCase, TestResult } from '@playwright/test/reporter';
import { Ajv2020 } from 'ajv/dist/2020.js';
import type { ValidateFunction } from 'ajv/dist/2020.js';
import ajvFormats from 'ajv-formats';
import { afterEach, describe, expect, it } from 'vitest';

import { buildCapabilityDocument } from './capability.js';
import type { EventEnvelope } from './envelope.js';
import { EvidgateReporter } from './reporter.js';

const contractsDir = join(dirname(fileURLToPath(import.meta.url)), '../../../contracts');

const ajv = new Ajv2020({ allErrors: true, strict: false });
ajvFormats.default(ajv);

function compileContract(relativePath: string): ValidateFunction {
  const document = JSON.parse(readFileSync(join(contractsDir, relativePath), 'utf8')) as Record<
    string,
    unknown
  >;
  // Address the schema by its canonical URI, as @evidgate/cli's loadContractSchemas does.
  document['$id'] = `urn:evidgate:contract:${basename(relativePath, '.json')}`;
  return ajv.compile(document);
}

const validateCapability = compileContract('adapter/capability.v1.json');
const validateEnvelope = compileContract('schemas/event-envelope.v1.json');

const tempDirs: string[] = [];

function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-capability-'));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function fakeTest(): TestCase {
  return {
    title: 'adds two numbers',
    location: { file: 'tests/math.spec.ts', line: 3, column: 1 },
    parent: { project: () => ({ name: 'chromium' }) },
  } as unknown as TestCase;
}

function fakeResult(): TestResult {
  return {
    status: 'passed',
    duration: 12,
    retry: 0,
    steps: [{ title: 'click', category: 'pw:api', duration: 4, steps: [] }],
  } as unknown as TestResult;
}

function readEnvelopes(spool: string): EventEnvelope[] {
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

describe('capability document', () => {
  it('AC3: the built document validates against contracts/adapter/capability.v1.json', () => {
    const document = buildCapabilityDocument({
      engineVersion: '1.63.0',
      stepEvents: true,
      mode: 'full',
      platforms: ['linux-x64'],
    });

    expect(validateCapability(document)).toBe(true);
    expect(document).toEqual({
      protocol: { major: 1, minor: 0 },
      engine: { family: 'playwright', version: '1.63.0' },
      platforms: ['linux-x64'],
      supports: {
        discovery: false,
        step_events: true,
        retry: true,
        cancel: false,
        capture_control: false,
      },
      mode: 'full',
    });
  });

  it('04.3.3-T2 / T97: supports.capture_control is declared false, never absent', () => {
    // An ABSENT flag means the run-create configuration document must be honoured
    // (capability.v1.json, contracts/adapter/README.md), which this reporter does not do;
    // `false` is what makes the server label the run `capture_uncontrolled` (T97).
    for (const mode of ['full', 'ingest_only'] as const) {
      for (const stepEvents of [true, false]) {
        const document = buildCapabilityDocument({
          engineVersion: '1.63.0',
          stepEvents,
          mode,
          platforms: ['linux-x64'],
        });
        expect(document.supports.capture_control).toBe(false);
        expect(Object.hasOwn(document.supports, 'capture_control')).toBe(true);
        expect(validateCapability(document), JSON.stringify(validateCapability.errors)).toBe(true);
      }
    }
  });

  it('AC3: the first envelope is the capability carrier at sequence 1', () => {
    const spool = tempSpool();
    const reporter = new EvidgateReporter({
      spool_dir: spool,
      run_id: 'run-1',
      producer_id: 'pw-0',
      engineVersion: '1.63.0',
    });
    reporter.onBegin();
    void reporter.onEnd({ status: 'passed', startTime: new Date(), duration: 1 });

    const envelopes = readEnvelopes(spool);
    const first = envelopes[0];
    expect(first?.sequence).toBe(1);
    expect(first?.type).toBe('run.log');
    const capability = first?.ext.capability;
    expect(capability).toBeDefined();
    expect(validateCapability(capability)).toBe(true);
  });

  it('AC1/AC3: every emitted envelope validates against contracts/schemas/event-envelope.v1.json', () => {
    const spool = tempSpool();
    const reporter = new EvidgateReporter({
      spool_dir: spool,
      run_id: 'run-1',
      producer_id: 'pw-0',
      engineVersion: '1.63.0',
    });
    reporter.onBegin();
    reporter.onTestBegin(fakeTest(), fakeResult());
    reporter.onTestEnd(fakeTest(), fakeResult());
    void reporter.onEnd({ status: 'passed', startTime: new Date(), duration: 1 });

    const envelopes = readEnvelopes(spool);
    expect(envelopes.length).toBeGreaterThan(0);
    for (const envelope of envelopes) {
      const valid = validateEnvelope(envelope);
      expect(valid, JSON.stringify(validateEnvelope.errors)).toBe(true);
    }
  });
});
