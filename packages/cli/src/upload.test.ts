// 04.2.4-T1.1/T1.2/T1.3: `uploadFile()` end to end under a scripted store (04.2.4 §2
// AC1-AC4, §5, §6, §8, §9). Every request goes to an in-process responder: the
// `upload-url` call, the presigned PUT (whose body is streamed and re-verified against the
// declared digest) and `complete`. No case waits on a clock, uses the network or asserts
// wall-clock behaviour: `sleep` advances a virtual clock and `random` is fixed.
//
// The secrets rule (04.2.3 §8): the bearer and the presigned URL are assembled at runtime;
// the journal assertions prove neither ever reaches the disk.
import { createHash } from 'node:crypto';
import { mkdtemp, readdir, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resolveConfig } from './config.js';
import { CliError } from './errors.js';
import { resetSecrets } from './redact.js';
import {
  ChecksumMismatchError,
  UploadFailedError,
  deriveCompleteIdempotencyKey,
  deriveUploadUrlIdempotencyKey,
  hashUploadFile,
  maxMintAttempts,
  uploadChunkBytes,
  uploadFile,
  type UploadDeps,
} from './upload.js';
import { UploadJournal } from './uploadJournal.js';
import { closeUploadFile, openUploadFile, type UploadFile } from './uploadPaths.js';

/** Assembled at runtime so no token-shaped literal is stored in the spec (T78). */
const token = ['tk', 'upload', 'value'].join('-');
const runId = '3f1c2b4a-5d6e-4f70-8192-a3b4c5d6e7f8';
const producerId = 'shard-1';
const executionKey = 'a3'.repeat(32);
const artifactId = '7d1f9a52-3c4e-4b6a-9f01-2b3c4d5e6f70';
const secondArtifactId = '0a1b2c3d-4e5f-4a6b-8c9d-0e1f2a3b4c5d';
/** The presigned URL: a loopback origin with a signature in the query (never printed). */
const presignedUrl = `http://127.0.0.1:9/pending/bucket/${artifactId}?X-Amz-Signature=deadbeef&X-Amz-Credential=AKIA%2F20261024`;
const config = resolveConfig({
  env: { EVIDGATE_API_URL: 'http://127.0.0.1:9', EVIDGATE_PROJECT: 'proj-1' },
});

interface Call {
  readonly method: string;
  readonly url: string;
  readonly headers: Headers;
  /** The bytes the request put on the wire (the JSON body for the API calls). */
  body: Buffer;
  /** One entry per body chunk as it crossed the wire; proves the PUT never buffers it. */
  chunks: number[];
  json: unknown;
}

type Responder = (call: Call) => Response | Promise<Response>;

interface Harness {
  readonly deps: UploadDeps;
  readonly calls: Call[];
  readonly sleeps: number[];
  readonly clock: { now: number };
}

/**
 * A scripted store. A non-PUT body is buffered (it is JSON); a PUT body is consumed chunk
 * by chunk, recording each chunk's size, so a spec can prove the file was streamed.
 */
function harness(respond: Responder): Harness {
  const calls: Call[] = [];
  const sleeps: number[] = [];
  const clock = { now: 1_000 };

  const fetchImpl: typeof fetch = async (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    const method = request.method.toUpperCase();
    // The call is recorded BEFORE the body is consumed: a body that aborts mid-stream (the
    // file changed between passes) still counts as an attempt that reached the store.
    const call: Call = {
      method,
      url: request.url,
      headers: new Headers(request.headers),
      body: Buffer.alloc(0),
      chunks: [],
      json: undefined,
    };
    calls.push(call);
    const parts: Buffer[] = [];
    if (method === 'PUT' && request.body !== null) {
      for await (const chunk of request.body as unknown as AsyncIterable<Uint8Array>) {
        call.chunks.push(chunk.byteLength);
        parts.push(Buffer.from(chunk));
      }
    } else {
      parts.push(Buffer.from(await request.clone().arrayBuffer()));
    }
    call.body = Buffer.concat(parts);
    if (method !== 'PUT' && call.body.length > 0) {
      call.json = JSON.parse(call.body.toString('utf8'));
    }
    return respond(call);
  };

  return {
    calls,
    sleeps,
    clock,
    deps: {
      fetch: fetchImpl,
      sleep: (milliseconds) => {
        sleeps.push(milliseconds);
        clock.now += milliseconds;
        return Promise.resolve();
      },
      now: () => clock.now,
      random: () => 0.5,
    },
  };
}

function json(status: number, body: unknown, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json', ...headers },
  });
}

/** The `upload-url` 201 body: the signed size and checksum are the true ones for `bytes`. */
function uploadUrlBody(input: {
  readonly bytes: Buffer;
  readonly sha256: string;
  readonly artifact?: string;
  readonly url?: string;
  readonly expiresAt?: string;
}): Record<string, unknown> {
  const digest = Buffer.from(input.sha256.slice('sha256:'.length), 'hex').toString('base64');
  return {
    artifact_id: input.artifact ?? artifactId,
    object_key: `pending/org/${input.artifact ?? artifactId}`,
    upload_url: input.url ?? presignedUrl,
    required_headers: {
      'Content-Length': String(input.bytes.byteLength),
      'x-amz-checksum-sha256': digest,
    },
    expires_at: input.expiresAt ?? '2026-10-24T23:59:59.000Z',
  };
}

/** The `complete` 200 body for a committed ready artifact. */
function readyBody(bytes: number, artifact = artifactId): Record<string, unknown> {
  return {
    artifact_id: artifact,
    state: 'ready',
    bytes,
    scan_state: 'pending',
    failure_code: null,
  };
}

function uploadUrlCall(calls: readonly Call[]): Call {
  const call = calls.find((entry) => entry.url.endsWith('/v1/artifacts/upload-url'));
  if (call === undefined) {
    throw new Error('no upload-url call was made');
  }
  return call;
}

describe('uploadFile', () => {
  const dirs: string[] = [];
  let root: string;
  let opened: UploadFile | undefined;

  beforeEach(async () => {
    resetSecrets();
    root = await mkdtemp(join(tmpdir(), 'evidgate-upload-'));
    dirs.push(root);
  });

  afterEach(async () => {
    if (opened !== undefined) {
      await closeUploadFile(opened);
      opened = undefined;
    }
    await Promise.all(dirs.splice(0).map((dir) => rm(dir, { recursive: true, force: true })));
  });

  /** Writes one file under the root and opens it through the guard (the command's path). */
  async function file(
    name: string,
    content: Buffer | string = 'trace-bytes',
  ): Promise<{ readonly opened: UploadFile; readonly sha256: string; readonly path: string }> {
    const path = join(root, name);
    await writeFile(path, content);
    const handle = await openUploadFile({ file: path, kind: 'trace', dirs: [root], cwd: root });
    opened = handle;
    return { opened: handle, sha256: await hashUploadFile(handle), path };
  }

  /** A journal the upload can resume from, in a producer directory beside the file. */
  async function journal(): Promise<UploadJournal> {
    const directory = await mkdtemp(join(tmpdir(), 'evidgate-upload-journal-'));
    dirs.push(directory);
    let tail: Promise<unknown> = Promise.resolve();
    return UploadJournal.open({
      producerDirectory: directory,
      withLock: <T>(fn: () => Promise<T>): Promise<T> => {
        const next = tail.then(fn, fn);
        tail = next.catch(() => undefined);
        return next;
      },
    });
  }

  function request(
    input: { readonly opened: UploadFile; readonly sha256: string },
    harnessed: Harness,
    resume: UploadJournal | undefined,
  ) {
    return {
      config,
      token,
      runId,
      producerId,
      executionKey,
      attemptNo: 1,
      kind: 'trace' as const,
      mediaType: 'application/zip',
      file: input.opened,
      sha256: input.sha256,
      journal: resume,
      deadlineMs: 1_800_000,
      deps: harnessed.deps,
    };
  }

  // AC1 / task row `upload_url_producer_and_idem_key`: the body names the run, the producer
  // slot, the planned execution and the local digest; the key is the deterministic §12 one
  // and is identical across process restarts.
  it('upload_url_producer_and_idem_key', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const body = uploadUrlBody({ bytes: await readFile(fileHandle.realPath), sha256 });
    const first = harness((call) =>
      call.method === 'PUT' ? new Response(null, { status: 200 }) : json(201, body),
    );
    const second = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) return json(200, readyBody(11));
      return json(201, body);
    });

    // The first run stops at the PUT (a 500 the caller does not retry here), the second
    // runs from scratch: both must derive the same key.
    const failing = harness(() => json(500, { error: { code: 'unavailable' } }));
    await expect(
      uploadFile(request({ opened: fileHandle, sha256 }, failing, undefined)),
    ).rejects.toBeInstanceOf(CliError);
    expect(uploadUrlCall(failing.calls).headers.get('idempotency-key')).toBe(
      deriveUploadUrlIdempotencyKey({
        runId,
        producerId,
        executionKey,
        attemptNo: 1,
        kind: 'trace',
        fileSha256: sha256,
        mintNo: 0,
      }),
    );

    const outcome = await uploadFile(request({ opened: fileHandle, sha256 }, second, undefined));
    expect(outcome).toMatchObject({ artifactId, state: 'ready', bytes: 11, scanState: 'pending' });
    const call = uploadUrlCall(second.calls);
    expect(new URL(call.url).pathname).toBe('/v1/artifacts/upload-url');
    expect(call.headers.get('idempotency-key')).toBe(
      uploadUrlCall([...first.calls, ...failing.calls]).headers.get('idempotency-key'),
    );
    expect(call.json).toEqual({
      run_id: runId,
      producer_id: producerId,
      execution_key: executionKey,
      attempt_no: 1,
      kind: 'trace',
      media_type: 'application/zip',
      bytes: 11,
      sha256,
    });
  });

  // AC2 / §9 `checksum_and_size`: the PUT carries exactly the signed headers, the bearer is
  // never sent to the store, and the body is re-hashed while it streams.
  it('puts_exactly_the_signed_headers_and_verifies_the_body', async () => {
    const content = Buffer.from('x'.repeat(uploadChunkBytes * 2 + 7));
    const { opened: fileHandle, sha256 } = await file('trace.zip', content);
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) return json(200, readyBody(content.byteLength));
      return json(201, uploadUrlBody({ bytes: content, sha256 }));
    });

    await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));

    const put = store.calls.find((call) => call.method === 'PUT');
    expect(put?.url.startsWith('http://127.0.0.1:9/pending/')).toBe(true);
    expect(put?.headers.get('content-length')).toBe(String(content.byteLength));
    expect(put?.headers.get('x-amz-checksum-sha256')).toBe(
      createHash('sha256').update(content).digest('base64'),
    );
    // The presigned PUT is unauthenticated for our token: the bearer must never leave the
    // API host, and the signature is the only credential on the wire.
    expect(put?.headers.get('authorization')).toBeNull();
    expect(put?.body.equals(content)).toBe(true);
    // Never buffered whole: every chunk is at most one read of 64 KiB.
    expect(put?.chunks.every((size) => size <= uploadChunkBytes)).toBe(true);
    expect(put?.chunks.length).toBeGreaterThan(1);
    expect(put?.chunks.reduce((sum, size) => sum + size, 0)).toBe(content.byteLength);
  });

  // §9 `put_503_then_success`: one upload-url, a retried PUT, no second artifact row.
  it('retries_a_put_5xx_without_a_second_upload_url', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const bytes = (await readFile(fileHandle.realPath)).byteLength;
    let puts = 0;
    const store = harness((call) => {
      if (call.method === 'PUT') {
        puts += 1;
        return puts === 1
          ? new Response('busy', { status: 503 })
          : new Response(null, { status: 200 });
      }
      if (call.url.endsWith('/complete')) return json(200, readyBody(bytes));
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    const outcome = await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));

    expect(outcome.artifactId).toBe(artifactId);
    expect(store.calls.filter((call) => call.url.endsWith('/artifacts/upload-url'))).toHaveLength(
      1,
    );
    expect(puts).toBe(2);
    // Full jitter at the spec's fixed sample (0.5) halves the 500 ms base.
    expect(store.sleeps).toEqual([250]);
  });

  // §9 `429 Retry-After honoured`: the wait is the server's value, not the backoff sample.
  it('honours_retry_after_on_the_put', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const bytes = (await readFile(fileHandle.realPath)).byteLength;
    let puts = 0;
    const store = harness((call) => {
      if (call.method === 'PUT') {
        puts += 1;
        return puts === 1
          ? new Response('slow down', { status: 429, headers: { 'retry-after': '7' } })
          : new Response(null, { status: 200 });
      }
      if (call.url.endsWith('/complete')) return json(200, readyBody(bytes));
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));

    expect(store.sleeps).toEqual([7_000]);
    expect(store.clock.now).toBe(8_000);
  });

  // §5/§9 `413/412 genuinely deterministic`: surfaced verbatim, one PUT, no complete.
  it.each([412, 413])('surfaces_a_%i_put_refusal_verbatim', async (status) => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    let puts = 0;
    const store = harness((call) => {
      if (call.method === 'PUT') {
        puts += 1;
        return new Response('STORE-BODY-MARKER', { status });
      }
      if (call.url.endsWith('/complete')) return json(200, readyBody(11));
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    let thrown: unknown;
    try {
      await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));
    } catch (error) {
      thrown = error;
    }

    expect(thrown).toBeInstanceOf(UploadFailedError);
    expect((thrown as UploadFailedError).status).toBe(status);
    expect((thrown as UploadFailedError).code).toBe('upload_put_failed');
    // The store's XML/text body is never echoed: it can carry the signature.
    expect((thrown as UploadFailedError).message).not.toContain('STORE-BODY-MARKER');
    expect(puts).toBe(1);
    expect(store.calls.some((call) => call.url.endsWith('/complete'))).toBe(false);
  });

  // AC3 / §9 `expired_window_rerequest`: a dead presigned URL gets a fresh row, and the
  // second mint uses its own deterministic key.
  it.each([403, 404, 410])('re_mints_after_a_%i_presigned_put', async (status) => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const bytes = (await readFile(fileHandle.realPath)).byteLength;
    let mints = 0;
    const store = harness((call) => {
      if (call.method === 'PUT') {
        mints += 1;
        return mints === 1
          ? new Response('expired', { status })
          : new Response(null, { status: 200 });
      }
      if (call.url.endsWith('/complete')) {
        const completed = call.url.slice(
          call.url.lastIndexOf('/artifacts/') + 11,
          -'/complete'.length,
        );
        return json(200, readyBody(bytes, completed));
      }
      const sequence = store.calls.filter((entry) => entry.url.endsWith('/upload-url')).length;
      return json(
        201,
        uploadUrlBody({
          bytes: Buffer.from('trace-bytes'),
          sha256,
          artifact: sequence === 1 ? artifactId : secondArtifactId,
          url: `http://127.0.0.1:9/pending/bucket/${sequence}?X-Amz-Signature=${sequence}`,
        }),
      );
    });

    const outcome = await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));

    expect(outcome.artifactId).toBe(secondArtifactId);
    const urls = store.calls.filter((call) => call.url.endsWith('/artifacts/upload-url'));
    expect(urls).toHaveLength(2);
    const key = (mint: number) =>
      deriveUploadUrlIdempotencyKey({
        runId,
        producerId,
        executionKey,
        attemptNo: 1,
        kind: 'trace',
        fileSha256: sha256,
        mintNo: mint,
      });
    expect(urls.map((call) => call.headers.get('idempotency-key'))).toEqual([key(0), key(1)]);
  });

  it('gives_up_after_the_mint_cap', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response('expired', { status: 403 });
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    await expect(
      uploadFile(request({ opened: fileHandle, sha256 }, store, undefined)),
    ).rejects.toMatchObject({ code: 'upload_put_failed', status: 403 });
    expect(store.calls.filter((call) => call.url.endsWith('/artifacts/upload-url'))).toHaveLength(
      maxMintAttempts,
    );
  });

  // AC2 / §8: a signed checksum that disagrees with the file is refused before any byte is
  // sent, and never retried.
  it('refuses_a_signed_checksum_that_does_not_match', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const body = uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 });
    const signed = body['required_headers'] as Record<string, string>;
    signed['x-amz-checksum-sha256'] = Buffer.alloc(32).toString('base64');
    const store = harness(() => json(201, body));

    await expect(
      uploadFile(request({ opened: fileHandle, sha256 }, store, undefined)),
    ).rejects.toMatchObject({ code: 'request_failed' });
    expect(store.calls.some((call) => call.method === 'PUT')).toBe(false);
  });

  it('refuses_an_upload_url_without_a_signed_size', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const body = uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 });
    body['required_headers'] = { 'x-amz-checksum-sha256': 'x' };
    const store = harness(() => json(201, body));

    await expect(
      uploadFile(request({ opened: fileHandle, sha256 }, store, undefined)),
    ).rejects.toMatchObject({ code: 'request_failed' });
    expect(store.calls.some((call) => call.method === 'PUT')).toBe(false);
  });

  // §8 "one pass each, O(1) memory": the file changed between the hash pass and the PUT
  // pass, so the streaming re-hash aborts the upload — no complete, and the mismatch is
  // never retried (the same bytes would fail again).
  it('aborts_when_the_file_changes_between_passes', async () => {
    const { opened: fileHandle, sha256, path } = await file('trace.zip', 'first-content');
    await writeFile(path, 'other-content');
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      return json(201, uploadUrlBody({ bytes: Buffer.from('first-content'), sha256 }));
    });

    let thrown: unknown;
    try {
      await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));
    } catch (error) {
      thrown = error;
    }

    expect(thrown).toBeInstanceOf(ChecksumMismatchError);
    expect((thrown as CliError).code).toBe('upload_checksum_mismatch');
    expect(store.calls.filter((call) => call.method === 'PUT')).toHaveLength(1);
    expect(store.calls.some((call) => call.url.endsWith('/complete'))).toBe(false);
    expect(store.sleeps).toEqual([]);
  });

  // AC4 / §9 `complete_rejected`: a committed `rejected` is exit 3 naming the artifact and
  // the server's reason, with no retry.
  it('fails_on_a_rejected_complete', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) {
        return json(200, {
          artifact_id: artifactId,
          state: 'rejected',
          bytes: null,
          scan_state: null,
          failure_code: 'checksum_mismatch',
        });
      }
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    let thrown: unknown;
    try {
      await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));
    } catch (error) {
      thrown = error;
    }

    const error = thrown as CliError;
    expect(error.code).toBe('artifact_rejected');
    expect(error.message).toContain(artifactId);
    expect(error.message).toContain('checksum_mismatch');
    expect(store.calls.filter((call) => call.url.endsWith('/complete'))).toHaveLength(1);
  });

  // §12 replay: a rejection leaves `complete_done` false, so a re-run replays `complete`
  // against the same row and is rejected again — the hint names the journal row that must
  // be deleted to start over, because re-running alone never mints a second row.
  it('replays_a_rejected_complete_and_stays_terminal', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const resume = await journal();
    const rejection = (): Response =>
      json(200, {
        artifact_id: artifactId,
        state: 'rejected',
        bytes: null,
        scan_state: null,
        failure_code: 'checksum_mismatch',
      });

    const first = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) return rejection();
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });
    await expect(
      uploadFile(request({ opened: fileHandle, sha256 }, first, resume)),
    ).rejects.toMatchObject({ code: 'artifact_rejected' });
    expect(await resume.findResumable(sha256, 11)).toMatchObject({
      artifact_id: artifactId,
      put_done: true,
      complete_done: false,
    });

    // The restart has no upload-url and no PUT left to do: it can only ask `complete`.
    const replay = harness((call) =>
      call.url.endsWith('/complete') ? rejection() : json(500, { error: { code: 'unexpected' } }),
    );
    let thrown: unknown;
    try {
      await uploadFile(request({ opened: fileHandle, sha256 }, replay, resume));
    } catch (error) {
      thrown = error;
    }

    const error = thrown as CliError;
    expect(error.code).toBe('artifact_rejected');
    expect(replay.calls).toHaveLength(1);
    expect(replay.calls[0]?.url.endsWith(`/v1/artifacts/${artifactId}/complete`)).toBe(true);
    expect(error.hint).toContain('this row stays rejected');
    expect(error.hint).toContain(`uploads/${artifactId}.json`);
    expect((await resume.findResumable(sha256, 11))?.complete_done).toBe(false);
  });

  // §5 error fidelity: the server's code and request id survive; the status is kept so the
  // command can purge a dead token; deterministic statuses are never retried.
  it.each([409, 422])('surfaces_a_%i_complete_verbatim', async (status) => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) {
        return json(status, {
          error: {
            code: status === 409 ? 'artifact_state_conflict' : 'artifact_invalid',
            message: 'the artifact cannot be completed',
            request_id: 'req-42',
            retryable: false,
          },
        });
      }
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    let thrown: unknown;
    try {
      await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));
    } catch (error) {
      thrown = error;
    }

    const error = thrown as UploadFailedError;
    expect(error.status).toBe(status);
    expect(error.code).toBe(status === 409 ? 'artifact_state_conflict' : 'artifact_invalid');
    expect(error.requestId).toBe('req-42');
    expect(store.calls.filter((call) => call.url.endsWith('/complete'))).toHaveLength(1);
  });

  it('hints_the_auth_command_on_a_401', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const store = harness(() =>
      json(401, {
        error: { code: 'token_expired', message: 'expired', request_id: 'req-7', retryable: false },
      }),
    );

    let thrown: unknown;
    try {
      await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));
    } catch (error) {
      thrown = error;
    }

    const error = thrown as UploadFailedError;
    expect(error.status).toBe(401);
    expect(error.code).toBe('token_expired');
    expect(error.hint).toContain('evidgate auth');
    expect(store.calls).toHaveLength(1);
  });

  it('verifies_the_byte_count_complete_reports', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) return json(200, readyBody(12));
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    await expect(
      uploadFile(request({ opened: fileHandle, sha256 }, store, undefined)),
    ).rejects.toMatchObject({ code: 'request_failed' });
  });

  // §9 `restart after put_done calls only complete` + the journal contract (§12).
  it('resumes_after_put_done_with_only_complete', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const resume = await journal();
    const failing = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) {
        return json(503, {
          error: { code: 'unavailable', message: 'down', request_id: 'r', retryable: true },
        });
      }
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    await expect(
      uploadFile(request({ opened: fileHandle, sha256 }, failing, resume)),
    ).rejects.toMatchObject({ code: 'unavailable' });
    const stored = await resume.findResumable(sha256, 11);
    expect(stored).toMatchObject({ artifact_id: artifactId, put_done: true, complete_done: false });

    const restarted = harness((call) =>
      call.url.endsWith('/complete') ? json(200, readyBody(11)) : json(500, {}),
    );
    const outcome = await uploadFile(request({ opened: fileHandle, sha256 }, restarted, resume));

    expect(outcome.artifactId).toBe(artifactId);
    expect(restarted.calls).toHaveLength(1);
    expect(restarted.calls[0]?.url.endsWith(`/v1/artifacts/${artifactId}/complete`)).toBe(true);
    expect(restarted.calls[0]?.headers.get('idempotency-key')).toBe(
      deriveCompleteIdempotencyKey({ artifactId, fileSha256: sha256, size: 11 }),
    );
    expect((await resume.findResumable(sha256, 11))?.complete_done).toBe(true);
  });

  // §9 / §12: a second run over a completed journal row answers from the disk — no request,
  // so at most one artifact row per upload.
  it('does_not_upload_again_once_the_journal_is_complete', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const resume = await journal();
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) return json(200, readyBody(11));
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    const first = await uploadFile(request({ opened: fileHandle, sha256 }, store, resume));
    const before = store.calls.length;
    const second = await uploadFile(request({ opened: fileHandle, sha256 }, store, resume));

    expect(second.artifactId).toBe(first.artifactId);
    expect(second.state).toBe('ready');
    expect(store.calls.length).toBe(before);
  });

  it('resumes_an_abandoned_mint_with_the_same_key', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const resume = await journal();
    // First run: the mint lands in the journal, the PUT then dies with a permanent 500 on
    // every attempt (the deadline ends it) — the journal row is not put_done.
    const failing = harness((call) => {
      if (call.method === 'PUT') return new Response('nope', { status: 503 });
      if (call.url.endsWith('/complete')) return json(200, readyBody(11));
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });
    await expect(
      uploadFile(request({ opened: fileHandle, sha256 }, failing, resume)),
    ).rejects.toBeInstanceOf(CliError);
    const written = await resume.findResumable(sha256, 11);
    expect(written).toMatchObject({ mint_no: 0, put_done: false, complete_done: false });

    const restarted = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) return json(200, readyBody(11));
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });
    await uploadFile(request({ opened: fileHandle, sha256 }, restarted, resume));

    // The restart re-requests with mint 0 — the key the first run used — so the server
    // replays the stored row instead of minting a second artifact.
    expect(uploadUrlCall(restarted.calls).headers.get('idempotency-key')).toBe(
      deriveUploadUrlIdempotencyKey({
        runId,
        producerId,
        executionKey,
        attemptNo: 1,
        kind: 'trace',
        fileSha256: sha256,
        mintNo: 0,
      }),
    );
    expect(uploadUrlCall(restarted.calls).headers.get('idempotency-key')).toBe(
      uploadUrlCall(failing.calls).headers.get('idempotency-key'),
    );
  });

  // §8/§12 secret hygiene: the journal survives the process, so it must name no URL, no
  // bearer and no header value.
  it('never_writes_a_url_bearer_or_header_to_the_journal', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const resume = await journal();
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) return json(200, readyBody(11));
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    await uploadFile(request({ opened: fileHandle, sha256 }, store, resume));

    const directory = resume.uploadsDirectory;
    const names = await readdir(directory);
    expect(names).toEqual([`${artifactId}.json`]);
    const text = await readFile(join(directory, names[0] as string), 'utf8');
    expect(text).not.toContain('http');
    expect(text).not.toContain('X-Amz');
    expect(text).not.toContain('Signature');
    expect(text).not.toContain(token);
    const record = JSON.parse(text) as Record<string, unknown>;
    expect(record).toEqual({
      artifact_id: artifactId,
      file_sha256: sha256,
      size: 11,
      put_done: true,
      complete_done: true,
      mint_no: 0,
    });
  });

  // §5 retry rule: a transport failure is retried; the second attempt is the same PUT.
  it('retries_a_transport_failure', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    let puts = 0;
    const store = harness((call) => {
      if (call.method === 'PUT') {
        puts += 1;
        if (puts === 1) throw new TypeError('fetch failed');
        return new Response(null, { status: 200 });
      }
      if (call.url.endsWith('/complete')) return json(200, readyBody(11));
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));

    expect(puts).toBe(2);
    expect(store.sleeps).toHaveLength(1);
  });

  // §9 `deadline_respected`: a store that never succeeds inside the deadline surfaces the
  // last error's code and status, and the journal is not advanced.
  it('stops_at_the_upload_deadline', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const store = harness(() =>
      json(503, {
        error: { code: 'unavailable', message: 'down', request_id: 'req-3', retryable: true },
      }),
    );

    let thrown: unknown;
    try {
      await uploadFile({
        ...request({ opened: fileHandle, sha256 }, store, undefined),
        deadlineMs: 1_000,
      });
    } catch (error) {
      thrown = error;
    }

    const error = thrown as CliError;
    expect(error.code).toBe('deadline_exceeded');
    expect(error.message).toContain('unavailable (HTTP 503) request_id=req-3');
    // The message never names the URL or the file.
    expect(error.message).not.toContain('http');
    expect(error.message).not.toContain('trace.zip');
  });
});
