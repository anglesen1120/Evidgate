// 04.3.1-T1.1 / 04.3.2-T1.2: the `execution_key` placeholder and the injectable identity
// resolver seam. The server computes `execution_key` from the manifest (00 §7); the
// reporter never hashes it (04.3.2 AC4) and keeps emitting the schema-valid placeholder
// behind the resolver. The built-in 04.3.2-T1 mapping (annotation/sidecar → `ext.case_id`,
// `variant_hash`, `target_env`) is covered by identity-resolve.test.ts.
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import type { TestCase, TestResult } from '@playwright/test/reporter';
import { afterEach, describe, expect, it } from 'vitest';

import type { EventEnvelope } from './envelope.js';
import { PLACEHOLDER_EXECUTION_KEY, type IdentityResolver } from './identity.js';
import { EvidgateReporter, type EvidgateReporterOptions } from './reporter.js';

const tempDirs: string[] = [];

function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-identity-'));
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
  return { status: 'passed', duration: 1, retry: 0, steps: [] } as unknown as TestResult;
}

function makeReporter(overrides: Partial<EvidgateReporterOptions> = {}): {
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

describe('execution_key identity', () => {
  it('exposes a schema-valid placeholder', () => {
    expect(PLACEHOLDER_EXECUTION_KEY).toBe('0'.repeat(64));
    expect(PLACEHOLDER_EXECUTION_KEY).toMatch(/^[0-9a-f]{64}$/);
  });

  it('uses the placeholder by default for test and run.log envelopes', () => {
    const { reporter, spool } = makeReporter();
    reporter.onBegin();
    reporter.onTestBegin(fakeTest(), fakeResult());
    void reporter.onEnd({ status: 'passed', startTime: new Date(), duration: 1 });

    for (const envelope of readEnvelopes(spool)) {
      expect(envelope.execution_key).toBe(PLACEHOLDER_EXECUTION_KEY);
    }
  });

  it('uses the injected resolver for test envelopes and keeps the placeholder for run.log', () => {
    const resolved = 'a'.repeat(64);
    const identity: IdentityResolver = () => ({ execution_key: resolved });
    const { reporter, spool } = makeReporter({ identity });
    reporter.onBegin();
    reporter.onTestBegin(fakeTest(), fakeResult());
    reporter.onTestEnd(fakeTest(), fakeResult());
    void reporter.onEnd({ status: 'passed', startTime: new Date(), duration: 1 });

    const envelopes = readEnvelopes(spool);
    for (const envelope of envelopes.filter((item) => item.type !== 'run.log')) {
      expect(envelope.execution_key).toBe(resolved);
    }
    for (const envelope of envelopes.filter((item) => item.type === 'run.log')) {
      expect(envelope.execution_key).toBe(PLACEHOLDER_EXECUTION_KEY);
    }
  });

  it('AC4: the built-in resolver emits ext.case_id/variant_hash/target_env, never hashes execution_key', () => {
    const { reporter, spool } = makeReporter();
    const annotated = {
      id: 'test-annotated',
      title: 'adds two numbers',
      location: { file: 'tests/math.spec.ts', line: 3, column: 1 },
      annotations: [{ type: 'evidgate.id', description: '11111111-1111-1111-1111-111111111111' }],
      parent: { project: () => ({ name: 'chromium' }) },
    } as unknown as TestCase;
    reporter.onBegin();
    reporter.onTestBegin(annotated, fakeResult());
    reporter.onTestEnd(annotated, fakeResult());
    void reporter.onEnd({ status: 'passed', startTime: new Date(), duration: 1 });

    const finished = readEnvelopes(spool).find((item) => item.type === 'test.finished');
    expect(finished?.ext.case_id).toBe('11111111-1111-1111-1111-111111111111');
    expect(finished?.ext.variant_hash).toMatch(/^sha256:[0-9a-f]{64}$/);
    expect(finished?.ext.target_env).toBe('chromium');
    expect(finished?.execution_key).toBe(PLACEHOLDER_EXECUTION_KEY);
  });
});
