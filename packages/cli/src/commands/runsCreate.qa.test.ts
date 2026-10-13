// 04.2.2-T2 QA (S47): exploratory, boundary and negative specs for `runs create`,
// written by an agent that did not write the command. They pin only behaviour that is
// intended by the plan (04.2.2 §2 AC2–AC5, §5, §8, §9), `contracts/cli.md` §9 and
// `contracts/openapi.yaml` (`CreateRunRequest`, `RunCreated`, `RunRefRequest`,
// `IdempotencyKey`): the ref/release/key/flag boundaries, the unambiguity of the AC4
// derived key, the closed request body, the pass-through of every documented error
// code, and the escaping of a hostile server message. Harness style follows
// `runsCreate.test.ts` (injected `fetch`, no new dependency).
//
// The two behaviours the QA report raised as findings are fixed in S47 and pinned below:
// `refs/pull/<N>/merge` holds `N` to a safe integer and builds `name` from its digits,
// and the `--ci-ref` name is measured in code points (both `contracts/cli.md` §9), as is
// the 201 `state` pattern.
import { createHash } from 'node:crypto';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { main } from '../main.js';
import { resetSecrets, type Io } from '../redact.js';

import { deriveRunIdempotencyKey, type RunIdempotencyKeyInput } from './runsCreate.js';

/** The project slug the command scopes the create to. */
const project = 'proj-1';
/** A valid `manifest_id` (openapi `format: uuid`). */
const manifestId = '9b2f4c1e-2a3b-4c5d-8e9f-0a1b2c3d4e5f';
/** A second UUID, for the flag-conflict cases. */
const otherManifestId = '1c2d3e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f';
/** A valid `manifest_hash`; assembled so no digest literal is stored. */
const manifestHash = `sha256:${'a'.repeat(64)}`;
/** A second digest for the derived-key difference checks. */
const otherManifestHash = `sha256:${'b'.repeat(64)}`;
/** The `run_id` the server returns (openapi `RunCreated`). */
const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
/** A single astral code point (two UTF-16 units) for the length-counting cases. */
const astral = '\u{1F600}';
/** U+202E RIGHT-TO-LEFT OVERRIDE, a `Cf` character the escaper must neutralise. */
const rlo = '\u202e';

interface Captured {
  readonly io: Io;
  readonly stdout: string[];
  readonly stderr: string[];
}

function capture(): Captured {
  const stdout: string[] = [];
  const stderr: string[] = [];
  return {
    io: { stdout: (text) => stdout.push(text), stderr: (text) => stderr.push(text) },
    stdout,
    stderr,
  };
}

interface StubCall {
  readonly request: Request;
  readonly body: Buffer;
}

/** A `fetch` stub that records every request (URL, headers, raw body) and answers from `handler`. */
function stubFetch(handler: (request: Request) => Response | Promise<Response>): {
  readonly fetchImpl: typeof fetch;
  readonly calls: StubCall[];
} {
  const calls: StubCall[] = [];
  const fetchImpl: typeof fetch = async (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    calls.push({ request, body: Buffer.from(await request.clone().arrayBuffer()) });
    return handler(request);
  };
  return { fetchImpl, calls };
}

/** `main()` with the injected `fetch` stub — the only transport these specs use. */
function invoke(
  argv: ReadonlyArray<string>,
  env: Readonly<Record<string, string | undefined>>,
  out: Captured,
  fetchImpl: typeof fetch,
): Promise<number> {
  return main(argv, env, out.io, { fetch: fetchImpl });
}

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

/** The canonical 201 body (openapi `RunCreated`), with per-case overrides. */
function created(overrides: Record<string, unknown> = {}): Response {
  return jsonResponse(201, { run_id: runId, state: 'queued', unverified_ref: false, ...overrides });
}

/** A 00 §9.2 error envelope with the given code (and optional details). */
function envelope(status: number, code: string, message: string, details?: unknown): Response {
  return jsonResponse(status, {
    error: {
      code,
      message,
      request_id: `req-${code}`,
      retryable: false,
      ...(details === undefined ? {} : { details }),
    },
  });
}

/** The JSON body the stub recorded for `call`. */
function sentBody(call: StubCall | undefined): Record<string, unknown> {
  return JSON.parse(call?.body.toString('utf8') ?? '') as Record<string, unknown>;
}

/** A bare `runs create` argv, for the cases that replace a required flag. */
function runsCreateArgv(flags: ReadonlyArray<string>): string[] {
  return ['runs', 'create', ...flags];
}

/** `runs create` with both required flags, plus `extra` (the common case). */
function createArgv(extra: ReadonlyArray<string> = []): string[] {
  return runsCreateArgv(['--manifest-id', manifestId, '--manifest-hash', manifestHash, ...extra]);
}

describe('runs create — QA boundaries', () => {
  const dirs: string[] = [];

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  /** The M1 dev path: no exchange, no cache, no token network round trip. */
  async function devEnv(): Promise<Record<string, string>> {
    const cacheDir = await mkdtemp(join(tmpdir(), 'evidgate-cli-qa-'));
    dirs.push(cacheDir);
    return {
      EVIDGATE_API_URL: 'http://127.0.0.1:8080',
      EVIDGATE_PROJECT: project,
      EVIDGATE_ENV: 'dev',
      EVIDGATE_DEV_TOKEN: 'qa-dev-token',
      EVIDGATE_CACHE_DIR: cacheDir,
    };
  }

  /** An ingest source: a cold cache would exchange at `/v1/ci/token`, so any call is visible. */
  async function ingestEnv(): Promise<Record<string, string>> {
    const cacheDir = await mkdtemp(join(tmpdir(), 'evidgate-cli-qa-'));
    dirs.push(cacheDir);
    return {
      EVIDGATE_API_URL: 'https://api.evidgate.test',
      EVIDGATE_PROJECT: project,
      EVIDGATE_CACHE_DIR: cacheDir,
      EVIDGATE_INGEST_TOKEN: 'qa-ingest-token',
    };
  }

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
      const stub = stubFetch(() => created());
      await expect(
        invoke(createArgv(['--ci-ref', testCase.ciRef]), env, capture(), stub.fetchImpl),
      ).resolves.toBe(0);
      expect(sentBody(stub.calls[0]).ref).toEqual(testCase.ref);
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

  // S47 findings, now fixed (§9): `N` in `refs/pull/<N>/merge` must be a safe integer ≥ 1
  // and `name` is built from its digits, and the ref name is counted in code points — the
  // schema's unit — so 255 astral characters are inside 1..255 though they are 510 UTF-16
  // units.
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
      const stub = stubFetch(() => created());
      await expect(
        invoke(createArgv(['--ci-ref', testCase.ciRef]), env, capture(), stub.fetchImpl),
      ).resolves.toBe(0);
      expect(sentBody(stub.calls[0]).ref).toEqual(testCase.ref);
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

  // §9 step 3: `state` must match `^[a-z][a-z0-9_]{0,31}$` — 32 characters is the last
  // accepted length; a 33-character or a hostile value is `request_failed` and nothing
  // from it is echoed (the failure path is pinned in `runsCreate.test.ts`).
  it('created_state_boundaries', async () => {
    const env = await devEnv();

    for (const state of ['a'.repeat(32), 'queued', 'a_1']) {
      const stub = stubFetch(() => created({ state }));
      const out = capture();
      await expect(invoke(createArgv(), env, out, stub.fetchImpl)).resolves.toBe(0);
      expect(out.stdout.join('')).toContain(`state=${state}`);
    }

    for (const state of ['a'.repeat(33), '1queued', 'queued\u001b[31m']) {
      const stub = stubFetch(() => created({ state }));
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
      const stub = stubFetch(() => created());
      await expect(
        invoke(createArgv(['--release', raw]), env, capture(), stub.fetchImpl),
      ).resolves.toBe(0);
      expect(sentBody(stub.calls[0]).release).toBe(raw.trim());
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
      const stub = stubFetch(() => created());
      await expect(
        invoke(createArgv(['--idempotency-key', key]), env, capture(), stub.fetchImpl),
      ).resolves.toBe(0);
      expect(stub.calls[0]?.request.headers.get('idempotency-key')).toBe(key);
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
    const stub = stubFetch(() => created());
    await expect(
      invoke(
        runsCreateArgv(['--manifest-id', upper, '--manifest-hash', manifestHash]),
        env,
        capture(),
        stub.fetchImpl,
      ),
    ).resolves.toBe(0);
    expect(sentBody(stub.calls[0]).manifest_id).toBe(upper);
  });

  // contracts/cli.md §5: a detail whose `reason` is not a string makes the whole envelope
  // unusable (fail closed) — the reply falls back to the command's `request_failed` code
  // with the `HTTP <status>` message and no `detail:` lines.
  it('non_string_detail_reason_falls_back_fail_closed', async () => {
    const env = await devEnv();
    const stub = stubFetch(() =>
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

  // AC5: `--json` is an output mode, never a partial write — a failure leaves stdout empty
  // whether the failure is a server envelope or a local usage error.
  it('json_keeps_stdout_empty_on_failure', async () => {
    const env = await devEnv();

    const conflict = stubFetch(() =>
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

  // AC4 (amended D-S47-Q1): the pre-image is a JSON array, so no separator or field split
  // can make two different intents collide. Every pair here would collide under a
  // concatenation with any single separator, and none of them collide under the array.
  it('derived_key_cannot_collide_across_a_field_split', () => {
    const pairs: ReadonlyArray<readonly [RunIdempotencyKeyInput, RunIdempotencyKeyInput]> = [
      [
        { manifestId, manifestHash, release: 'a', ciRef: 'b' },
        { manifestId, manifestHash, release: 'ab', ciRef: null },
      ],
      [
        { manifestId, manifestHash, release: null, ciRef: 'refs/heads/main' },
        { manifestId, manifestHash, release: 'refs/heads/main', ciRef: null },
      ],
      [
        { manifestId, manifestHash, release: 'a"', ciRef: 'b' },
        { manifestId, manifestHash, release: 'a', ciRef: '"b' },
      ],
      [
        { manifestId: 'a-b', manifestHash: 'c' },
        { manifestId: 'a', manifestHash: 'b-c' },
      ],
      [
        { manifestId, manifestHash, release: 'x', ciRef: 'y' },
        { manifestId: otherManifestId, manifestHash: otherManifestHash, release: 'x', ciRef: 'y' },
      ],
    ];

    const keys: string[] = [];
    for (const [left, right] of pairs) {
      const leftKey = deriveRunIdempotencyKey(left);
      const rightKey = deriveRunIdempotencyKey(right);
      expect(leftKey).not.toBe(rightKey);
      keys.push(leftKey, rightKey);
    }
    expect(new Set(keys).size).toBe(keys.length);
    for (const key of keys) {
      expect(key).toMatch(/^[0-9a-f]{64}$/);
      expect(key.length).toBeLessThanOrEqual(128);
    }
    // The derivation is pure: the same input object derives the same key on a re-call.
    expect(deriveRunIdempotencyKey({ manifestId, manifestHash, release: 'a', ciRef: 'b' })).toBe(
      createHash('sha256')
        .update(JSON.stringify(['evidgate.run-key.v1', manifestHash, manifestId, 'a', 'b']), 'utf8')
        .digest('hex'),
    );
  });

  // AC2/AC4: an explicit key is sent verbatim and is the only key on the header — the
  // derived value never also travels (they are different values here).
  it('explicit_key_overrides_the_derived_key', async () => {
    const env = await devEnv();
    const derived = deriveRunIdempotencyKey({ manifestId, manifestHash });
    const stub = stubFetch(() => created());
    const argv = createArgv(['--idempotency-key', 'qa-explicit-key']);
    await expect(invoke(argv, env, capture(), stub.fetchImpl)).resolves.toBe(0);

    const header = stub.calls[0]?.request.headers.get('idempotency-key');
    expect(header).toBe('qa-explicit-key');
    expect(header).not.toBe(derived);
    expect(stub.calls).toHaveLength(1);
  });

  // AC5 / openapi `CreateRunRequest` + `RunRefRequest` are closed shapes: the wire body
  // carries exactly the members the CLI owns, and the hash/policy/suite/env never travel.
  it('request_body_carries_only_documented_members', async () => {
    const env = await devEnv();
    const stub = stubFetch(() => created());
    const argv = createArgv([
      '--release',
      'rel/1.2',
      '--ci-ref',
      'refs/heads/main',
      '--idempotency-key',
      'qa-body-key',
    ]);
    await expect(invoke(argv, env, capture(), stub.fetchImpl)).resolves.toBe(0);

    const call = stub.calls[0];
    expect(Object.keys(sentBody(call)).sort()).toEqual(['manifest_id', 'ref', 'release']);
    const raw = call?.body.toString('utf8') ?? '';
    for (const forbidden of [
      'manifest_hash',
      'purpose',
      'components',
      'replay_grant_id',
      'policy',
      'suite',
      'env',
      'head_sha',
      'base_sha',
      'trigger_event',
      'unverified_ref',
    ]) {
      expect(raw, forbidden).not.toContain(forbidden);
    }
    // The hash travels nowhere: not the body, not a query string, not a header.
    expect(call?.request.url).not.toContain('sha256');
    for (const [, value] of call?.request.headers ?? []) {
      expect(value).not.toContain('sha256');
    }
  });

  // §9 step 3 / §6: the CLI reads `run_id` and `state` and prints the state it was given
  // once that state matches §9's pattern; a 201 carrying extra unknown members is not a
  // failure (the canonical body already carries `unverified_ref`), and stdout holds
  // exactly the documented members.
  it('created_body_extra_members_are_ignored_and_state_is_printed_verbatim', async () => {
    const env = await devEnv();
    const stub = stubFetch(() =>
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
      const stub = stubFetch(() => envelope(testCase.status, testCase.code, 'refused'));
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
    const detailsStub = stubFetch(() =>
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
    const stub = stubFetch(() =>
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
