// `evidgate runs create` flag validation: every refused value is a `usage` error raised before
// any token work, and every option is single-use (contracts/cli.md §8/§9).
import { describe, expect, it } from 'vitest';

import {
  manifestHash,
  manifestId,
  shortHash,
  upperHash,
  capture,
  createArgv,
  created,
  invoke,
  runsCreateArgv,
  stubFetch,
  useRunsCreateSpec,
} from './runsCreate.testing.js';

describe('runs create', () => {
  const { ingestEnv } = useRunsCreateSpec();

  // §8: every flag failure is a `usage` error raised before any token work,
  // so an ingest source (which would exchange over the network) makes no call at all.
  it('usage_errors_no_request', async () => {
    const cases: ReadonlyArray<{ readonly label: string; readonly argv: string[] }> = [
      {
        label: '--manifest-id absent',
        argv: runsCreateArgv(['--manifest-hash', manifestHash]),
      },
      {
        label: '--manifest-id not a UUID',
        argv: runsCreateArgv(['--manifest-id', 'nope', '--manifest-hash', manifestHash]),
      },
      {
        label: '--manifest-hash absent',
        argv: runsCreateArgv(['--manifest-id', manifestId]),
      },
      {
        label: '--manifest-hash without the prefix',
        argv: runsCreateArgv(['--manifest-id', manifestId, '--manifest-hash', 'a'.repeat(64)]),
      },
      {
        label: '--manifest-hash too short',
        argv: runsCreateArgv(['--manifest-id', manifestId, '--manifest-hash', shortHash]),
      },
      {
        label: '--manifest-hash uppercase',
        argv: runsCreateArgv(['--manifest-id', manifestId, '--manifest-hash', upperHash]),
      },
      { label: '--ci-ref unprefixed', argv: createArgv(['--ci-ref', 'main']) },
      { label: '--ci-ref empty', argv: createArgv(['--ci-ref', '']) },
      {
        label: '--ci-ref other namespace',
        argv: createArgv(['--ci-ref', 'refs/remotes/origin/main']),
      },
      { label: '--ci-ref empty branch name', argv: createArgv(['--ci-ref', 'refs/heads/']) },
      { label: '--ci-ref PR number zero', argv: createArgv(['--ci-ref', 'refs/pull/0/merge']) },
      { label: '--ci-ref PR wrong suffix', argv: createArgv(['--ci-ref', 'refs/pull/42/head']) },
      {
        label: '--ci-ref PR number past the safe integer range',
        argv: createArgv(['--ci-ref', 'refs/pull/9007199254740992/merge']),
      },
      {
        label: '--ci-ref PR number of 400 digits',
        argv: createArgv(['--ci-ref', `refs/pull/${'9'.repeat(400)}/merge`]),
      },
      {
        label: '--ci-ref name over 255 chars',
        argv: createArgv(['--ci-ref', `refs/heads/${'a'.repeat(256)}`]),
      },
      { label: '--idempotency-key empty', argv: createArgv(['--idempotency-key', '']) },
      {
        label: '--idempotency-key over 128 chars',
        argv: createArgv(['--idempotency-key', 'k'.repeat(129)]),
      },
      {
        label: '--idempotency-key with a space',
        argv: createArgv(['--idempotency-key', 'key with space']),
      },
      { label: '--idempotency-key with a tab', argv: createArgv(['--idempotency-key', 'key\tv']) },
      { label: '--release empty after trim', argv: createArgv(['--release', '   ']) },
      { label: '--release over 128 chars', argv: createArgv(['--release', 'r'.repeat(129)]) },
    ];

    for (const testCase of cases) {
      const env = await ingestEnv();
      const stub = stubFetch(() => created());
      const out = capture();
      const attempt = invoke(testCase.argv, env, out, stub.fetchImpl);
      await expect(attempt, testCase.label).resolves.toBe(3);
      const text = out.stderr.join('');
      expect(text, testCase.label).toContain('evidgate: error usage: ');
      expect(text, testCase.label).toContain("evidgate: hint: run 'evidgate --help' for usage");
      expect(out.stdout.join(''), testCase.label).toBe('');
      expect(stub.calls, testCase.label).toHaveLength(0);
    }
  });

  // contracts/cli.md §9 step 1: every option is single-use. `multiple: true`
  // makes a repetition visible to the reader, which refuses it instead of silently sending
  // the last value — the failure mode that would defeat the `--idempotency-key` replay
  // guard. The message names the flag only, never the value, and no request is made.
  it('repeated_flag_refused', async () => {
    const cases: ReadonlyArray<{ readonly flag: string; readonly argv: string[] }> = [
      {
        flag: 'manifest-id',
        argv: runsCreateArgv([
          '--manifest-id',
          manifestId,
          '--manifest-hash',
          manifestHash,
          '--manifest-id',
          'zz-other-id',
        ]),
      },
      {
        flag: 'manifest-hash',
        argv: runsCreateArgv([
          '--manifest-id',
          manifestId,
          '--manifest-hash',
          manifestHash,
          '--manifest-hash',
          'zz-other-hash',
        ]),
      },
      { flag: 'release', argv: createArgv(['--release', 'zz-one', '--release', 'zz-two']) },
      {
        flag: 'ci-ref',
        argv: createArgv(['--ci-ref', 'refs/heads/zz-main', '--ci-ref', 'refs/tags/zz-v1']),
      },
      {
        flag: 'idempotency-key',
        argv: createArgv(['--idempotency-key', 'zz-first', '--idempotency-key', 'zz-second']),
      },
    ];

    for (const testCase of cases) {
      const stub = stubFetch(() => created());
      const out = capture();
      // An ingest source would exchange a token over the network: the refusal is local.
      await expect(
        invoke(testCase.argv, await ingestEnv(), out, stub.fetchImpl),
        testCase.flag,
      ).resolves.toBe(3);
      const text = out.stderr.join('');
      expect(text, testCase.flag).toContain(
        `evidgate: error usage: --${testCase.flag} may be given only once`,
      );
      expect(text, testCase.flag).toContain("evidgate: hint: run 'evidgate --help' for usage");
      expect(text, testCase.flag).not.toContain('zz-');
      expect(out.stdout.join(''), testCase.flag).toBe('');
      expect(stub.calls, testCase.flag).toHaveLength(0);
    }
  });
});
