// The deterministic large-report fixture: the repo keeps the generator and a loader-shaped
// directory (meta.json, input.json = generator parameters, expected.json = pinned checksums),
// never the report itself. These specs load that directory through the shared loader, run
// the generator from its parameters through the command line and compare the bytes.
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import {
  appendFileSync,
  mkdirSync,
  mkdtempSync,
  readdirSync,
  readFileSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { afterEach, describe, expect, it } from 'vitest';

import { loadFixture } from './loadFixture.js';

const REPO_ROOT = fileURLToPath(new URL('../../..', import.meta.url));
const GENERATOR = join(REPO_ROOT, 'scripts/dev/gen-large-report/gen-large-report.mjs');
const FIXTURE_DIR = join(REPO_ROOT, 'fixtures/large/100k-tests');

interface Sized {
  readonly tests: number;
  readonly bytes: number;
  readonly sha256: string;
}
interface Checksums {
  readonly seed: number;
  readonly full: Sized;
  readonly small: Sized;
  readonly budget: unknown;
}
interface Params {
  readonly seed: number;
  readonly tests: number;
  readonly small_tests: number;
}
interface Summary extends Sized {
  readonly seed: number;
}

const fixture = loadFixture(FIXTURE_DIR);
const checksums = fixture.expected as Checksums;
const params = JSON.parse(fixture.inputText) as Params;
const tempDirs: string[] = [];

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function tempDir(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-large-'));
  tempDirs.push(dir);
  return dir;
}

function generate(outDir: string, tests: number, seed = checksums.seed): Summary {
  const stdout = execFileSync(
    process.execPath,
    [GENERATOR, '--out-dir', outDir, '--tests', String(tests), '--seed', String(seed)],
    { encoding: 'utf8', maxBuffer: 1 << 20 },
  );
  return JSON.parse(stdout) as Summary;
}

function sha256(bytes: Buffer): string {
  return `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
}

/** The loader has no checksum rule; a fixture whose bytes are not stored pins them here. */
function assertChecksum(file: string, expected: string): void {
  const actual = sha256(readFileSync(file));
  if (actual !== expected) throw new Error(`${file}: checksum ${actual} != pinned ${expected}`);
}

type JsonObject = Record<string, unknown>;

const BUDGET_METRICS = [
  'parse_wall_ms',
  'reconcile_wall_ms',
  'walk_wall_ms',
  'child_peak_working_set_bytes',
  'parent_inflight_retained_bytes',
] as const;

/**
 * The budget contract of `expected.json`, enforced identically by both loaders: `unset` until a
 * timing run records numbers, `measured` with every field below. A partially filled measurement
 * must fail here rather than reach the budget test as a weaker limit.
 */
function assertBudgetContract(budget: unknown): void {
  const root = asJsonObject(budget, 'budget');
  const status = requiredString(root, 'status');
  if (status === 'unset') {
    require(Object.keys(root).length === 1, 'budget.unset must carry only status');
    return;
  }
  require(status === 'measured', 'budget.status must be unset or measured');
  const metadata = asJsonObject(requiredMember(root, 'measured_on'), 'budget.measured_on');
  require(isIsoDate(
    requiredString(metadata, 'date'),
  ), 'budget.measured_on.date must be YYYY-MM-DD');
  requiredString(metadata, 'host');
  const sha = requiredString(metadata, 'sha');
  require(sha.length >= 8 &&
    !/[^0-9a-fA-F]/.test(sha), 'budget.measured_on.sha must be 8+ hex digits');
  const headroom = asJsonObject(requiredMember(root, 'headroom'), 'budget.headroom');
  const wall = requiredNumber(headroom, 'wall');
  const memory = requiredNumber(headroom, 'memory');
  const retained = requiredNumber(headroom, 'retained');
  require(wall === 5 &&
    memory === 2 &&
    retained === 1.05, 'budget.headroom must be wall 5, memory 2 and retained 1.05');
  const measured = asJsonObject(requiredMember(root, 'measured'), 'budget.measured');
  const limits = asJsonObject(requiredMember(root, 'limits'), 'budget.limits');
  assertMetricKeys(measured, 'budget.measured');
  assertMetricKeys(limits, 'budget.limits');
  for (const metric of BUDGET_METRICS) {
    const value = requiredMetric(measured, metric);
    const limit = requiredMetric(limits, metric);
    const factor = headroomFactor(metric, { wall, memory, retained });
    require(limit >= value &&
      limit >= Math.ceil(value * factor), `budget.limits.${metric} is below measured/headroom`);
  }
}

// The child's resident set varies with host load, so it keeps a loose factor; the parent's retained managed heap is
// measured after a forced collection and repeats within a percent, so only a tight factor can see a buffering regression.
function headroomFactor(
  metric: string,
  factors: { wall: number; memory: number; retained: number },
): number {
  if (metric.endsWith('_wall_ms')) return factors.wall;
  return metric === 'parent_inflight_retained_bytes' ? factors.retained : factors.memory;
}

function assertMetricKeys(metrics: JsonObject, label: string): void {
  const complete =
    Object.keys(metrics).length === BUDGET_METRICS.length &&
    BUDGET_METRICS.every((metric) => Object.hasOwn(metrics, metric));
  require(complete, `${label} must have exactly the five budget metrics`);
}

function isJsonObject(value: unknown): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function asJsonObject(value: unknown, label: string): JsonObject {
  if (!isJsonObject(value)) throw new Error(`${label} must be an object`);
  return value;
}

function requiredMember(parent: JsonObject, name: string): unknown {
  if (!Object.hasOwn(parent, name)) throw new Error(`budget missing ${name}`);
  return parent[name];
}

function requiredString(parent: JsonObject, name: string): string {
  const value = requiredMember(parent, name);
  if (typeof value !== 'string' || value.trim().length === 0) {
    throw new Error(`budget.${name} must be a non-blank string`);
  }
  return value;
}

function requiredNumber(parent: JsonObject, name: string): number {
  const value = requiredMember(parent, name);
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    throw new Error(`budget.${name} must be a finite number`);
  }
  return value;
}

function requiredMetric(parent: JsonObject, name: string): number {
  const value = requiredNumber(parent, name);
  require(Number.isInteger(value) && value >= 0, `budget.${name} must be a nonnegative integer`);
  return value;
}

function require(condition: boolean, message: string): void {
  if (!condition) throw new Error(message);
}

function isIsoDate(text: string): boolean {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text);
  if (text.length !== 10 || match === null) return false;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  if (year < 1 || month < 1 || month > 12 || day < 1) return false;
  const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
  const days = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  return day <= (days[month - 1] ?? 0);
}

function measuredBudget(): JsonObject {
  return {
    status: 'measured',
    measured_on: { date: '2026-11-20', host: 'budget-host', sha: 'abcdef12' },
    headroom: { wall: 5, memory: 2, retained: 1.05 },
    measured: {
      parse_wall_ms: 1,
      reconcile_wall_ms: 1,
      walk_wall_ms: 1,
      child_peak_working_set_bytes: 1,
      parent_inflight_retained_bytes: 1,
    },
    limits: {
      parse_wall_ms: 5,
      reconcile_wall_ms: 5,
      walk_wall_ms: 5,
      child_peak_working_set_bytes: 2,
      parent_inflight_retained_bytes: 2,
    },
  };
}

describe('gen-large-report', () => {
  it('writes byte-identical output for the same seed and equals the committed small checksum', () => {
    const first = generate(tempDir(), checksums.small.tests);
    const second = generate(tempDir(), checksums.small.tests);

    expect(second).toEqual(first);
    expect(first.sha256).toBe(checksums.small.sha256);
    expect(first.bytes).toBe(checksums.small.bytes);
  });

  it('writes a different report for a different seed', () => {
    const firstDir = tempDir();
    const secondDir = tempDir();
    const first = generate(firstDir, 1_000, 1);
    const second = generate(secondDir, 1_000, 2);
    const testcaseLines = [firstDir, secondDir].map((dir) =>
      readFileSync(join(dir, 'input.xml'), 'utf8')
        .split('\n')
        .filter((line) => line.includes('<testcase ')),
    );

    expect(second.sha256).not.toBe(first.sha256);
    expect(testcaseLines[0]).toHaveLength(1_000);
    expect(testcaseLines[1]).toHaveLength(1_000);
    expect(testcaseLines[1]).not.toEqual(testcaseLines[0]);
  });

  it('generates the 100 000-test report equal to the committed checksum', () => {
    const outDir = tempDir();
    const started = performance.now();
    const summary = generate(outDir, checksums.full.tests);
    const elapsedMs = Math.round(performance.now() - started);

    // One printed line is the measured generation time and size; no budget is asserted here.
    console.log(`large-report 100k: ${elapsedMs} ms, ${summary.bytes} bytes, ${summary.sha256}`);
    expect(summary.sha256).toBe(checksums.full.sha256);
    expect(summary.bytes).toBe(checksums.full.bytes);
    assertChecksum(join(outDir, 'input.xml'), checksums.full.sha256);
  }, 120_000);

  it('declares exactly the cases it writes', () => {
    const outDir = tempDir();
    generate(outDir, 1_000);
    const xml = readFileSync(join(outDir, 'input.xml'), 'utf8');

    expect(xml.match(/<testcase /g)).toHaveLength(1_000);
    expect(xml).toContain('tests="1000"');
    expect(xml).not.toContain('\r');
  });

  it.each([['0'], ['-1'], ['1.5'], ['abc'], ['1000001']])('refuses --tests %s', (tests) => {
    expect(() =>
      execFileSync(process.execPath, [GENERATOR, '--out-dir', tempDir(), `--tests=${tests}`], {
        stdio: 'pipe',
      }),
    ).toThrowError(/--tests must be an integer/);
  });

  it('writes only input.xml into the target directory', () => {
    const outDir = tempDir();
    mkdirSync(join(outDir, 'nested'));
    generate(join(outDir, 'nested'), 10);

    expect(readdirSync(join(outDir, 'nested'))).toEqual(['input.xml']);
    expect(readdirSync(outDir)).toEqual(['nested']);
  });

  it('refuses a symlinked input.xml without changing the outside sentinel', () => {
    const root = tempDir();
    const outDir = join(root, 'out');
    const sentinel = join(root, 'sentinel.xml');
    const original = Buffer.from('outside report sentinel\n');
    mkdirSync(outDir);
    writeFileSync(sentinel, original);
    symlinkSync(sentinel, join(outDir, 'input.xml'));

    const result = spawnSync(process.execPath, [GENERATOR, '--out-dir', outDir, '--tests=10'], {
      encoding: 'utf8',
    });

    expect(result.status).toBe(2);
    expect(result.stderr).toMatch(/input\.xml.*symlink/i);
    expect(readFileSync(sentinel)).toEqual(original);
    expect(readdirSync(outDir)).toEqual(['input.xml']);
  });

  it('refuses a symlinked out-dir without changing its target directory', () => {
    const root = tempDir();
    const target = join(root, 'target');
    const outDir = join(root, 'out');
    const original = Buffer.from('target directory sentinel\n');
    mkdirSync(target);
    writeFileSync(join(target, 'input.xml'), original);
    symlinkSync(target, outDir, 'dir');

    const result = spawnSync(process.execPath, [GENERATOR, '--out-dir', outDir, '--tests=10'], {
      encoding: 'utf8',
    });

    expect(result.status).toBe(2);
    expect(result.stderr).toMatch(/out-dir.*symlink/i);
    expect(readdirSync(target)).toEqual(['input.xml']);
    expect(readFileSync(join(target, 'input.xml'))).toEqual(original);
  });
});

describe('large fixture through loadFixture', () => {
  it('loads from the committed directory with the generator parameters', () => {
    expect(fixture.domain).toBe('large');
    expect(fixture.name).toBe('100k-tests');
    expect(fixture.meta.addedBy).toBe('04.1.3-T3.1');
    expect(params.seed).toBe(checksums.seed);
    expect(params.tests).toBe(checksums.full.tests);
    expect(params.small_tests).toBe(checksums.small.tests);
    expect(() => assertBudgetContract(checksums.budget)).not.toThrow();
  });

  it('generates the small report from the parameters and matches its pinned checksum', () => {
    const outDir = tempDir();
    generate(outDir, params.small_tests, params.seed);

    assertChecksum(join(outDir, 'input.xml'), checksums.small.sha256);
  });

  it('refuses a report whose bytes changed', () => {
    const outDir = tempDir();
    generate(outDir, params.small_tests, params.seed);
    appendFileSync(join(outDir, 'input.xml'), ' ');

    expect(() => assertChecksum(join(outDir, 'input.xml'), checksums.small.sha256)).toThrowError(
      /checksum sha256:[0-9a-f]{64} != pinned/,
    );
  });
});

describe('large fixture budget contract', () => {
  it('accepts an unset budget and a fully measured one', () => {
    expect(() => assertBudgetContract({ status: 'unset' })).not.toThrow();
    expect(() => assertBudgetContract(measuredBudget())).not.toThrow();
  });

  it('LargeFixture_MeasuredBudgetShaTrailingNewline_Rejects', () => {
    const budget = measuredBudget();
    expect(() => assertBudgetContract(budget)).not.toThrow();
    asJsonObject(budget.measured_on, 'test measured_on').sha = 'abcdef12\n';
    expect(() => assertBudgetContract(budget)).toThrowError(/budget/);
  });

  it('LargeFixture_MeasuredBudgetDateTrailingNewline_Rejects', () => {
    const budget = measuredBudget();
    expect(() => assertBudgetContract(budget)).not.toThrow();
    asJsonObject(budget.measured_on, 'test measured_on').date = '2026-11-20\n';
    expect(() => assertBudgetContract(budget)).toThrowError(/budget/);
  });

  it('LargeFixture_MeasuredBudgetInvalidCalendarDate_Rejects', () => {
    const budget = measuredBudget();
    expect(() => assertBudgetContract(budget)).not.toThrow();
    asJsonObject(budget.measured_on, 'test measured_on').date = '2026-02-30';
    expect(() => assertBudgetContract(budget)).toThrowError(/budget/);
  });

  it('LargeFixture_MeasuredBudgetYear0099_Accepts', () => {
    const budget = measuredBudget();
    asJsonObject(budget.measured_on, 'test measured_on').date = '0099-01-01';
    expect(() => assertBudgetContract(budget)).not.toThrow();
  });

  it('refuses an unset budget that carries extra members', () => {
    expect(() => assertBudgetContract({ status: 'unset' })).not.toThrow();
    expect(() => assertBudgetContract({ status: 'unset', limits: {} })).toThrowError(
      /budget\.unset must carry only status/,
    );
  });

  const missingMemberCases = [
    { label: 'measured_on', parent: '', member: 'measured_on' },
    { label: 'headroom', parent: '', member: 'headroom' },
    { label: 'measured', parent: '', member: 'measured' },
    { label: 'limits', parent: '', member: 'limits' },
    { label: 'measured_on.date', parent: 'measured_on', member: 'date' },
    { label: 'measured_on.host', parent: 'measured_on', member: 'host' },
    { label: 'measured_on.sha', parent: 'measured_on', member: 'sha' },
    { label: 'headroom.wall', parent: 'headroom', member: 'wall' },
    { label: 'headroom.memory', parent: 'headroom', member: 'memory' },
    { label: 'headroom.retained', parent: 'headroom', member: 'retained' },
  ];

  it.each(missingMemberCases)('refuses a measured budget missing $label', ({ parent, member }) => {
    const budget = measuredBudget();
    expect(() => assertBudgetContract(budget)).not.toThrow();
    const target = parent === '' ? budget : asJsonObject(budget[parent], `test ${parent}`);
    expect(Reflect.deleteProperty(target, member)).toBe(true);
    expect(() => assertBudgetContract(budget)).toThrowError(/budget/);
  });

  const missingMetricCases = BUDGET_METRICS.flatMap((metric) =>
    ['measured', 'limits'].map((section) => ({ label: `${section}.${metric}`, section, metric })),
  );

  it.each(missingMetricCases)('refuses a measured budget missing $label', ({ section, metric }) => {
    const budget = measuredBudget();
    expect(() => assertBudgetContract(budget)).not.toThrow();
    const target = asJsonObject(budget[section], `test ${section}`);
    expect(Reflect.deleteProperty(target, metric)).toBe(true);
    expect(() => assertBudgetContract(budget)).toThrowError(/exactly the five budget metrics/);
  });

  const insufficientLimitCases = BUDGET_METRICS.flatMap((metric) => [
    { label: `${metric} below measured`, metric, condition: 'below_measured' },
    { label: `${metric} below headroom`, metric, condition: 'below_headroom' },
  ]);

  it.each(insufficientLimitCases)('refuses a limit $label', ({ metric, condition }) => {
    const budget = measuredBudget();
    expect(() => assertBudgetContract(budget)).not.toThrow();
    const limits = asJsonObject(budget.limits, 'test limits');
    const current = limits[metric];
    if (typeof current !== 'number') throw new Error(`test setup: limit ${metric} is not a number`);
    limits[metric] = condition === 'below_measured' ? 0 : current - 1;
    expect(() => assertBudgetContract(budget)).toThrowError(
      new RegExp(`budget\\.limits\\.${metric} is below measured/headroom`),
    );
  });

  const invalidMemberCases = [
    { label: 'measured_on.host', section: 'measured_on', member: 'host', value: ' ' },
    { label: 'measured_on.sha', section: 'measured_on', member: 'sha', value: 'not-a-sha' },
    { label: 'headroom.wall', section: 'headroom', member: 'wall', value: 0 },
    { label: 'headroom.wall factor', section: 'headroom', member: 'wall', value: 4 },
    { label: 'headroom.memory', section: 'headroom', member: 'memory', value: -1 },
    { label: 'headroom.memory factor', section: 'headroom', member: 'memory', value: 3 },
    { label: 'headroom.retained', section: 'headroom', member: 'retained', value: -1 },
    { label: 'headroom.retained factor', section: 'headroom', member: 'retained', value: 2 },
    { label: 'measured.parse_wall_ms', section: 'measured', member: 'parse_wall_ms', value: 1.5 },
    {
      label: 'measured.parent_inflight_retained_bytes',
      section: 'measured',
      member: 'parent_inflight_retained_bytes',
      value: -1,
    },
    { label: 'limits.walk_wall_ms', section: 'limits', member: 'walk_wall_ms', value: 5.5 },
  ];

  it.each(invalidMemberCases)('refuses the invalid member $label', ({ section, member, value }) => {
    const budget = measuredBudget();
    expect(() => assertBudgetContract(budget)).not.toThrow();
    asJsonObject(budget[section], `test ${section}`)[member] = value;
    expect(() => assertBudgetContract(budget)).toThrowError(/budget/);
  });

  it('refuses an unknown budget status', () => {
    const budget = measuredBudget();
    expect(() => assertBudgetContract(budget)).not.toThrow();
    budget.status = 'unknown';
    expect(() => assertBudgetContract(budget)).toThrowError(/unset or measured/);
  });

  it('refuses the dropped parent_peak_working_set_bytes metric', () => {
    const budget = measuredBudget();
    expect(() => assertBudgetContract(budget)).not.toThrow();
    asJsonObject(budget.measured, 'test measured').parent_peak_working_set_bytes = 1;
    expect(() => assertBudgetContract(budget)).toThrowError(/exactly the five budget metrics/);
  });
});
