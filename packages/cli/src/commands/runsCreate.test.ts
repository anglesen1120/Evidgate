// `evidgate runs create` on the binary: the `Idempotency-Key` header, the flag → body mapping
// and the exact output line (text and `--json`) the command prints (contracts/cli.md
// §5/§6/§7/§9).
import { describe, expect, it } from 'vitest';

import { mintedToken } from './devExchange.testing.js';
import {
  manifestHash,
  manifestId,
  project,
  runId,
  apiCalls,
  capture,
  createArgv,
  created,
  invoke,
  sentBody,
  stubDevFetch,
  useRunsCreateSpec,
} from './runsCreate.testing.js';

describe('runs create', () => {
  const { devEnv } = useRunsCreateSpec();

  // AC2: the `Idempotency-Key` header is sent, the body carries only
  // `manifest_id`, and `run_id`/`manifest_hash`/`state` are printed.
  it('prints_run_id', async () => {
    const env = await devEnv();
    const stub = stubDevFetch(() => created({ unverified_ref: true }));
    const out = capture();

    const argv = createArgv(['--idempotency-key', 'explicit-key-1']);
    await expect(invoke(argv, env, out, stub.fetchImpl)).resolves.toBe(0);

    expect(out.stdout.join('')).toBe(
      `run_id=${runId} manifest_hash=${manifestHash} state=queued\n`,
    );
    expect(out.stderr.join('')).toBe('');
    const calls = apiCalls(stub);
    expect(calls).toHaveLength(1);
    const call = calls[0];
    expect(call?.request.method).toBe('POST');
    // `createApiClient` puts the SDK's `/v1` server prefix on the base URL, so the
    // operation path is `<root>/v1/projects/{p}/runs` (the env value carries no `/v1`).
    expect(new URL(call?.request.url ?? '').pathname).toBe(`/v1/projects/${project}/runs`);
    // The command runs on the exchanged token: the dev token only travels in the exchange
    // body, so a regression that sent it as the bearer fails here.
    expect(call?.request.headers.get('authorization')).toBe(`Bearer ${mintedToken}`);
    expect(call?.request.headers.get('content-type')).toBe('application/json');
    // 00 §9.1: the route requires an `Idempotency-Key`; an explicit one is sent verbatim.
    expect(call?.request.headers.get('idempotency-key')).toBe('explicit-key-1');
    // `CreateRunRequest` has `additionalProperties: false`: only the members the CLI owns.
    expect(sentBody(call)).toEqual({ manifest_id: manifestId });
  });

  // AC5: `--release` and every `--ci-ref` form map to the documented body
  // members, and `--json` prints exactly one line with `run_id`, `manifest_hash`, `state`.
  it('flags_and_json_output', async () => {
    const env = await devEnv();
    const cases: ReadonlyArray<{ readonly ciRef: string; readonly ref: unknown }> = [
      { ciRef: 'refs/heads/main', ref: { kind: 'branch', name: 'main' } },
      { ciRef: 'refs/heads/release/1.2', ref: { kind: 'branch', name: 'release/1.2' } },
      { ciRef: 'refs/tags/v1.2.3', ref: { kind: 'tag', name: 'v1.2.3' } },
      {
        ciRef: 'refs/pull/42/merge',
        ref: { kind: 'pull_request', name: '42/merge', pr_number: 42 },
      },
    ];

    for (const testCase of cases) {
      const stub = stubDevFetch(() => created({ unverified_ref: true }));
      const out = capture();
      const flags = ['--release', 'rel-1', '--ci-ref', testCase.ciRef, '--idempotency-key', 'k'];
      await expect(invoke(createArgv(flags), env, out, stub.fetchImpl)).resolves.toBe(0);
      expect(out.stderr.join('')).toBe('');
      // Only the members the CLI owns: no head_sha/base_sha/trigger_event, and no policy,
      // suite or environment (the server pins those).
      expect(sentBody(apiCalls(stub)[0])).toEqual({
        manifest_id: manifestId,
        release: 'rel-1',
        ref: testCase.ref,
      });
    }

    // `--release` is trimmed before it is sent (and before it feeds the key).
    const trimStub = stubDevFetch(() => created());
    const trimArgv = createArgv(['--release', '  rel-1  ', '--idempotency-key', 'k']);
    await expect(invoke(trimArgv, env, capture(), trimStub.fetchImpl)).resolves.toBe(0);
    expect(sentBody(apiCalls(trimStub)[0])).toEqual({ manifest_id: manifestId, release: 'rel-1' });

    // `--json`: one line, three members in the documented order, and nothing else.
    const jsonStub = stubDevFetch(() => created());
    const jsonOut = capture();
    await expect(invoke(createArgv(['--json']), env, jsonOut, jsonStub.fetchImpl)).resolves.toBe(0);
    const expected = `{"run_id":"${runId}","manifest_hash":"${manifestHash}","state":"queued"}`;
    expect(jsonOut.stdout.join('')).toBe(`${expected}\n`);
    expect(jsonOut.stderr.join('')).toBe('');
    expect(jsonOut.stdout.join('')).not.toContain('unverified_ref');
  });
});
