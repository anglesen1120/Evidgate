// 04.3.2 §8 performance row, made load-tolerant (04.3.3-T1, Note from #49): the identity
// map is built once at onBegin and the variant hash is memoised per project, so the cost of
// resolving N tests is linear in N.
//
// The old budget was wall-clock (`5,000 tests in under 500 ms`) and failed once in a full
// `verify.sh` while three review agents ran on the same host (#49, 77a5a64). Wall time
// measures the host's load, not this code. These cases instead measure **process CPU time**
// (`process.cpuUsage`) and assert **complexity**: 4× the input may cost at most 8× the CPU
// (linear ⇒ ~4×, quadratic ⇒ ~16×), and the cost per item may not grow. CPU time is what
// this process itself spent, so other tenants on a busy host do not inflate it; the 2×
// margin over linear absorbs cache effects. A quadratic regression — the failure this test
// exists for, such as a per-test sidecar read or a per-entry scan of all entries — is 16×.
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import type { TestCase } from '@playwright/test/reporter';
import { describe, expect, it } from 'vitest';

import { createIdentityResolver } from './identity.js';
import { loadSidecarIndex, SIDECAR_FILE_NAME, type SidecarFs } from './sidecar.js';

const SMALL = 1000;
/** 4× the small input: linear ⇒ ~4× the cost, quadratic ⇒ ~16×. */
const LARGE = SMALL * 4;
/** Allowed CPU growth for 4× the input. */
const MAX_GROWTH = 8;
/** CPU-time samples per size; the minimum is the least disturbed sample. */
const SAMPLES = 3;

const project = {
  name: 'chromium',
  metadata: { evidgate: { params: { browser: 'chromium', mode: 'full', locale: 'en-US' } } },
};

function caseId(index: number): string {
  // index + 1 so the first id is never the nil UUID (refused by the strict canonical
  // case-id check, `isCanonicalCaseId`, on both the annotation and the sidecar path).
  return `${(index + 1).toString(16).padStart(8, '0')}-0000-4000-8000-000000000000`;
}

function syntheticTest(index: number): TestCase {
  return {
    id: `test-${String(index)}`,
    title: `parametrised case ${String(index)}`,
    location: { file: `tests/case-${String(index)}.spec.ts`, line: index + 1, column: 1 },
    annotations: [{ type: 'evidgate.id', description: caseId(index) }],
    parent: { project: () => project },
  } as unknown as TestCase;
}

function sidecarTest(file: string, index: number): TestCase {
  return {
    id: `test-${String(index)}`,
    title: `case ${String(index)}`,
    location: { file, line: 1, column: 1 },
    annotations: [],
    parent: { project: () => project },
  } as unknown as TestCase;
}

/**
 * One real sidecar fixture: `count` empty test files in `dir` plus a manifest mapping each
 * of them. The fixture is written before any measurement, so its I/O is never timed.
 */
function writeSidecarFixture(dir: string, count: number): string[] {
  const entries: string[] = [];
  const files: string[] = [];
  for (let index = 0; index < count; index += 1) {
    const name = `case-${String(index)}.spec.ts`;
    writeFileSync(join(dir, name), '');
    files.push(join(dir, name));
    entries.push(`  - file: ${name}\n    evidgate.id: ${caseId(index)}\n`);
  }
  writeFileSync(join(dir, SIDECAR_FILE_NAME), `version: 1\ncases:\n${entries.join('')}`);
  return files;
}

/** The CPU time (user + system, milliseconds) this process spent running `work`. */
function cpuMs(work: () => void): number {
  const start = process.cpuUsage();
  work();
  const delta = process.cpuUsage(start);
  return (delta.user + delta.system) / 1000;
}

/**
 * The two CPU times measured **alternately** (small, large, small, large, …), each taken as
 * the minimum of {@link SAMPLES} samples. Alternating is what makes the ratio honest: a
 * mid-run CPU-frequency or QoS shift (E-core migration, CI contention) that persists would
 * inflate whichever size ran second if the samples were grouped, and a min of grouped
 * samples cannot see a persistent shift.
 */
function cpuPairMs(small: () => void, large: () => void): [number, number] {
  let smallBest = Number.POSITIVE_INFINITY;
  let largeBest = Number.POSITIVE_INFINITY;
  for (let sample = 0; sample < SAMPLES; sample += 1) {
    smallBest = Math.min(smallBest, cpuMs(small));
    largeBest = Math.min(largeBest, cpuMs(large));
  }
  return [smallBest, largeBest];
}

/** Asserts the linear shape: 4× the input, at most {@link MAX_GROWTH}× the CPU. */
function expectLinearGrowth(label: string, smallCpu: number, largeCpu: number): void {
  expect(smallCpu).toBeGreaterThan(0);
  expect(
    largeCpu / smallCpu,
    `${label}: 4× the input must not cost more than ${String(MAX_GROWTH)}× the CPU`,
  ).toBeLessThanOrEqual(MAX_GROWTH);
  // Quadratic cost grows 4× per item at this ratio; linear stays flat.
  expect(
    largeCpu / LARGE,
    `${label}: the CPU cost per item must not grow with the input`,
  ).toBeLessThanOrEqual((smallCpu / SMALL) * 2.5);
}

describe('identity resolution complexity (load-tolerant, #49)', () => {
  it('resolves 4× the tests in linear CPU time, with no per-item growth', () => {
    const smallTests = Array.from({ length: SMALL }, (_, index) => syntheticTest(index));
    const largeTests = Array.from({ length: LARGE }, (_, index) => syntheticTest(index));

    // A fresh resolver per sample, after one warm-up, so each sample pays the resolution
    // work rather than a memo hit (a warm cache would hide a quadratic path).
    const measure = (tests: TestCase[]): void => {
      const resolver = createIdentityResolver({});
      for (const test of tests) resolver(test);
    };
    measure(smallTests.slice(0, 10));

    const [smallCpu, largeCpu] = cpuPairMs(
      () => measure(smallTests),
      () => measure(largeTests),
    );
    expectLinearGrowth('resolver', smallCpu, largeCpu);

    // The work is real: every test resolves to the one project's variant hash and case id.
    const resolver = createIdentityResolver({});
    const identities = largeTests.map((test) => resolver(test));
    expect(identities).toHaveLength(LARGE);
    expect(new Set(identities.map((identity) => identity.variant_hash)).size).toBe(1);
    expect(identities.every((identity) => identity.case_id !== undefined)).toBe(true);
  });

  it('loads a 4× larger sidecar in linear CPU time and reads each manifest once', () => {
    const smallDir = mkdtempSync(join(tmpdir(), 'evidgate-perf-small-'));
    const largeDir = mkdtempSync(join(tmpdir(), 'evidgate-perf-large-'));
    try {
      const smallFiles = writeSidecarFixture(smallDir, SMALL);
      const largeFiles = writeSidecarFixture(largeDir, LARGE);

      // Timed: the real onBegin identity work — loadSidecarIndex (one manifest read, YAML
      // parse, Ajv validation, one existsSync per entry) plus the resolver over every test.
      const measure = (files: string[]): void => {
        const index = loadSidecarIndex(files);
        const resolver = createIdentityResolver({ sidecar: index });
        files.forEach((file, position) => resolver(sidecarTest(file, position)));
      };
      measure(smallFiles.slice(0, 10));

      const [smallCpu, largeCpu] = cpuPairMs(
        () => measure(smallFiles),
        () => measure(largeFiles),
      );
      expectLinearGrowth('sidecar load', smallCpu, largeCpu);

      // Deterministic companion of the CPU bound: the loader opens exactly one manifest no
      // matter how many tests point at it, so a per-test re-read cannot creep back in.
      let reads = 0;
      const countingFs: SidecarFs = {
        readFile: () => {
          reads += 1;
          return undefined;
        },
      };
      loadSidecarIndex(largeFiles, countingFs);
      expect(reads).toBe(1);

      // Every entry still maps: the index is complete, not truncated by a cap.
      const index = loadSidecarIndex(largeFiles);
      const resolver = createIdentityResolver({ sidecar: index });
      const identities = largeFiles.map((file, position) => resolver(sidecarTest(file, position)));
      expect(identities.every((identity) => identity.case_id !== undefined)).toBe(true);
    } finally {
      for (const dir of [smallDir, largeDir]) rmSync(dir, { recursive: true, force: true });
    }
  });
});
