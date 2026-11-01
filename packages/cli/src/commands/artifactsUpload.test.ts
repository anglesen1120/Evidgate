// 04.2.4-T1.4: `evidgate artifacts upload` on the binary — the command surface of
// 04.2.4 §5 over the `uploadFile()` library (contracts/cli.md §12): flags and their
// validation, the media-type derivation, the file guard BEFORE any request, the output
// line, the 401 token purge, verbatim server refusals and the redaction of a presigned URL.
import { createHash } from 'node:crypto';
import { chmod, mkdir, mkdtemp, open, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { readCachedToken, writeCachedToken } from '../auth/tokenCache.js';
import { resolveConfig } from '../config.js';
import { main } from '../main.js';
import { resetSecrets, type Io } from '../redact.js';
import { journalFileName, type UploadIdentity } from '../uploadJournal.js';

/** Assembled at runtime so no token-shaped literal is stored in the spec (T78). */
const devToken = ['dev', 'artifact', 'token'].join('-');
/** The `ci_producer` token the exchange hands out; it is the bearer, not the dev credential. */
const ciToken = ['dev', 'artifact', 'ci', 'producer'].join('-');
/** The `project_id` a token-exchange answer carries (openapi `format: uuid`). */
const projectUuid = '5c8d9e0f-1a2b-4c3d-8e9f-0a1b2c3d4e5f';
const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
const executionKey = 'b4'.repeat(32);
const artifactId = '7d1f9a52-3c4e-4b6a-9f01-2b3c4d5e6f70';
const project = 'proj-1';

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
  readonly method: string;
  readonly url: string;
  readonly headers: Headers;
  readonly json: unknown;
}

type Responder = (call: StubCall) => Response | Promise<Response>;

/**
 * The token-exchange answer. The dev credential is exchanged like an ingest token, so this is
 * the one success body the stub owes it; `token` is the bearer every artifact call carries.
 */
function ciTokenBody(): Record<string, unknown> {
  return {
    token: ciToken,
    expires_at: '2030-01-01T00:00:00Z',
    project_id: projectUuid,
  };
}

/**
 * A fetch stub that routes the calls of an upload and records them. A dev token is an ingest
 * credential now, so `POST /v1/ci/token` is answered here and kept in `exchanges`: `calls`
 * then holds exactly the artifact route's own requests, which is what each case asserts.
 */
function stubFetch(respond: Responder): {
  readonly fetchImpl: typeof fetch;
  readonly calls: StubCall[];
  readonly exchanges: StubCall[];
} {
  const calls: StubCall[] = [];
  const exchanges: StubCall[] = [];
  const fetchImpl: typeof fetch = async (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    const method = request.method.toUpperCase();
    // A PUT body is a stream: read it directly instead of cloning (a `clone()` on a
    // half-duplex body would tee it and stall until the other branch is drained).
    const body =
      method === 'PUT' && request.body !== null
        ? Buffer.from(await new Response(request.body as unknown as ReadableStream).arrayBuffer())
        : Buffer.from(await request.clone().arrayBuffer());
    const call: StubCall = {
      method,
      url: request.url,
      headers: new Headers(request.headers),
      json: method === 'PUT' || body.length === 0 ? undefined : JSON.parse(body.toString('utf8')),
    };
    if (method === 'POST' && new URL(request.url).pathname === '/v1/ci/token') {
      exchanges.push(call);
      return jsonResponse(200, ciTokenBody());
    }
    calls.push(call);
    return respond(call);
  };
  return { fetchImpl, calls, exchanges };
}

function jsonResponse(
  status: number,
  body: unknown,
  headers: Record<string, string> = {},
): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json', ...headers },
  });
}

describe('artifacts upload', () => {
  const dirs: string[] = [];
  let root: string;

  beforeEach(async () => {
    resetSecrets();
    root = await mkdtemp(join(tmpdir(), 'evidgate-artifacts-'));
    dirs.push(root);
  });

  afterEach(async () => {
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  /** Writes a file under the run's output root and returns its path. */
  async function evidence(
    name = 'trace.zip',
    content: Buffer | string = 'trace-bytes',
  ): Promise<string> {
    const path = join(root, name);
    await mkdir(join(path, '..'), { recursive: true });
    await writeFile(path, content);
    return path;
  }

  function devEnv(extra: Record<string, string> = {}): Record<string, string> {
    return {
      EVIDGATE_API_URL: 'http://127.0.0.1:8080',
      EVIDGATE_PROJECT: project,
      EVIDGATE_ENV: 'dev',
      EVIDGATE_DEV_TOKEN: devToken,
      // The exchange writes its `ci_producer` token cache, so it is kept in the temp root.
      EVIDGATE_CACHE_DIR: root,
      ...extra,
    };
  }

  /** The happy-path responder: `upload-url` 201, PUT 200, complete 200 ready. */
  function happyStore(bytes = 11, sha256 = `sha256:${'a'.repeat(64)}`): Responder {
    const base = Buffer.from('trace-bytes');
    return (call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) {
        return jsonResponse(200, {
          artifact_id: artifactId,
          state: 'ready',
          bytes,
          scan_state: 'pending',
          failure_code: null,
        });
      }
      return jsonResponse(201, {
        artifact_id: artifactId,
        object_key: 'pending/org/x',
        upload_url: 'http://127.0.0.1:9/pending/x?X-Amz-Signature=abc',
        required_headers: {
          'Content-Length': String(base.byteLength),
          'x-amz-checksum-sha256': createHash('sha256').update(base).digest('base64'),
        },
        expires_at: '2026-10-24T23:59:59.000Z',
        sha256,
      });
    };
  }

  function argv(file: string, extra: string[] = []): string[] {
    return [
      'artifacts',
      'upload',
      '--run',
      runId,
      '--file',
      file,
      '--kind',
      'trace',
      '--execution-key',
      executionKey,
      '--dir',
      root,
      ...extra,
    ];
  }

  // AC1 / §5: the command prints the artifact id and its state, and sends the body the
  // openapi route documents (producer slot `shard-1` when nothing is given).
  it('uploads_and_prints_the_artifact', async () => {
    const file = await evidence();
    const store = stubFetch(happyStore());
    const { io, stdout, stderr } = capture();

    await expect(main(argv(file), devEnv(), io, { fetch: store.fetchImpl })).resolves.toBe(0);

    expect(stdout.join('')).toBe(`artifact_id=${artifactId} state=ready bytes=11 file=trace.zip\n`);
    expect(stderr.join('')).toBe('');
    // The dev credential is an ingest token: one exchange at `/v1/ci/token` precedes the
    // upload, it presents the dev token, and the artifact calls bear the minted token.
    expect(store.exchanges.map((call) => call.url)).toStrictEqual([
      'http://127.0.0.1:8080/v1/ci/token',
    ]);
    expect(store.exchanges[0]?.json).toEqual({ ingest_token: devToken });
    const uploadUrl = store.calls[0];
    expect(uploadUrl?.url).toBe('http://127.0.0.1:8080/v1/artifacts/upload-url');
    expect(uploadUrl?.headers.get('authorization')).toBe(`Bearer ${ciToken}`);
    expect(uploadUrl?.json).toEqual({
      run_id: runId,
      producer_id: 'shard-1',
      execution_key: executionKey,
      attempt_no: 1,
      kind: 'trace',
      media_type: 'application/zip',
      bytes: 11,
      sha256: createHash('sha256').update('trace-bytes').digest('hex').replace(/^/, 'sha256:'),
    });
    // The presigned URL never reaches stdout/stderr.
    expect(stdout.join('')).not.toContain('X-Amz');
  });

  it('prints_one_json_line_with_json', async () => {
    const file = await evidence();
    const store = stubFetch(happyStore());
    const { io, stdout } = capture();

    await expect(
      main(argv(file, ['--json']), devEnv(), io, { fetch: store.fetchImpl }),
    ).resolves.toBe(0);

    expect(stdout.join('')).toBe(
      `${JSON.stringify({ file: 'trace.zip', artifact_id: artifactId, state: 'ready', bytes: 11, scan_state: 'pending' })}\n`,
    );
  });

  // §8: the file guard runs before any request — not even the token exchange.
  it('refuses_a_file_outside_the_allowed_directories_before_any_request', async () => {
    const outside = join(await mkdtemp(join(tmpdir(), 'evidgate-outside-')), 'passwd');
    dirs.push(join(outside, '..'));
    await writeFile(outside, 'root:x:0:0', 'utf8');
    const store = stubFetch(happyStore());
    const { io, stdout, stderr } = capture();

    await expect(
      main(
        [
          'artifacts',
          'upload',
          '--run',
          runId,
          '--file',
          outside,
          '--kind',
          'trace',
          '--media-type',
          'application/zip',
          '--execution-key',
          executionKey,
        ],
        devEnv(),
        io,
        { fetch: store.fetchImpl },
      ),
    ).resolves.toBe(3);

    expect(store.calls).toHaveLength(0);
    expect(store.exchanges).toHaveLength(0);
    expect(stdout.join('')).toBe('');
    expect(stderr.join('')).toContain('evidgate: error upload_path_refused: ');
    expect(stderr.join('')).toContain('--dir');
  });

  it('refuses_a_file_above_the_kind_cap_before_any_request', async () => {
    const file = await evidence('big.png');
    const handle = await open(file, 'r+');
    await handle.truncate(10_485_761);
    await handle.close();
    const store = stubFetch(happyStore());
    const { io, stderr } = capture();

    await expect(
      main(
        [
          'artifacts',
          'upload',
          '--run',
          runId,
          '--file',
          file,
          '--kind',
          'screenshot',
          '--execution-key',
          executionKey,
          '--dir',
          root,
        ],
        devEnv(),
        io,
        { fetch: store.fetchImpl },
      ),
    ).resolves.toBe(3);

    expect(store.calls).toHaveLength(0);
    expect(store.exchanges).toHaveLength(0);
    expect(stderr.join('')).toContain('evidgate: error upload_too_large: ');
  });

  // §5: usage errors exit 3 with the help hint and never reach the network.
  it.each([
    [['--run', 'not-a-uuid'], '--run must be a UUID', ['--run', 'not-a-uuid']],
    [
      ['--execution-key', 'abc'],
      '--execution-key must be 64 lowercase hex characters',
      ['--execution-key', 'abc'],
    ],
    [['--attempt', '0'], '--attempt must be an integer of at least 1', ['--attempt', '0']],
    [['--producer', 'bad/producer'], '--producer must be 1..128', ['--producer', 'bad/producer']],
    [
      ['--media-type', 'bad\u0000type'],
      '--media-type must be 1..255 characters',
      ['--media-type', 'bad\u0000type'],
    ],
  ])('refuses_%s_as_a_usage_error', async (_label, message, overrides) => {
    const file = await evidence();
    const store = stubFetch(happyStore());
    const { io, stderr } = capture();

    // The override replaces the base value of the same flag (a repeat would be refused as
    // "may be given only once" before the value is validated).
    const flag = overrides[0] as string;
    const base = argv(file).filter(
      (entry: string, index: number, all: string[]) => entry !== flag && all[index - 1] !== flag,
    );
    await expect(
      main([...base, ...overrides], devEnv(), io, { fetch: store.fetchImpl }),
    ).resolves.toBe(3);

    expect(store.calls).toHaveLength(0);
    expect(store.exchanges).toHaveLength(0);
    expect(stderr.join('')).toContain('evidgate: error usage: ');
    expect(stderr.join('')).toContain(message);
    expect(stderr.join('')).toContain("run 'evidgate --help' for usage");
  });

  // The shared flag helper refuses an empty required value, so `--file ''` fails as a usage
  // error before any request rather than reaching the path guard (or `--run ''` the UUID
  // check), which are the wrong errors for a flag that is present but empty.
  it('refuses_an_empty_required_value_before_any_request', async () => {
    const store = stubFetch(happyStore());
    const { io, stdout, stderr } = capture();

    await expect(
      main(
        [
          'artifacts',
          'upload',
          '--run',
          runId,
          '--file',
          '',
          '--kind',
          'trace',
          '--execution-key',
          executionKey,
          '--dir',
          root,
        ],
        devEnv(),
        io,
        { fetch: store.fetchImpl },
      ),
    ).resolves.toBe(3);

    expect(store.calls).toHaveLength(0);
    expect(store.exchanges).toHaveLength(0);
    expect(stdout.join('')).toBe('');
    expect(stderr.join('')).toContain(
      'evidgate: error usage: --file must be a single, non-empty value',
    );
    expect(stderr.join('')).toContain("run 'evidgate --help' for usage");
  });

  it('refuses_an_unknown_kind', async () => {
    const file = await evidence();
    const store = stubFetch(happyStore());
    const { io, stderr } = capture();

    await expect(
      main(
        [
          'artifacts',
          'upload',
          '--run',
          runId,
          '--file',
          file,
          '--kind',
          'nope',
          '--execution-key',
          executionKey,
          '--dir',
          root,
        ],
        devEnv(),
        io,
        { fetch: store.fetchImpl },
      ),
    ).resolves.toBe(3);
    expect(store.calls).toHaveLength(0);
    expect(store.exchanges).toHaveLength(0);
    expect(stderr.join('')).toContain('--kind must be one of trace, video, screenshot');
  });

  it('requires_a_media_type_when_no_static_default_exists', async () => {
    const file = await evidence('snapshot.html', '<html></html>');
    const store = stubFetch(happyStore());
    const { io, stderr } = capture();

    await expect(
      main(
        [
          'artifacts',
          'upload',
          '--run',
          runId,
          '--file',
          file,
          '--kind',
          'dom_snapshot',
          '--execution-key',
          executionKey,
          '--dir',
          root,
        ],
        devEnv(),
        io,
        { fetch: store.fetchImpl },
      ),
    ).resolves.toBe(3);
    expect(stderr.join('')).toContain('--media-type is required for --kind dom_snapshot');
    expect(store.calls).toHaveLength(0);
    expect(store.exchanges).toHaveLength(0);
  });

  it('sends_the_given_media_type_and_attempt', async () => {
    const file = await evidence('snapshot.html', '<html></html>');
    const store = stubFetch((call) =>
      call.method === 'PUT'
        ? new Response(null, { status: 200 })
        : call.url.endsWith('/complete')
          ? jsonResponse(200, {
              artifact_id: artifactId,
              state: 'ready',
              bytes: 13,
              scan_state: 'pending',
              failure_code: null,
            })
          : jsonResponse(201, {
              artifact_id: artifactId,
              object_key: 'pending/org/x',
              upload_url: 'http://127.0.0.1:9/pending/x?X-Amz-Signature=abc',
              required_headers: {
                'Content-Length': '13',
                'x-amz-checksum-sha256': createHash('sha256')
                  .update('<html></html>')
                  .digest('base64'),
              },
              expires_at: '2026-10-24T23:59:59.000Z',
            }),
    );
    const { io } = capture();

    await expect(
      main(
        [
          'artifacts',
          'upload',
          '--run',
          runId,
          '--file',
          file,
          '--kind',
          'dom_snapshot',
          '--media-type',
          'text/html',
          '--attempt',
          '3',
          '--producer',
          'shard-7',
          '--execution-key',
          executionKey,
          '--dir',
          root,
        ],
        devEnv(),
        io,
        { fetch: store.fetchImpl },
      ),
    ).resolves.toBe(0);

    expect(store.calls[0]?.json).toMatchObject({
      media_type: 'text/html',
      attempt_no: 3,
      producer_id: 'shard-7',
      kind: 'dom_snapshot',
    });
  });

  // §5/§9: the server's code and request id surface verbatim; a 413 is never retried.
  it('surfaces_a_server_413_verbatim', async () => {
    const file = await evidence();
    const store = stubFetch(() =>
      jsonResponse(413, {
        error: {
          code: 'payload_too_large',
          message: 'the artifact is over the policy cap',
          request_id: 'req-413',
          retryable: false,
        },
      }),
    );
    const { io, stdout, stderr } = capture();

    await expect(main(argv(file), devEnv(), io, { fetch: store.fetchImpl })).resolves.toBe(3);

    expect(stdout.join('')).toBe('');
    expect(stderr.join('')).toContain('evidgate: error payload_too_large: ');
    expect(stderr.join('')).toContain('evidgate: request_id: req-413');
    expect(stderr.join('')).not.toContain(devToken);
    expect(stderr.join('')).not.toContain(ciToken);
    expect(store.calls).toHaveLength(1);
    expect(store.exchanges).toHaveLength(1);
  });

  // AC4: an `upload-url` 422 (an unplanned `(execution_key, attempt_no)`, a disallowed
  // path or MIME) exits 3 with the server's own code and request id, makes exactly one
  // request and is never retried.
  it('surfaces_a_422_from_upload_url_verbatim', async () => {
    const file = await evidence();
    const store = stubFetch(() =>
      jsonResponse(422, {
        error: {
          code: 'unknown_execution_key',
          message: 'the execution is not planned for this run',
          request_id: 'req-422',
          retryable: false,
        },
      }),
    );
    const { io, stdout, stderr } = capture();

    await expect(main(argv(file), devEnv(), io, { fetch: store.fetchImpl })).resolves.toBe(3);

    expect(stdout.join('')).toBe('');
    expect(stderr.join('')).toContain('evidgate: error unknown_execution_key: ');
    expect(stderr.join('')).toContain('evidgate: request_id: req-422');
    expect(store.calls).toHaveLength(1);
    expect(store.exchanges).toHaveLength(1);
    expect(store.calls[0]?.url.endsWith('/v1/artifacts/upload-url')).toBe(true);
  });

  it('surfaces_a_body_digest_refusal_as_upload_checksum_mismatch', async () => {
    const file = await evidence();
    const store = stubFetch((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) {
        return jsonResponse(200, {
          artifact_id: artifactId,
          state: 'rejected',
          bytes: null,
          scan_state: null,
          failure_code: 'checksum_mismatch',
        });
      }
      return jsonResponse(201, {
        artifact_id: artifactId,
        object_key: 'pending/org/x',
        upload_url: 'http://127.0.0.1:9/pending/x?X-Amz-Signature=abc',
        required_headers: {
          'Content-Length': '11',
          'x-amz-checksum-sha256': createHash('sha256').update('trace-bytes').digest('base64'),
        },
        expires_at: '2026-10-24T23:59:59.000Z',
      });
    });
    const { io, stderr } = capture();

    await expect(main(argv(file), devEnv(), io, { fetch: store.fetchImpl })).resolves.toBe(3);

    expect(stderr.join('')).toContain('evidgate: error artifact_rejected: ');
    expect(stderr.join('')).toContain(artifactId);
    expect(stderr.join('')).toContain('checksum_mismatch');
  });

  // §7 / 04.2.1 §6: a 401 purges the cached token, and the server code still surfaces.
  it('purges_the_cached_token_on_401', async () => {
    const file = await evidence();
    const cacheDir = join(root, 'cache');
    await mkdir(cacheDir, { recursive: true });
    await chmod(cacheDir, 0o700);
    const env = {
      EVIDGATE_API_URL: 'https://api.evidgate.test',
      EVIDGATE_PROJECT: project,
      EVIDGATE_CACHE_DIR: cacheDir,
      EVIDGATE_INGEST_TOKEN: 'egt_ingest_secret_value',
    };
    const config = resolveConfig({ env });
    await writeCachedToken(config, {
      token: 'cached-ci-token-value',
      expiresAt: '2099-01-01T00:00:00.000Z',
    });
    // The seeded entry is far in the future, which the cache refuses as untrusted, so the
    // stub's exchange mints the bearer and the 401 comes from the artifact route: that is
    // the refusal that must purge the cache.
    const store = stubFetch(() =>
      jsonResponse(401, {
        error: {
          code: 'token_expired',
          message: 'the CI token expired',
          request_id: 'req-401',
          retryable: false,
        },
      }),
    );
    const { io, stderr } = capture();

    await expect(main(argv(file), env, io, { fetch: store.fetchImpl })).resolves.toBe(3);

    expect(stderr.join('')).toContain('evidgate: error token_expired: ');
    expect(stderr.join('')).toContain('run `evidgate auth login`');
    // Neither the dev credential nor the bearer it minted is ever printed.
    expect(stderr.join('')).not.toContain(devToken);
    expect(stderr.join('')).not.toContain(ciToken);
    expect(store.calls).toHaveLength(1);
    expect(store.exchanges).toHaveLength(1);
    // The purge is best-effort and the server's error is the one that surfaced.
    await expect(readCachedToken(config)).resolves.toBeUndefined();
  });

  // §6 / §12: a presigned URL inside a server message loses its query — the signature is a
  // bearer credential and never appears on stderr.
  it('redacts_a_presigned_url_from_a_server_message', async () => {
    const file = await evidence();
    const store = stubFetch(() =>
      jsonResponse(409, {
        error: {
          code: 'idempotency_conflict',
          message:
            'the key was reused for a different payload: see http://127.0.0.1:9/pending/x?X-Amz-Signature=secret-sig&X-Amz-Credential=AKIA',
          request_id: 'req-409',
          retryable: false,
        },
      }),
    );
    const { io, stderr } = capture();

    await expect(main(argv(file), devEnv(), io, { fetch: store.fetchImpl })).resolves.toBe(3);

    const text = stderr.join('');
    expect(text).toContain('evidgate: error idempotency_conflict: ');
    expect(text).not.toContain('secret-sig');
    expect(text).not.toContain('X-Amz-Signature');
    expect(text).toContain('http://127.0.0.1:9/pending/x?[REDACTED]');
    // The refused credential and the token it minted stay off the wire too.
    expect(text).not.toContain(devToken);
    expect(text).not.toContain(ciToken);
  });

  // §11.1 / §12: with EVIDGATE_SPOOL_DIR set the journal appears under the producer dir;
  // unset, the upload still works and no spool is created anywhere.
  it('writes_the_journal_when_a_spool_dir_is_set', async () => {
    const file = await evidence();
    const spoolDir = join(root, 'spool');
    await mkdir(spoolDir, { recursive: true });
    const store = stubFetch(happyStore());
    const { io } = capture();

    await expect(
      main(argv(file), devEnv({ EVIDGATE_SPOOL_DIR: spoolDir }), io, { fetch: store.fetchImpl }),
    ).resolves.toBe(0);

    // The record is named by the upload identity (contracts/cli.md §12.5), not by the id.
    const identity: UploadIdentity = {
      file_sha256: `sha256:${createHash('sha256').update('trace-bytes').digest('hex')}`,
      size: 11,
      kind: 'trace',
      execution_key: executionKey,
      attempt_no: 1,
      media_type: 'application/zip',
    };
    const journal = join(
      spoolDir,
      runId,
      'shard-1',
      'uploads',
      journalFileName(identity, artifactId),
    );
    const record = JSON.parse(await readFile(journal, 'utf8')) as Record<string, unknown>;
    expect(record).toMatchObject({ artifact_id: artifactId, put_done: true, complete_done: true });
  });

  it('uploads_without_a_spool_dir', async () => {
    const file = await evidence();
    const store = stubFetch(happyStore());
    const { io, stdout } = capture();

    await expect(main(argv(file), devEnv(), io, { fetch: store.fetchImpl })).resolves.toBe(0);
    expect(stdout.join('')).toContain(`artifact_id=${artifactId}`);
  });

  // §3: a malformed EVIDGATE_UPLOAD_DEADLINE refuses before the token exchange.
  it('refuses_a_malformed_upload_deadline', async () => {
    const file = await evidence();
    const store = stubFetch(happyStore());
    const { io, stderr } = capture();

    await expect(
      main(argv(file), devEnv({ EVIDGATE_UPLOAD_DEADLINE: 'forever' }), io, {
        fetch: store.fetchImpl,
      }),
    ).resolves.toBe(3);

    expect(store.calls).toHaveLength(0);
    expect(store.exchanges).toHaveLength(0);
    expect(stderr.join('')).toContain('evidgate: error invalid_config: ');
    expect(stderr.join('')).toContain('EVIDGATE_UPLOAD_DEADLINE');
    expect(stderr.join('')).not.toContain('forever');
  });
});
