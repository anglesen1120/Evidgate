// 04.3.1-T1.2: 4-byte emoji and surrogate pairs in `test.title` survive the JSON round
// trip into the spool file (04 §7 boundary list).
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import type { TestCase, TestResult } from '@playwright/test/reporter';
import { afterEach, describe, expect, it } from 'vitest';

import type { EventEnvelope } from './envelope.js';
import { EvidgateReporter } from './reporter.js';

const tempDirs: string[] = [];

function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-unicode-'));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function fakeTest(title: string): TestCase {
  return {
    title,
    location: { file: 'tests/unicode.spec.ts', line: 1, column: 1 },
    parent: { project: () => ({ name: 'chromium' }) },
  } as unknown as TestCase;
}

function fakeResult(): TestResult {
  return { status: 'passed', duration: 1, retry: 0, steps: [] } as unknown as TestResult;
}

describe('unicode titles', () => {
  it('round-trips a 4-byte emoji, a combining accent and a ZWJ sequence', () => {
    const title = 'renders 🧪 café 𝄞 and 👩‍👩‍👧‍👦';
    const spool = tempSpool();
    const reporter = new EvidgateReporter({
      spool_dir: spool,
      run_id: 'run-1',
      producer_id: 'pw-0',
      engineVersion: '1.63.0',
    });
    reporter.onBegin();
    reporter.onTestBegin(fakeTest(title), fakeResult());
    reporter.onEnd({ status: 'passed', startTime: new Date(), duration: 1 });

    const dir = join(spool, 'run-1', 'pw-0');
    const raw = readdirSync(dir)
      .filter((entry) => entry.endsWith('.jsonl'))
      .sort()
      .map((entry) => readFileSync(join(dir, entry), 'utf8'))
      .join('');
    const envelopes = raw
      .split('\n')
      .filter((line) => line.length > 0)
      .map((line) => JSON.parse(line) as EventEnvelope);

    const started = envelopes.find((item) => item.type === 'test.started');
    expect(started?.ext.title).toBe(title);
    expect(raw).toContain(title);
  });
});
