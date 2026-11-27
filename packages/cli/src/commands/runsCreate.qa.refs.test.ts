// `--ci-ref` boundaries: the `refs/heads`, `refs/tags` and `refs/pull/<N>/merge` forms the
// command accepts, the refs it refuses as `usage` before any request, and the lengths the
// schema measures in code points (contracts/cli.md §9, openapi `RunRefRequest`).
import { describe, expect, it } from 'vitest';

import {
  apiCalls,
  astral,
  capture,
  createArgv,
  created,
  invoke,
  sentBody,
  stubDevFetch,
  stubFetch,
  useRunsCreateQaSpec,
} from './runsCreate.qa.testing.js';

describe('runs create — QA boundaries', () => {
  const { devEnv, ingestEnv } = useRunsCreateQaSpec();

  // The CI's OIDC request variables are no credential source: the exchange is the only
  // credential request, it goes to the API alone, and the declared ref still travels in the body.
  it('sends_no_oidc_request_and_still_declares_the_ref', async () => {
    const env = await ingestEnv();
    const actionsUrl = 'https://actions.example.test/_apis/oidc?audience=evidgate';
    const stub = stubDevFetch(() => created());
    const code = await invoke(
      createArgv(['--ci-ref', 'refs/heads/main']),
      {
        ...env,
        ACTIONS_ID_TOKEN_REQUEST_URL: actionsUrl,
        ACTIONS_ID_TOKEN_REQUEST_TOKEN: 'actions-request-token',
      },
      capture(),
      stub.fetchImpl,
    );
    expect(code).toBe(0);
    expect(stub.calls.length).toBeGreaterThan(0);
    for (const call of stub.calls) {
      expect(new URL(call.request.url).origin).toBe('https://api.evidgate.test');
      expect(call.request.headers.get('authorization') ?? '').not.toContain(
        'actions-request-token',
      );
    }
    expect(sentBody(apiCalls(stub)[0]).ref).toEqual({ kind: 'branch', name: 'main' });
  });

  // §9 / openapi `RunRefRequest`: the name boundary is 1..255, the PR form is
  // `refs/pull/<N>/merge` with a positive integer, and the CLI is a thin client (it does
  // not re-implement git's ref grammar), so a doubled slash is passed through.
  it('ci_ref_name_and_pr_boundaries', async () => {
    const env = await devEnv();
    const accepted: ReadonlyArray<{ readonly ciRef: string; readonly ref: unknown }> = [
      {
        ciRef: `refs/heads/${'a'.repeat(255)}`,
        ref: { kind: 'branch', name: 'a'.repeat(255) },
      },
      { ciRef: 'refs/heads/a//b', ref: { kind: 'branch', name: 'a//b' } },
      { ciRef: 'refs/heads/fiësta/ünïcode', ref: { kind: 'branch', name: 'fiësta/ünïcode' } },
      { ciRef: 'refs/tags/v0.0.1-rc.1', ref: { kind: 'tag', name: 'v0.0.1-rc.1' } },
      { ciRef: 'refs/pull/1/merge', ref: { kind: 'pull_request', name: '1/merge', pr_number: 1 } },
    ];
    for (const testCase of accepted) {
      const stub = stubDevFetch(() => created());
      await expect(
        invoke(createArgv(['--ci-ref', testCase.ciRef]), env, capture(), stub.fetchImpl),
      ).resolves.toBe(0);
      expect(sentBody(apiCalls(stub)[0]).ref).toEqual(testCase.ref);
    }

    const rejected = [
      `refs/heads/${'a'.repeat(256)}`,
      'refs/pull/042/merge',
      'refs/pull/1',
      'refs/pull//merge',
      'refs/pull/1/merge ',
      'refs/pull/abc/merge',
      'refs/pull/-1/merge',
      'refs/pull/1e3/merge',
      'refs/pull/1.5/merge',
      'refs/heads',
      ' refs/heads/main',
      'refs/heads/main\n',
      'refs/tags',
    ];
    for (const ciRef of rejected) {
      const stub = stubFetch(() => created());
      const attempt = invoke(
        createArgv(['--ci-ref', ciRef]),
        await ingestEnv(),
        capture(),
        stub.fetchImpl,
      );
      await expect(attempt, ciRef).resolves.toBe(3);
      expect(stub.calls, ciRef).toHaveLength(0);
    }
  });

  // §9: `N` in `refs/pull/<N>/merge` must be a safe integer ≥ 1 and `name` is built from
  // its digits, and the ref name is counted in code points — the schema's unit — so 255
  // astral characters are inside 1..255 though they are 510 UTF-16 units.
  it('ci_ref_safe_pr_number_and_code_point_name', async () => {
    const env = await devEnv();

    const maxSafe = '9007199254740991';
    const accepted: ReadonlyArray<{ readonly ciRef: string; readonly ref: unknown }> = [
      {
        ciRef: `refs/pull/${maxSafe}/merge`,
        ref: { kind: 'pull_request', name: `${maxSafe}/merge`, pr_number: Number(maxSafe) },
      },
      {
        ciRef: `refs/heads/${astral.repeat(255)}`,
        ref: { kind: 'branch', name: astral.repeat(255) },
      },
    ];
    for (const testCase of accepted) {
      const stub = stubDevFetch(() => created());
      await expect(
        invoke(createArgv(['--ci-ref', testCase.ciRef]), env, capture(), stub.fetchImpl),
      ).resolves.toBe(0);
      expect(sentBody(apiCalls(stub)[0]).ref).toEqual(testCase.ref);
    }

    const rejected = [
      `refs/pull/${Number(maxSafe) + 1}/merge`,
      `refs/pull/${'9'.repeat(400)}/merge`,
      `refs/heads/${astral.repeat(256)}`,
    ];
    for (const ciRef of rejected) {
      const label = ciRef.slice(0, 24);
      const stub = stubFetch(() => created());
      await expect(
        invoke(createArgv(['--ci-ref', ciRef]), await ingestEnv(), capture(), stub.fetchImpl),
        label,
      ).resolves.toBe(3);
      expect(stub.calls, label).toHaveLength(0);
    }
  });
});
