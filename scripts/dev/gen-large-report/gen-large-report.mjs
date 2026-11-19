#!/usr/bin/env node
// Deterministic pytest junitxml generator for the large-report fixture.
//
// Same (seed, tests) always yields byte-identical output on every platform: the only
// randomness is an integer-only PRNG, there is no clock, locale or hash-order input,
// numbers are formatted from integers, and every line ends with "\n". The report is
// written as input.xml into --out-dir (a fixed file name, so no caller input ever names a
// path) and a one-line JSON summary {tests, seed, bytes, sha256} goes to stdout.
import { createHash } from 'node:crypto';
import { closeSync, mkdirSync, openSync, writeSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { parseArgs } from 'node:util';

export const DEFAULT_TESTS = 100_000;
export const DEFAULT_SEED = 1;
export const REPORT_FILE = 'input.xml';
const MAX_TESTS = 1_000_000;
const BATCH = 1_000;

const FAILURE_PERMILLE = 20;
const ERROR_PERMILLE = 5;
const SKIP_PERMILLE = 10;
const CASES_PER_MODULE = 50;
const MODULES_PER_PACKAGE = 20;

const VERBS = ['creates', 'rejects', 'updates', 'lists', 'retries', 'expires', 'merges', 'renders'];
const NOUNS = ['invoice', 'session', 'ticket', 'webhook', 'report', 'policy', 'token', 'run'];
const FAILURE_TRACE = [
  'def test_body(client):',
  '>       assert response.status_code == expected',
  'E       AssertionError: assert 409 == 200',
  'E        +  where 409 = <Response [409]>.status_code',
  'tests/conftest.py:42: AssertionError',
];

/** mulberry32: 32-bit integer PRNG, identical on every JS engine. */
function mulberry32(seed) {
  let state = seed >>> 0;
  return () => {
    state = (state + 0x6d2b79f5) >>> 0;
    let t = state;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function seconds(ms) {
  return `${Math.trunc(ms / 1000)}.${String(ms % 1000).padStart(3, '0')}`;
}

function pad(value, width) {
  return String(value).padStart(width, '0');
}

function escapeXml(text) {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

/** Plans every case up front (cheap: three small integers each) so the suite header can declare exact counters. */
function planCases(tests, seed) {
  const next = mulberry32(seed);
  const cases = new Array(tests);
  const totals = { failures: 0, errors: 0, skipped: 0, ms: 0 };
  for (let i = 0; i < tests; i += 1) {
    const roll = Math.floor(next() * 1000);
    const ms = 1 + Math.floor(next() * 400);
    const verb = Math.floor(next() * VERBS.length);
    const noun = Math.floor(next() * NOUNS.length);
    let status = 'passed';
    if (roll < FAILURE_PERMILLE) status = 'failure';
    else if (roll < FAILURE_PERMILLE + ERROR_PERMILLE) status = 'error';
    else if (roll < FAILURE_PERMILLE + ERROR_PERMILLE + SKIP_PERMILLE) status = 'skipped';
    if (status === 'failure') totals.failures += 1;
    else if (status === 'error') totals.errors += 1;
    else if (status === 'skipped') totals.skipped += 1;
    totals.ms += ms;
    cases[i] = { status, ms, verb, noun };
  }
  return { cases, totals };
}

function renderCase(index, c) {
  const moduleIndex = Math.floor(index / CASES_PER_MODULE);
  const pkg = Math.floor(moduleIndex / MODULES_PER_PACKAGE);
  const classname = `tests.pkg_${pad(pkg, 4)}.test_mod_${pad(moduleIndex, 5)}`;
  const name = `test_${pad(index, 7)}_${VERBS[c.verb]}_${NOUNS[c.noun]}`;
  const open = `    <testcase classname="${classname}" name="${name}" time="${seconds(c.ms)}"`;
  if (c.status === 'passed') return `${open} />\n`;
  if (c.status === 'skipped') {
    return `${open}>\n      <skipped message="requires the sandbox gateway" type="pytest.skip" />\n    </testcase>\n`;
  }
  const tag = c.status === 'failure' ? 'failure' : 'error';
  const message = escapeXml(`AssertionError: expected 200, got 409 for ${NOUNS[c.noun]} ${index}`);
  const body = escapeXml(FAILURE_TRACE.join('\n'));
  return `${open}>\n      <${tag} message="${message}">${body}</${tag}>\n    </testcase>\n`;
}

/**
 * Writes the report into `outDir`.
 * @returns {{tests: number, seed: number, bytes: number, sha256: string}}
 */
export function generateLargeReport({ outDir, tests = DEFAULT_TESTS, seed = DEFAULT_SEED }) {
  if (!Number.isInteger(tests) || tests < 1 || tests > MAX_TESTS) {
    throw new RangeError(`--tests must be an integer in 1..${MAX_TESTS}`);
  }
  if (!Number.isInteger(seed) || seed < 0 || seed > 0xffffffff) {
    throw new RangeError('--seed must be an integer in 0..4294967295');
  }
  const { cases, totals } = planCases(tests, seed);
  mkdirSync(outDir, { recursive: true });
  const fd = openSync(join(outDir, REPORT_FILE), 'w');
  const hash = createHash('sha256');
  let bytes = 0;
  const emit = (text) => {
    const buffer = Buffer.from(text, 'utf8');
    writeSync(fd, buffer);
    hash.update(buffer);
    bytes += buffer.length;
  };
  try {
    emit(
      '<?xml version="1.0" encoding="utf-8"?>\n<testsuites>\n' +
        `  <testsuite name="pytest" errors="${totals.errors}" failures="${totals.failures}" ` +
        `skipped="${totals.skipped}" tests="${tests}" time="${seconds(totals.ms)}" ` +
        'timestamp="2026-01-01T00:00:00.000000" hostname="ci-runner">\n',
    );
    for (let start = 0; start < tests; start += BATCH) {
      let chunk = '';
      const end = Math.min(start + BATCH, tests);
      for (let i = start; i < end; i += 1) chunk += renderCase(i, cases[i]);
      emit(chunk);
    }
    emit('  </testsuite>\n</testsuites>\n');
  } finally {
    closeSync(fd);
  }
  return { tests, seed, bytes, sha256: `sha256:${hash.digest('hex')}` };
}

function main() {
  const { values } = parseArgs({
    options: {
      'out-dir': { type: 'string' },
      tests: { type: 'string', default: String(DEFAULT_TESTS) },
      seed: { type: 'string', default: String(DEFAULT_SEED) },
    },
    strict: true,
  });
  if (!values['out-dir']) {
    process.stderr.write('usage: gen-large-report.mjs --out-dir DIR [--tests N] [--seed S]\n');
    process.exit(2);
  }
  const integer = (text) => (/^\d+$/.test(text) ? Number(text) : Number.NaN);
  const summary = generateLargeReport({
    outDir: values['out-dir'],
    tests: integer(values.tests),
    seed: integer(values.seed),
  });
  process.stdout.write(`${JSON.stringify(summary)}\n`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    main();
  } catch (error) {
    process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
    process.exit(2);
  }
}
