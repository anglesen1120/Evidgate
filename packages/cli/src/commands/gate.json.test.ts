// The `--json` line: the five verdict keys in their original order, then the members the
// widened `Decision` body adds, one parseable object line, and redaction that neither hides
// the decision nor breaks the line.
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resetSecrets } from '../redact.js';

import { devStubFetch } from './devExchange.testing.js';
import type { GateDecisionValue } from './gateDecision.js';
import {
  cacheDirs,
  capture,
  decided,
  devEnv,
  evaluated,
  gateArgv,
  invoke,
  pending,
  runId,
} from './gate.kit.test.js';

const keys = [
  'decision',
  'completeness',
  'evaluation_id',
  'mode',
  'url',
  'status',
  'evidence_revision',
  'outcome',
  'missing',
  'reasons',
  'policy_hash',
  'manifest_hash',
  'evaluator_version',
];

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
    it(`prints the members in order for ${decision} and exits ${expected}`, async () => {
      const env = await devEnv(dirs);
      const stub = devStubFetch(() => decided(evaluated({ decision })));
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
        status: 'evaluated',
        evidence_revision: null,
        outcome: null,
        missing: [],
        reasons: null,
        policy_hash: null,
        manifest_hash: null,
        evaluator_version: null,
      });
    });
  }

  it('prints a pending body with every evaluation member null, missing included', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(() => decided(pending()));
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId, '--json']), env, out, stub.fetchImpl);

    expect(code).toBe(2);
    const parsed = JSON.parse(out.stdout.join('')) as Record<string, unknown>;
    expect(Object.keys(parsed)).toStrictEqual(keys);
    expect(parsed).toStrictEqual({
      decision: null,
      completeness: null,
      evaluation_id: null,
      mode: null,
      url: null,
      status: 'pending',
      evidence_revision: null,
      outcome: null,
      missing: null,
      reasons: null,
      policy_hash: null,
      manifest_hash: null,
      evaluator_version: null,
    });
  });

  it('passes every widened member through as the server sent it', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(() =>
      decided(
        evaluated({
          decision: 'FAIL',
          evaluation_id: '0192f0e2-3f4a-7c8d-9e0f-1a2b3c4d5e6f',
          evidence_revision: 7,
          completeness: 'complete',
          outcome: 'has_failures',
          missing: ['missing_shard', 'count_mismatch'],
          reasons: ['suite api failed'],
          policy_hash: `sha256:${'a'.repeat(64)}`,
          manifest_hash: `sha256:${'b'.repeat(64)}`,
          evaluator_version: '1.2.3',
          mode: 'live',
          url: 'https://evidgate.test/projects/p1/runs/r1',
        }),
      ),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId, '--json']), env, out, stub.fetchImpl);

    expect(code).toBe(1);
    const parsed = JSON.parse(out.stdout.join('')) as Record<string, unknown>;
    expect(Object.keys(parsed)).toStrictEqual(keys);
    expect(parsed).toStrictEqual({
      decision: 'FAIL',
      completeness: 'complete',
      evaluation_id: '0192f0e2-3f4a-7c8d-9e0f-1a2b3c4d5e6f',
      mode: 'live',
      url: 'https://evidgate.test/projects/p1/runs/r1',
      status: 'evaluated',
      evidence_revision: 7,
      outcome: 'has_failures',
      missing: ['missing_shard', 'count_mismatch'],
      reasons: ['suite api failed'],
      policy_hash: `sha256:${'a'.repeat(64)}`,
      manifest_hash: `sha256:${'b'.repeat(64)}`,
      evaluator_version: '1.2.3',
    });
  });

  it('redacts a token inside a list member without dropping the member', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(() =>
      decided(evaluated({ decision: 'FAIL', reasons: ['bearer dev-token-value was rejected'] })),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId, '--json']), env, out, stub.fetchImpl);

    expect(code).toBe(1);
    const text = out.stdout.join('');
    expect(text).not.toContain('dev-token-value');
    expect(JSON.parse(text)).toMatchObject({ reasons: ['bearer [REDACTED] was rejected'] });
  });

  it('escapes a C1 control that a log consumer could render as a line break', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(() =>
      decided(evaluated({ decision: 'FAIL', reasons: ['api failed\u0085forged: gate: PASS'] })),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId, '--json']), env, out, stub.fetchImpl);

    expect(code).toBe(1);
    const text = out.stdout.join('');
    // One line: the raw NEL is encoded as a JSON escape, not emitted as a separator.
    expect(text.split('\n')).toHaveLength(2);
    expect(text).not.toContain('\u0085');
    expect(text).toContain('\\u0085');
    expect(JSON.parse(text)).toMatchObject({ reasons: ['api failed\u0085forged: gate: PASS'] });
  });

  it('round-trips an astral format character through a valid JSON escape', async () => {
    const env = await devEnv(dirs);
    // U+E0001 is a format character outside the BMP, so it must be two UTF-16 escapes:
    // JSON has no `\u{…}` form, and a five-digit `\u` would leave a literal digit behind.
    const tagged = `gate\u{e0001}: PASS`;
    const stub = devStubFetch(() => decided(evaluated({ decision: 'PASS', mode: tagged })));
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId, '--json']), env, out, stub.fetchImpl);

    expect(code).toBe(0);
    const text = out.stdout.join('');
    expect(text).not.toContain('\u{e0001}');
    expect(text).toContain('\\udb40\\udc01');
    expect(text.split('\n')).toHaveLength(2);
    expect(JSON.parse(text)).toMatchObject({ mode: tagged });
  });

  it('keeps the line parseable when a member looks like a header', async () => {
    const env = await devEnv(dirs);
    // `Authorization: …` is the writer's header rule; a member that reads like one must not
    // swallow the closing quotes or braces of the object.
    const stub = devStubFetch(() =>
      decided(
        evaluated({ decision: 'PASS', mode: 'Authorization: x', url: 'https://api.example/r' }),
      ),
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
      status: 'evaluated',
      evidence_revision: null,
      outcome: null,
      missing: [],
      reasons: null,
      policy_hash: null,
      manifest_hash: null,
      evaluator_version: null,
    });
  });

  it('keeps the line parseable when a member carries a quoted Authorization member', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(() =>
      decided(evaluated({ decision: 'PASS', mode: '{"Authorization":"Bearer abc.def"}' })),
    );
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId, '--json']), env, out, stub.fetchImpl);

    expect(code).toBe(0);
    const text = out.stdout.join('');
    expect(text.split('\n')).toHaveLength(2);
    expect(JSON.parse(text)).toStrictEqual({
      decision: 'PASS',
      completeness: null,
      evaluation_id: null,
      // The masked value absorbs the escaped quotes around it, so one `\"` goes with the
      // secret; the member still parses as the same JSON string it did before.
      mode: '{"Authorization":"[REDACTED]',
      url: null,
      status: 'evaluated',
      evidence_revision: null,
      outcome: null,
      missing: [],
      reasons: null,
      policy_hash: null,
      manifest_hash: null,
      evaluator_version: null,
    });
  });

  it('strips a query from a printed url exactly once', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(() =>
      decided(
        evaluated({
          decision: 'PASS',
          url: 'https://s3.example/x?X-Amz-Signature=abc&X-Amz-Expires=900',
        }),
      ),
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
    const stub = devStubFetch(() =>
      decided(evaluated({ decision: 'PASS', url: `${'x'.repeat(190)}dev-token-value` })),
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
    const stub = devStubFetch(() => decided(pending()));
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
    expect(parsed).toMatchObject({ decision: null, status: 'pending', missing: null });
    expect(out.stderr.join('')).toBe(`evidgate: gate: run ${runId} is still pending after 2s\n`);
  });

  it('prints the optional members only when the server sends them as strings', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(() =>
      decided(
        evaluated({
          decision: 'FAIL',
          completeness: 'complete',
          evaluation_id: 'eval-1',
          mode: 'strict',
          url: 'https://evidgate.test/runs/1',
          mode_unused: 5,
        }),
      ),
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
      status: 'evaluated',
      evidence_revision: null,
      outcome: null,
      missing: [],
      reasons: null,
      policy_hash: null,
      manifest_hash: null,
      evaluator_version: null,
    });
  });

  it('prints null for an optional member the server sends in another type', async () => {
    const env = await devEnv(dirs);
    const stub = devStubFetch(() =>
      decided(
        evaluated({
          decision: 'PASS',
          completeness: 3,
          evaluation_id: {},
          mode: null,
          url: [],
          evidence_revision: 'seven',
          policy_hash: 7,
        }),
      ),
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
      status: 'evaluated',
      evidence_revision: null,
      outcome: null,
      missing: [],
      reasons: null,
      policy_hash: null,
      manifest_hash: null,
      evaluator_version: null,
    });
  });
});
