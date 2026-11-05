// The identity gate at onEnd: conflict and unmapped accounting, EVIDGATE_ALLOW_UNMAPPED, the
// single stderr line, the warning-envelope cap and its summary, and the status the reporter
// hands back to Playwright.
import { join } from 'node:path';

import { describe, expect, it, vi } from 'vitest';

import {
  CASE_A,
  CASE_B,
  fakeResult,
  fakeSuite,
  fakeTest,
  fullResult,
  fullResultWith,
  makeReporter,
  readEnvelopes,
  sidecarDir,
  useIdentityResolveSpec,
  warningsOf,
} from './identity-resolve.testing.js';
import { MAX_WARNING_ENVELOPES } from './reporter.js';
import { SIDECAR_FILE_NAME } from './sidecar.js';

describe('reporter identity wiring', () => {
  useIdentityResolveSpec();

  it('AC2: a conflict emits identity_conflict, no case_id, and onEnd fails', async () => {
    const dir = sidecarDir({
      'math.spec.ts': '',
      'evidgate.cases.yaml': `version: 1\ncases:\n  - file: math.spec.ts\n    evidgate.id: ${CASE_B}\n`,
    });
    const { reporter, spool } = makeReporter();
    const test = fakeTest({
      id: 't1',
      file: join(dir, 'math.spec.ts'),
      annotations: [{ type: 'evidgate.id', description: CASE_A }],
    });
    reporter.onBegin(undefined, fakeSuite([test]));
    reporter.onTestBegin(test, fakeResult());
    reporter.onTestEnd(test, fakeResult());

    const finished = readEnvelopes(spool).find((item) => item.type === 'test.finished');
    expect(finished?.ext.identity_conflict).toBe(true);
    expect(finished?.ext.case_id).toBeUndefined();

    await expect(reporter.onEnd(fullResult)).resolves.toEqual({ status: 'failed' });
  });

  it('AC2: EVIDGATE_ALLOW_UNMAPPED=1 keeps an unmapped run from failing', () => {
    process.env.EVIDGATE_ALLOW_UNMAPPED = '1';
    const { reporter } = makeReporter();
    reporter.onBegin();
    reporter.onTestBegin(fakeTest(), fakeResult());
    expect(reporter.onEnd(fullResult)).toBeUndefined();
  });

  it('AC2: an unmapped-only run fails without the flag', async () => {
    const { reporter, spool } = makeReporter();
    reporter.onBegin();
    reporter.onTestBegin(fakeTest(), fakeResult());
    await expect(reporter.onEnd(fullResult)).resolves.toEqual({ status: 'failed' });

    const summary = warningsOf(readEnvelopes(spool)).find((warning) => warning.code === 'unmapped');
    expect(summary?.count).toBe(1);
    expect(summary?.message).toContain(
      '(0 conflicted, 1 no case_id, 0 no variant_hash, 0 no target_env)',
    );
  });

  it('AC2: a fully mapped run does not fail', () => {
    const { reporter } = makeReporter();
    const test = fakeTest({ annotations: [{ type: 'evidgate.id', description: CASE_A }] });
    reporter.onBegin();
    reporter.onTestBegin(test, fakeResult());
    expect(reporter.onEnd(fullResult)).toBeUndefined();
  });

  it('caps warning envelopes and summarises the overflow once', () => {
    const { reporter, spool } = makeReporter();
    reporter.onBegin();
    const total = MAX_WARNING_ENVELOPES + 50;
    for (let index = 0; index < total; index += 1) {
      reporter.onTestBegin(
        fakeTest({
          id: `t${index}`,
          file: `tests/t${index}.spec.ts`,
          annotations: [{ type: 'evidgate.id', description: CASE_A }],
        }),
        fakeResult(),
      );
    }
    void reporter.onEnd(fullResult);

    const warnings = warningsOf(readEnvelopes(spool));
    expect(warnings.filter((warning) => warning.code === 'duplicate_case_id')).toHaveLength(
      MAX_WARNING_ENVELOPES,
    );
    const truncated = warnings.find((warning) => warning.code === 'warnings_truncated');
    expect(truncated?.count).toBe(total - 1 - MAX_WARNING_ENVELOPES);
  });

  it('C3: writes one stderr line when the identity gate trips and overrides only a passed run', async () => {
    const write = vi.spyOn(process.stderr, 'write').mockImplementation(() => true);
    try {
      const { reporter } = makeReporter();
      reporter.onBegin();
      reporter.onTestBegin(fakeTest(), fakeResult());
      await expect(reporter.onEnd(fullResult)).resolves.toEqual({ status: 'failed' });
      expect(write).toHaveBeenCalledTimes(1);
      const line = String(write.mock.calls[0]?.[0]);
      expect(line).toContain('[evidgate]');
      expect(line).toContain('EVIDGATE_ALLOW_UNMAPPED=1');
      expect(line).toContain('(0 conflicted, 1 no case_id, 0 no variant_hash, 0 no target_env)');
    } finally {
      write.mockRestore();
    }
  });

  it('C3: keeps an interrupted run status but still reports the identity gate', () => {
    const write = vi.spyOn(process.stderr, 'write').mockImplementation(() => true);
    try {
      const { reporter } = makeReporter();
      reporter.onBegin();
      reporter.onTestBegin(fakeTest(), fakeResult());
      expect(reporter.onEnd(fullResultWith('interrupted'))).toBeUndefined();
      expect(write).toHaveBeenCalledTimes(1);
    } finally {
      write.mockRestore();
    }
  });

  it('C3: writes no stderr line when the gate is allowed', () => {
    const write = vi.spyOn(process.stderr, 'write').mockImplementation(() => true);
    try {
      process.env.EVIDGATE_ALLOW_UNMAPPED = '1';
      const { reporter } = makeReporter();
      reporter.onBegin();
      reporter.onTestBegin(fakeTest(), fakeResult());
      expect(reporter.onEnd(fullResult)).toBeUndefined();
      expect(write).not.toHaveBeenCalled();
    } finally {
      write.mockRestore();
    }
  });

  it('C4: the unmapped summary bypasses the warning cap', () => {
    const { reporter, spool } = makeReporter();
    reporter.onBegin();
    const total = MAX_WARNING_ENVELOPES + 50;
    for (let index = 0; index < total; index += 1) {
      reporter.onTestBegin(
        fakeTest({
          id: `t${index}`,
          file: `tests/t${index}.spec.ts`,
          annotations: [{ type: 'evidgate.id', description: CASE_A }],
        }),
        fakeResult(),
      );
    }
    reporter.onTestBegin(
      fakeTest({ id: 'unmapped', file: 'tests/unmapped.spec.ts' }),
      fakeResult(),
    );
    void reporter.onEnd(fullResult);

    const warnings = warningsOf(readEnvelopes(spool));
    expect(warnings.filter((warning) => warning.code === 'duplicate_case_id')).toHaveLength(
      MAX_WARNING_ENVELOPES,
    );
    expect(warnings.find((warning) => warning.code === 'unmapped')?.count).toBe(1);
    expect(warnings.find((warning) => warning.code === 'warnings_truncated')?.count).toBe(
      total - 1 - MAX_WARNING_ENVELOPES,
    );
  });

  it('P3: counts the sidecar loader drops in warnings_truncated', () => {
    const entries = Array.from(
      { length: 2001 },
      (_, index) => `  - file: missing-${index}.spec.ts\n    evidgate.id: ${CASE_A}\n`,
    ).join('');
    const manifest = `version: 1\ncases:\n${entries}`;
    const dir = sidecarDir({ 'a.spec.ts': '', [SIDECAR_FILE_NAME]: manifest });
    const { reporter, spool } = makeReporter();
    const test = fakeTest({ id: 't1', file: join(dir, 'a.spec.ts') });
    reporter.onBegin(undefined, fakeSuite([test]));
    void reporter.onEnd(fullResult);

    const truncated = warningsOf(readEnvelopes(spool)).find(
      (warning) => warning.code === 'warnings_truncated',
    );
    // 2001 loader warnings: 2000 kept (100 emitted, 1900 capped) + 1 dropped by the
    // sidecar's own cap.
    expect(truncated?.count).toBe(2001 - MAX_WARNING_ENVELOPES);
  });

  it('S3: a test whose variant params were refused counts toward the identity gate', async () => {
    const { reporter, spool } = makeReporter();
    const test = fakeTest({
      annotations: [{ type: 'evidgate.id', description: CASE_A }],
      project: {
        name: 'chromium',
        metadata: { evidgate: { params: { api_token: 'secret-value' } } },
      },
    });
    reporter.onBegin();
    reporter.onTestBegin(test, fakeResult());
    const gate = await reporter.onEnd(fullResult);
    const started = readEnvelopes(spool).find((item) => item.type === 'test.started');
    expect(started?.ext.case_id).toBe(CASE_A);
    expect(started?.ext.variant_hash).toBeUndefined();
    expect(gate).toEqual({ status: 'failed' });
  });

  it('S3: a test with no valid target env counts toward the identity gate', async () => {
    const { reporter, spool } = makeReporter();
    const test = fakeTest({
      annotations: [{ type: 'evidgate.id', description: CASE_A }],
      project: { name: 'Chromium Desktop' },
    });
    reporter.onBegin();
    reporter.onTestBegin(test, fakeResult());
    const gate = await reporter.onEnd(fullResult);
    const started = readEnvelopes(spool).find((item) => item.type === 'test.started');
    expect(started?.ext.case_id).toBe(CASE_A);
    expect(started?.ext.target_env).toBeUndefined();
    expect(gate).toEqual({ status: 'failed' });
  });

  it('S3: warns once per project when neither the env nor the project name is valid', () => {
    const { reporter, spool } = makeReporter();
    const chromium = { name: 'Chromium Desktop' };
    const firefox = { name: 'Firefox Desktop' };
    const tests = [
      fakeTest({
        id: 't1',
        annotations: [{ type: 'evidgate.id', description: CASE_A }],
        project: chromium,
      }),
      fakeTest({
        id: 't2',
        file: 'tests/b.spec.ts',
        annotations: [{ type: 'evidgate.id', description: CASE_B }],
        project: chromium,
      }),
      fakeTest({
        id: 't3',
        file: 'tests/c.spec.ts',
        annotations: [{ type: 'evidgate.id', description: CASE_A }],
        project: firefox,
      }),
    ];
    reporter.onBegin();
    for (const test of tests) reporter.onTestBegin(test, fakeResult());
    void reporter.onEnd(fullResult);

    const invalid = warningsOf(readEnvelopes(spool)).filter(
      (warning) => warning.code === 'invalid_target_env',
    );
    expect(invalid).toHaveLength(2);
    expect(JSON.stringify(invalid)).not.toContain('Desktop');
  });
});
