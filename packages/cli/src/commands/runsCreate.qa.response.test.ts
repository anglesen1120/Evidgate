// What `runs create` accepts back and prints: the `state` pattern of a 201, the unknown
// members it ignores, and the `--json` mode that keeps stdout empty on failure
// (contracts/cli.md §5, §9, openapi `RunCreated`).
import { describe, expect, it } from 'vitest';

import {
  capture,
  createArgv,
  created,
  envelope,
  invoke,
  manifestHash,
  manifestId,
  runId,
  runsCreateArgv,
  stubDevFetch,
  stubFetch,
  useRunsCreateQaSpec,
} from './runsCreate.qa.testing.js';

describe('runs create — QA boundaries', () => {
  const { devEnv, ingestEnv } = useRunsCreateQaSpec();

  // §9 step 3: `state` must match `^[a-z][a-z0-9_]{0,31}$` — 32 characters is the last
  // accepted length; a 33-character or a hostile value is `request_failed` and nothing
  // from it is echoed (the failure path is pinned in `runsCreate.test.ts`).
  it('created_state_boundaries', async () => {
    const env = await devEnv();

    for (const state of ['a'.repeat(32), 'queued', 'a_1']) {
      const stub = stubDevFetch(() => created({ state }));
      const out = capture();
      await expect(invoke(createArgv(), env, out, stub.fetchImpl)).resolves.toBe(0);
      expect(out.stdout.join('')).toContain(`state=${state}`);
    }

    for (const state of ['a'.repeat(33), '1queued', 'queued\u001b[31m']) {
      const stub = stubDevFetch(() => created({ state }));
      const out = capture();
      const attempt = invoke(createArgv(), env, out, stub.fetchImpl);
      await expect(attempt, `state length ${state.length}`).resolves.toBe(3);
      const text = out.stderr.join('');
      expect(text).toContain('evidgate: error request_failed: ');
      expect(text).toContain('the run create response was not valid');
      expect(text).not.toContain('\\u001b');
      expect(text).not.toContain('a'.repeat(33));
      expect(out.stdout.join('')).toBe('');
    }
  });

  // AC5: `--json` is an output mode, never a partial write — a failure leaves stdout empty
  // whether the failure is a server envelope or a local usage error.
  it('json_keeps_stdout_empty_on_failure', async () => {
    const env = await devEnv();

    const conflict = stubDevFetch(() =>
      envelope(409, 'idempotency_conflict', 'the key was used with a different body'),
    );
    const conflictOut = capture();
    await expect(
      invoke(
        createArgv(['--json', '--idempotency-key', 'qa-key']),
        env,
        conflictOut,
        conflict.fetchImpl,
      ),
    ).resolves.toBe(3);
    expect(conflictOut.stdout.join('')).toBe('');
    expect(conflictOut.stderr.join('')).toContain('evidgate: error idempotency_conflict: ');

    const usage = stubFetch(() => created());
    const usageOut = capture();
    await expect(
      invoke(
        runsCreateArgv(['--manifest-id', manifestId, '--manifest-hash', 'nope', '--json']),
        await ingestEnv(),
        usageOut,
        usage.fetchImpl,
      ),
    ).resolves.toBe(3);
    expect(usageOut.stdout.join('')).toBe('');
    expect(usageOut.stderr.join('')).toContain('evidgate: error usage: ');
    expect(usage.calls).toHaveLength(0);
  });

  // §9 step 3 / §6: the CLI reads `run_id` and `state` and prints the state it was given
  // once that state matches §9's pattern; a 201 carrying extra unknown members is not a
  // failure (the canonical body already carries `unverified_ref`), and stdout holds
  // exactly the documented members.
  it('created_body_extra_members_are_ignored_and_state_is_printed_verbatim', async () => {
    const env = await devEnv();
    const stub = stubDevFetch(() =>
      created({ state: 'running', extra_field: 'x', nested: { a: 1 }, unverified_ref: true }),
    );
    const out = capture();
    await expect(
      invoke(createArgv(['--json', '--idempotency-key', 'qa-extra-key']), env, out, stub.fetchImpl),
    ).resolves.toBe(0);

    expect(out.stdout.join('')).toBe(
      `{"run_id":"${runId}","manifest_hash":"${manifestHash}","state":"running"}\n`,
    );
    expect(out.stderr.join('')).toBe('');
  });
});
