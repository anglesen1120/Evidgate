// 04.3.1-T1.3: the integration test (AC1/AC3/AC4). It runs the real `playwright test` CLI
// on the tiny browserless fixture project under test-fixtures/ with the reporter loaded by
// its package name, then validates every spooled line against the contracts/ schemas with
// Ajv 8 (2020-12 + ajv-formats), checks the real engine version, a real `test.step`, the
// test-level `ext.title`/`ext.project` keys and the default console summary. The reporter
// is compiled first so the test is independent of a prior build.
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { Ajv2020 } from 'ajv/dist/2020.js';
import type { ValidateFunction } from 'ajv/dist/2020.js';
import ajvFormats from 'ajv-formats';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';

import type { EventEnvelope } from './envelope.js';

const packageDir = join(dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = join(packageDir, '..', '..');
const fixtureDir = join(packageDir, 'test-fixtures');
const contractsDir = join(repoRoot, 'contracts');
const playwrightCli = join(repoRoot, 'node_modules', '@playwright', 'test', 'cli.js');
const tscBin = join(repoRoot, 'node_modules', 'typescript', 'bin', 'tsc');

const RUN_ID = 'run-e2e';
const PRODUCER_ID = 'pw-e2e-0';

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

let spool = '';
let outputDir = '';
let cliOutput = '';
let runFailure: string | null = null;

beforeAll(() => {
  // The fixture reporter resolves the package by name, so the compiled entry must exist
  // and be current; `tsc -b` is incremental.
  execFileSync(process.execPath, [tscBin, '-b'], { cwd: packageDir, stdio: 'pipe' });
  spool = mkdtempSync(join(tmpdir(), 'evidgate-e2e-'));
  outputDir = mkdtempSync(join(tmpdir(), 'evidgate-e2e-out-'));
  const run = spawnSync(
    process.execPath,
    [playwrightCli, 'test', '--config', join(fixtureDir, 'playwright.config.mjs')],
    {
      cwd: fixtureDir,
      env: {
        ...process.env,
        EVIDGATE_SPOOL_DIR: spool,
        EVIDGATE_RUN_ID: RUN_ID,
        EVIDGATE_E2E_OUTPUT_DIR: outputDir,
      },
      encoding: 'utf8',
      timeout: 170_000,
    },
  );
  cliOutput = `${run.stdout ?? ''}\n${run.stderr ?? ''}`;
  if (run.status !== 0) {
    runFailure = `playwright test exited with status ${String(run.status)}: ${cliOutput}`;
  }
}, 180_000);

afterAll(() => {
  for (const dir of [spool, outputDir]) {
    if (dir.length > 0) rmSync(dir, { recursive: true, force: true });
  }
});

function readEnvelopes(): EventEnvelope[] {
  const dir = join(spool, RUN_ID, PRODUCER_ID);
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

describe('reporter e2e (real playwright test CLI)', () => {
  it('AC1: 3 fixture tests produce one test.started and one test.finished each', () => {
    if (runFailure !== null) throw new Error(`playwright test failed: ${runFailure}`);

    const envelopes = readEnvelopes();
    expect(envelopes.filter((item) => item.type === 'test.started')).toHaveLength(3);
    expect(envelopes.filter((item) => item.type === 'test.finished')).toHaveLength(3);
    for (const envelope of envelopes.filter((item) => item.type === 'test.finished')) {
      expect(envelope.status).toBe('passed');
      expect(envelope.producer_id).toBe(PRODUCER_ID);
    }
  });

  it('AC3/AC4: the capability carrier is first and sequences are contiguous from 1', () => {
    if (runFailure !== null) throw new Error(`playwright test failed: ${runFailure}`);

    const envelopes = readEnvelopes();
    expect(envelopes[0]?.ext.capability).toBeDefined();
    expect(envelopes[0]?.sequence).toBe(1);
    expect(envelopes.map((item) => item.sequence)).toEqual(envelopes.map((_, index) => index + 1));
  });

  it('AC1/AC3/AC4: every spooled line validates and carries the real engine, step and test data', () => {
    if (runFailure !== null) throw new Error(`playwright test failed: ${runFailure}`);

    const envelopes = readEnvelopes();
    expect(envelopes.length).toBeGreaterThan(0);
    for (const envelope of envelopes) {
      const valid = validateEnvelope(envelope);
      expect(valid, JSON.stringify(validateEnvelope.errors)).toBe(true);
    }

    // The capability document travels on the first envelope and its engine version comes
    // from the real readInstalledPlaywrightVersion() (nothing is injected in the fixture).
    // `ext` is an open object, so narrow the carrier to the one field this test reads.
    const capability = envelopes[0]?.ext.capability as
      { engine?: { version?: string } } | undefined;
    expect(validateCapability(capability)).toBe(true);
    expect(capability?.engine?.version ?? '').toMatch(/^1\.63\./);

    // The fixture's user `test.step` must arrive as a real step envelope with an integer
    // duration (the schema requires duration_ms >= 0).
    const steps = envelopes.filter((item) => item.type === 'test.step');
    expect(steps.length).toBeGreaterThan(0);
    for (const step of steps) {
      expect(Number.isInteger(step.duration_ms)).toBe(true);
      expect(step.duration_ms ?? -1).toBeGreaterThanOrEqual(0);
    }
    const userStep = steps.find((item) => item.ext.category === 'test.step');
    expect(userStep).toBeDefined();
    expect(userStep?.ext.title).toBe('concatenate');

    // A test envelope carries the test-level ext keys (04 §6 allow-list).
    const finished = envelopes.find((item) => item.type === 'test.finished');
    expect(finished?.ext).toHaveProperty('title');
    expect(finished?.ext).toHaveProperty('project');
  });

  it('AC1: the CLI prints the default summary because printsToStdio() is false', () => {
    if (runFailure !== null) throw new Error(`playwright test failed: ${runFailure}`);

    expect(cliOutput).toContain('3 passed');
  });
});
