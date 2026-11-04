// How `runs create` reports a refusal: every documented error code with its `request_id`,
// the fail-closed fallback of an unusable envelope, and the escaping of hostile server text
// (contracts/cli.md §5, §9).
import { describe, expect, it } from 'vitest';

import {
  capture,
  createArgv,
  envelope,
  invoke,
  jsonResponse,
  rlo,
  stubDevFetch,
  useRunsCreateQaSpec,
} from './runsCreate.qa.testing.js';

describe('runs create — QA boundaries', () => {
  const { devEnv } = useRunsCreateQaSpec();

  // contracts/cli.md §5: a detail whose `reason` is not a string makes the whole envelope
  // unusable (fail closed) — the reply falls back to the command's `request_failed` code
  // with the `HTTP <status>` message and no `detail:` lines.
  it('non_string_detail_reason_falls_back_fail_closed', async () => {
    const env = await devEnv();
    const stub = stubDevFetch(() =>
      jsonResponse(422, {
        error: {
          code: 'manifest_invalid',
          message: 'the manifest did not resolve',
          request_id: 'req-fail-closed',
          retryable: false,
          details: [{ index: 0, path: '/manifest_id', reason: 5 }],
        },
      }),
    );
    const out = capture();
    await expect(
      invoke(createArgv(['--idempotency-key', 'qa-fail-closed']), env, out, stub.fetchImpl),
    ).resolves.toBe(3);

    const text = out.stderr.join('');
    expect(text).toContain('evidgate: error request_failed: the run create failed (HTTP 422)');
    expect(text).not.toContain('manifest_invalid');
    expect(text).not.toContain('evidgate: detail: ');
    expect(out.stdout.join('')).toBe('');
  });

  // §9 step 4: every documented non-2xx code reaches stderr as `evidgate: error <code>`
  // with its `request_id`, exits 3, and never writes stdout.
  it('documented_error_codes_pass_through', async () => {
    const env = await devEnv();
    const cases: ReadonlyArray<{ readonly status: number; readonly code: string }> = [
      { status: 401, code: 'token_revoked' },
      { status: 403, code: 'project_scope_mismatch' },
      { status: 404, code: 'not_found' },
      { status: 409, code: 'idempotency_conflict' },
      { status: 422, code: 'manifest_invalid' },
      { status: 422, code: 'release_unresolvable' },
      { status: 422, code: 'execution_key_conflict' },
      { status: 429, code: 'rate_limited' },
      { status: 503, code: 'service_unavailable' },
    ];

    for (const testCase of cases) {
      const stub = stubDevFetch(() => envelope(testCase.status, testCase.code, 'refused'));
      const out = capture();
      await expect(
        invoke(createArgv(['--idempotency-key', 'qa-code-key']), env, out, stub.fetchImpl),
        testCase.code,
      ).resolves.toBe(3);
      const text = out.stderr.join('');
      expect(text, testCase.code).toContain(`evidgate: error ${testCase.code}: `);
      expect(text, testCase.code).toContain(`evidgate: request_id: req-${testCase.code}`);
      expect(out.stdout.join(''), testCase.code).toBe('');
    }

    // A 422 with `details[]` becomes one `detail:` line per entry (each field redacted and
    // escaped), and nothing else of the body is echoed.
    const detailsStub = stubDevFetch(() =>
      envelope(422, 'manifest_invalid', 'the manifest did not resolve', [
        { index: 0, path: '/manifest_id', reason: 'invalid_value' },
      ]),
    );
    const detailsOut = capture();
    await expect(
      invoke(
        createArgv(['--idempotency-key', 'qa-detail-key']),
        env,
        detailsOut,
        detailsStub.fetchImpl,
      ),
    ).resolves.toBe(3);
    expect(detailsOut.stderr.join('')).toContain(
      'evidgate: detail: index=0 path=/manifest_id reason=invalid_value',
    );
  });

  // §8 / contracts/cli.md §5: a hostile server message, `request_id` or detail field is
  // escaped to `\uXXXX`, so no control, format, line- or paragraph-separator character can
  // forge a stderr line or a GitHub `::` workflow command.
  it('hostile_server_text_is_escaped', async () => {
    const env = await devEnv();
    const stub = stubDevFetch(() =>
      jsonResponse(409, {
        error: {
          code: 'idempotency_conflict',
          message: `bad${'\u0007'}thing\u000aline${rlo}tail`,
          request_id: `req${rlo}1`,
          retryable: false,
          details: [{ path: `/${rlo}manifest`, reason: `bad${'\u0007'}reason` }],
        },
      }),
    );
    const out = capture();
    await expect(
      invoke(createArgv(['--idempotency-key', 'qa-hostile-key']), env, out, stub.fetchImpl),
    ).resolves.toBe(3);

    const text = out.stderr.join('');
    expect(text).toContain('evidgate: error idempotency_conflict: ');
    expect(text).toContain('\\u0007');
    expect(text).toContain('\\u000a');
    expect(text).toContain('\\u202e');
    // Nothing raw survives: no alarm, no RLO, and every line still starts with the prefix.
    expect(/[\u0000-\u0008\u000b-\u001f\u2028\u2029\u202e]/u.test(text)).toBe(false);
    expect(text.endsWith('\n')).toBe(true);
    for (const line of text.split('\n').slice(0, -1)) {
      expect(line.startsWith('evidgate: ')).toBe(true);
    }
    expect(out.stdout.join('')).toBe('');
  });
});
