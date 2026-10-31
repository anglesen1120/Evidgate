// The `--json` line: exactly five members in a fixed order, one parseable object line, and
// redaction that neither hides the decision nor breaks the line.
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resetSecrets } from '../redact.js';

import type { GateDecisionValue } from './gateDecision.js';
import {
  cacheDirs,
  capture,
  decided,
  devEnv,
  gateArgv,
  invoke,
  runId,
  stubFetch,
} from './gate.kit.test.js';

const keys = ['decision', 'completeness', 'evaluation_id', 'mode', 'url'];

describe('gate --json', () => {
  const dirs = cacheDirs();

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await dirs.reset();
  });

  const decisions: ReadonlyArray<readonly [GateDecisionValue, number]> = [
    ['PASS', 0],
    ['FAIL', 1],
    ['INCONCLUSIVE', 2],
  ];

  for (const [decision, expected] of decisions) {
    it(`prints the five members in order for ${decision} and exits ${expected}`, async () => {
      const env = await devEnv(dirs);
      const stub = stubFetch(() => decided({ decision }));
      const out = capture();

      const { code } = await invoke(gateArgv(['--run', runId, '--json']), env, out, stub.fetchImpl);

      expect(code).toBe(expected);
      const text = out.stdout.join('');
      expect(text.split('\n')).toHaveLength(2);
      const parsed = JSON.parse(text) as Record<string, unknown>;
      expect(Object.keys(parsed)).toStrictEqual(keys);
      expect(parsed).toStrictEqual({
        decision,
        completeness: null,
        evaluation_id: null,
        mode: null,
        url: null,
      });
    });
  }

  it('keeps the line parseable when a member looks like a header', async () => {
    const env = await devEnv(dirs);
    // `Authorization: …` is the writer's header rule; a member that reads like one must not
    // swallow the closing quotes or braces of the object.
    const stub = stubFetch(() =>
      decided({ decision: 'PASS', mode: 'Authorization: x', url: 'https://api.example/r' }),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId, '--json']), env, out, stub.fetchImpl);

    expect(code).toBe(0);
    const text = out.stdout.join('');
    expect(text.split('\n')).toHaveLength(2);
    const parsed = JSON.parse(text) as Record<string, unknown>;
    expect(Object.keys(parsed)).toStrictEqual(keys);
    expect(parsed).toStrictEqual({
      decision: 'PASS',
      completeness: null,
      evaluation_id: null,
      mode: '[REDACTED]',
      url: 'https://api.example/r',
    });
  });

  it('strips a query from a printed url exactly once', async () => {
    const env = await devEnv(dirs);
    const stub = stubFetch(() =>
      decided({
        decision: 'PASS',
        url: 'https://s3.example/x?X-Amz-Signature=abc&X-Amz-Expires=900',
      }),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId, '--json']), env, out, stub.fetchImpl);

    expect(code).toBe(0);
    const text = out.stdout.join('');
    expect(text).not.toContain('X-Amz-Signature');
    // The member is redacted before encoding and again by the writer: one placeholder only.
    expect(JSON.parse(text)).toMatchObject({ url: 'https://s3.example/x?[REDACTED]' });
    expect(text.match(/\[REDACTED\]/gu)).toHaveLength(1);
  });

  it('redacts a token inside a printed member without shortening it', async () => {
    const env = await devEnv(dirs);
    const stub = stubFetch(() =>
      decided({ decision: 'PASS', url: `${'x'.repeat(190)}dev-token-value` }),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId, '--json']), env, out, stub.fetchImpl);

    expect(code).toBe(0);
    const line = out.stdout.join('').trimEnd();
    expect(line).toContain(`${'x'.repeat(190)}[REDACTED]`);
    expect(line).not.toContain('dev-token-value');
    expect(JSON.parse(line)).toMatchObject({ url: `${'x'.repeat(190)}[REDACTED]` });
  });

  it('prints decision null for a pending timeout', async () => {
    const env = await devEnv(dirs);
    const stub = stubFetch(() => decided({ decision: null }));
    const out = capture();

    const { code } = await invoke(
      gateArgv(['--run', runId, '--wait', '2s', '--json']),
      env,
      out,
      stub.fetchImpl,
    );

    expect(code).toBe(2);
    const parsed = JSON.parse(out.stdout.join('')) as Record<string, unknown>;
    expect(Object.keys(parsed)).toStrictEqual(keys);
    expect(parsed).toStrictEqual({
      decision: null,
      completeness: null,
      evaluation_id: null,
      mode: null,
      url: null,
    });
    expect(out.stderr.join('')).toBe(`evidgate: gate: run ${runId} is still pending after 2s\n`);
  });

  it('prints the optional members only when the server sends them as strings', async () => {
    const env = await devEnv(dirs);
    const stub = stubFetch(() =>
      decided({
        decision: 'FAIL',
        completeness: 'complete',
        evaluation_id: 'eval-1',
        mode: 'strict',
        url: 'https://evidgate.test/runs/1',
        mode_unused: 5,
      }),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId, '--json']), env, out, stub.fetchImpl);

    expect(code).toBe(1);
    const parsed = JSON.parse(out.stdout.join('')) as Record<string, unknown>;
    expect(parsed).toStrictEqual({
      decision: 'FAIL',
      completeness: 'complete',
      evaluation_id: 'eval-1',
      mode: 'strict',
      url: 'https://evidgate.test/runs/1',
    });
  });

  it('prints null for an optional member the server sends in another type', async () => {
    const env = await devEnv(dirs);
    const stub = stubFetch(() =>
      decided({ decision: 'PASS', completeness: 3, evaluation_id: {}, mode: null, url: [] }),
    );
    const out = capture();

    await invoke(gateArgv(['--run', runId, '--json']), env, out, stub.fetchImpl);

    const parsed = JSON.parse(out.stdout.join('')) as Record<string, unknown>;
    expect(parsed).toStrictEqual({
      decision: 'PASS',
      completeness: null,
      evaluation_id: null,
      mode: null,
      url: null,
    });
  });
});
