// `--release`, `--idempotency-key` and `--manifest-id` boundaries, and the single-use rule
// that refuses a repeated flag as `usage` before any token work (contracts/cli.md §9,
// openapi `CreateRunRequest`).
import { describe, expect, it } from 'vitest';

import {
  apiCalls,
  astral,
  capture,
  createArgv,
  created,
  invoke,
  manifestHash,
  manifestId,
  otherManifestId,
  runsCreateArgv,
  sentBody,
  stubDevFetch,
  stubFetch,
  useRunsCreateQaSpec,
} from './runsCreate.qa.testing.js';

describe('runs create — QA boundaries', () => {
  const { devEnv, ingestEnv } = useRunsCreateQaSpec();

  // openapi `CreateRunRequest.release` is 1..128 characters; the CLI counts code points
  // (the schema's `maxLength` unit), so 128 astral characters are inside the limit even
  // though they are 256 UTF-16 units.
  it('release_boundaries_count_code_points', async () => {
    const env = await devEnv();

    const accepted: ReadonlyArray<string> = [
      'r'.repeat(128),
      `   ${'r'.repeat(128)}   `,
      astral.repeat(128),
    ];
    for (const raw of accepted) {
      const stub = stubDevFetch(() => created());
      await expect(
        invoke(createArgv(['--release', raw]), env, capture(), stub.fetchImpl),
      ).resolves.toBe(0);
      expect(sentBody(apiCalls(stub)[0]).release).toBe(raw.trim());
    }

    const rejected = ['r'.repeat(129), astral.repeat(129), '\t', ' \t '];
    for (const raw of rejected) {
      const stub = stubFetch(() => created());
      await expect(
        invoke(createArgv(['--release', raw]), await ingestEnv(), capture(), stub.fetchImpl),
        JSON.stringify(raw.slice(0, 8)),
      ).resolves.toBe(3);
      expect(stub.calls).toHaveLength(0);
    }
  });

  // contracts/cli.md §9: `--idempotency-key` is 1..128 printable ASCII (0x21–0x7E); the
  // endpoints of that range are inclusive and the delimiters (0x20 space, 0x7F DEL) are not.
  it('idempotency_key_printable_ascii_boundaries', async () => {
    const env = await devEnv();

    for (const key of ['~', '~'.repeat(128), 'A'.repeat(128), '!']) {
      const stub = stubDevFetch(() => created());
      await expect(
        invoke(createArgv(['--idempotency-key', key]), env, capture(), stub.fetchImpl),
      ).resolves.toBe(0);
      expect(apiCalls(stub)[0]?.request.headers.get('idempotency-key')).toBe(key);
    }

    for (const key of ['\u007f', ' '.repeat(8), 'kéy', '~'.repeat(129), '\u001f']) {
      const stub = stubFetch(() => created());
      await expect(
        invoke(
          createArgv(['--idempotency-key', key]),
          await ingestEnv(),
          capture(),
          stub.fetchImpl,
        ),
        JSON.stringify(key.slice(0, 8)),
      ).resolves.toBe(3);
      expect(stub.calls).toHaveLength(0);
    }
  });

  // contracts/cli.md §9 step 1: every option is single-use. `multiple: true` makes the
  // repetition visible to the reader, which refuses it as `usage` before any token work
  // instead of silently sending the last value — the failure mode that would defeat the
  // `--idempotency-key` replay guard. The message names the flag only, never the value.
  it('repeated_flag_refused_before_any_request', async () => {
    const cases: ReadonlyArray<{ readonly flag: string; readonly argv: string[] }> = [
      {
        flag: 'manifest-id',
        argv: runsCreateArgv([
          '--manifest-id',
          manifestId,
          '--manifest-hash',
          manifestHash,
          '--manifest-id',
          otherManifestId,
        ]),
      },
      {
        flag: 'idempotency-key',
        argv: createArgv(['--idempotency-key', 'qa-first', '--idempotency-key', 'qa-second']),
      },
    ];

    for (const testCase of cases) {
      const stub = stubFetch(() => created());
      const out = capture();
      // An ingest source would exchange a token: the refusal must precede any request.
      await expect(
        invoke(testCase.argv, await ingestEnv(), out, stub.fetchImpl),
        testCase.flag,
      ).resolves.toBe(3);
      const text = out.stderr.join('');
      expect(text, testCase.flag).toContain(`--${testCase.flag} may be given only once`);
      expect(text, testCase.flag).toContain('evidgate: error usage: ');
      expect(text, testCase.flag).not.toContain('qa-second');
      expect(text, testCase.flag).not.toContain(otherManifestId);
      expect(out.stdout.join(''), testCase.flag).toBe('');
      expect(stub.calls, testCase.flag).toHaveLength(0);
    }
  });

  // openapi `CreateRunRequest.manifest_id` is `format: uuid`; the CLI's pattern is
  // case-insensitive, and the value the operator typed is what reaches the body.
  it('manifest_id_letter_case_is_accepted_and_sent_verbatim', async () => {
    const env = await devEnv();
    const upper = manifestId.toUpperCase();
    const stub = stubDevFetch(() => created());
    await expect(
      invoke(
        runsCreateArgv(['--manifest-id', upper, '--manifest-hash', manifestHash]),
        env,
        capture(),
        stub.fetchImpl,
      ),
    ).resolves.toBe(0);
    expect(sentBody(apiCalls(stub)[0]).manifest_id).toBe(upper);
  });
});
