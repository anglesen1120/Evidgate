// QA 04.3.1-T1 (tier T2): reporter option/env boundary probes the existing specs do not
// cover — option-over-env precedence, the absent `EVIDGATE_PRODUCER_ID` fallback, an
// empty-string option, and the "callbacks never throw" contract for a malformed test
// object and a throwing identity resolver.
import { existsSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import type { TestCase, TestResult } from '@playwright/test/reporter';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { IdentityResolver } from './identity.js';
import { EvidgateReporter } from './reporter.js';

const tempDirs: string[] = [];
const ENV_KEYS = ['EVIDGATE_SPOOL_DIR', 'EVIDGATE_RUN_ID', 'EVIDGATE_PRODUCER_ID'] as const;
const savedEnv: Record<string, string | undefined> = Object.fromEntries(
  ENV_KEYS.map((key) => [key, process.env[key]]),
);

function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-reporter-boundary-'));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const key of ENV_KEYS) {
    const value = savedEnv[key];
    if (value === undefined) delete process.env[key];
    else process.env[key] = value;
  }
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function fakeTest(overrides: Record<string, unknown> = {}): TestCase {
  return {
    title: 'adds two numbers',
    location: { file: 'tests/math.spec.ts', line: 3, column: 1 },
    parent: { project: () => ({ name: 'chromium' }) },
    ...overrides,
  } as unknown as TestCase;
}

function fakeResult(): TestResult {
  return { status: 'passed', duration: 1, retry: 0, steps: [] } as unknown as TestResult;
}

const fullResult = { status: 'passed' as const, startTime: new Date(), duration: 1 };

describe('reporter option and env boundaries', () => {
  it('prefers the option over the env fallback for spool_dir and run_id', () => {
    const spool = tempSpool();
    process.env.EVIDGATE_SPOOL_DIR = join(spool, 'env-root');
    process.env.EVIDGATE_RUN_ID = 'env-run';

    const reporter = new EvidgateReporter({
      spool_dir: join(spool, 'opt-root'),
      run_id: 'opt-run',
      producer_id: 'pw-0',
      engineVersion: '1.63.0',
    });
    reporter.onBegin();
    reporter.onEnd(fullResult);

    expect(existsSync(join(spool, 'opt-root', 'opt-run', 'pw-0', '000001.jsonl'))).toBe(true);
    expect(existsSync(join(spool, 'env-root'))).toBe(false);
  });

  it('uses the env fallback when the option is absent', () => {
    const spool = tempSpool();
    process.env.EVIDGATE_SPOOL_DIR = spool;
    process.env.EVIDGATE_RUN_ID = 'env-run';

    const reporter = new EvidgateReporter({ producer_id: 'pw-0', engineVersion: '1.63.0' });
    reporter.onBegin();
    reporter.onEnd(fullResult);

    expect(existsSync(join(spool, 'env-run', 'pw-0', '000001.jsonl'))).toBe(true);
  });

  it('does not read EVIDGATE_PRODUCER_ID (no env fallback exists)', () => {
    const spool = tempSpool();
    process.env.EVIDGATE_PRODUCER_ID = 'from-env';

    // 04.3.3-T1 made `producer_id` optional (T54: it is derived from the shard at onBegin), so
    // the constructor no longer throws without it. The intent of this probe stands: the env
    // variable must never supply the id — the unsharded run derives `shard-1`, never `from-env`.
    const reporter = new EvidgateReporter({
      spool_dir: spool,
      run_id: 'run-1',
      engineVersion: '1.63.0',
    });
    reporter.onBegin();
    reporter.onEnd(fullResult);

    expect(existsSync(join(spool, 'run-1', 'shard-1', '000001.jsonl'))).toBe(true);
    expect(existsSync(join(spool, 'run-1', 'from-env'))).toBe(false);
  });

  it('treats an empty-string option as missing instead of falling back to the env', () => {
    const spool = tempSpool();
    process.env.EVIDGATE_SPOOL_DIR = spool;

    expect(
      () =>
        new EvidgateReporter({
          spool_dir: '',
          run_id: 'run-1',
          producer_id: 'pw-0',
          engineVersion: '1.63.0',
        }),
    ).toThrow(/spool_dir/);
  });
});

describe('callbacks never throw', () => {
  it('buffers a malformed test object from onTestBegin and fails the run at onEnd', async () => {
    const spool = tempSpool();
    const reporter = new EvidgateReporter({
      spool_dir: spool,
      run_id: 'run-1',
      producer_id: 'pw-0',
      engineVersion: '1.63.0',
    });
    reporter.onBegin();

    // No `location` and no `parent`: reading them throws inside the callback.
    const malformed = { title: 'no location' } as unknown as TestCase;
    const write = vi.spyOn(process.stderr, 'write').mockImplementation(() => true);
    try {
      expect(() => reporter.onTestBegin(malformed, fakeResult())).not.toThrow();
      await expect(reporter.onEnd(fullResult)).resolves.toEqual({ status: 'failed' });
      expect(String(write.mock.calls[0]?.[0])).toContain('[evidgate] reporter failure');
    } finally {
      write.mockRestore();
    }
  });

  it('buffers a throwing identity resolver from onTestBegin and fails the run at onEnd', async () => {
    const spool = tempSpool();
    const identity: IdentityResolver = () => {
      throw new Error('identity boom');
    };
    const reporter = new EvidgateReporter({
      spool_dir: spool,
      run_id: 'run-1',
      producer_id: 'pw-0',
      engineVersion: '1.63.0',
      identity,
    });
    reporter.onBegin();

    expect(() => reporter.onTestBegin(fakeTest(), fakeResult())).not.toThrow();
    const write = vi.spyOn(process.stderr, 'write').mockImplementation(() => true);
    try {
      await expect(reporter.onEnd(fullResult)).resolves.toEqual({ status: 'failed' });
      const line = String(write.mock.calls[0]?.[0]);
      expect(line).toContain('[evidgate] reporter failure');
      // The resolver's error text is caller-supplied and may carry a secret: only a fixed
      // code reaches stderr.
      expect(line).not.toContain('identity boom');
      expect(line).not.toContain(spool);
    } finally {
      write.mockRestore();
    }
  });

  it('prints a constant code for a caller-crafted error name or code', async () => {
    const spool = tempSpool();
    const identity: IdentityResolver = () => {
      throw Object.assign(new Error('token=secret-value'), {
        name: 'tok_live_abc',
        code: 'https://ingest.example.test/put?token=secret-value',
      });
    };
    const reporter = new EvidgateReporter({
      spool_dir: spool,
      run_id: 'run-1',
      producer_id: 'pw-0',
      engineVersion: '1.63.0',
      identity,
    });
    reporter.onBegin();
    reporter.onTestBegin(fakeTest(), fakeResult());
    const write = vi.spyOn(process.stderr, 'write').mockImplementation(() => true);
    try {
      await expect(reporter.onEnd(fullResult)).resolves.toEqual({ status: 'failed' });
      expect(String(write.mock.calls[0]?.[0])).toBe(
        "[evidgate] reporter failure (unexpected_error); the run's evidence is incomplete\n",
      );
    } finally {
      write.mockRestore();
    }
  });
});
