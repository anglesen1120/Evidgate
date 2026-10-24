// 04.2.4-T1.1/T1.2/T1.3: `uploadFile()` end to end under a scripted store (04.2.4 §2
// AC1-AC4, §5, §6, §8, §9). Every request goes to an in-process responder: the
// `upload-url` call, the presigned PUT (whose body is streamed and re-verified against the
// declared digest) and `complete`. No case waits on a clock, uses the network or asserts
// wall-clock behaviour: `sleep` advances a virtual clock and `random` is fixed.
//
// The secrets rule (04.2.3 §8): the bearer and the presigned URL are assembled at runtime;
// the journal assertions prove neither ever reaches the disk.
import { createHash } from 'node:crypto';
import { mkdtemp, readdir, readFile, rename, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

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
  putStallTimeoutMs,
  uploadChunkBytes,
  uploadFile,
  type UploadDeps,
  type UploadRequest,
  type UploadTimers,
} from './upload.js';
import { SpoolLockedError } from './spoolLock.js';
import { UploadJournal, journalFileName, type UploadIdentity } from './uploadJournal.js';
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

/** The URL of a fetch call, whichever of the three spellings the transport used. */
function fetchUrl(input: string | URL | Request): string {
  return typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
}

/**
 * A timer seam over a virtual clock: `set` records when the timer is due instead of arming
 * anything, and `fireDue()` (called by the spec at the exact point simulated time passes)
 * runs whatever is due, in due order. Nothing here races real file or network I/O, so a
 * spec that drives time through this seam never depends on how fast the runner is.
 */
function virtualTimers(clock: { now: number }): {
  readonly timers: UploadTimers;
  readonly scheduled: Map<number, { readonly ms: number; readonly fn: () => void }>;
  readonly fireDue: () => number;
} {
  const scheduled = new Map<
    number,
    { readonly due: number; readonly ms: number; readonly fn: () => void }
  >();
  let nextId = 1;
  const timers: UploadTimers = {
    set: (milliseconds, fn) => {
      const id = nextId;
      nextId += 1;
      scheduled.set(id, { due: clock.now + milliseconds, ms: milliseconds, fn });
      return id;
    },
    clear: (handle) => {
      scheduled.delete(handle as number);
    },
  };
  const fireDue = (): number => {
    let fired = 0;
    for (;;) {
      const due = [...scheduled.entries()]
        .filter(([, entry]) => entry.due <= clock.now)
        .sort((left, right) => left[1].due - right[1].due)[0];
      if (due === undefined) return fired;
      scheduled.delete(due[0]);
      due[1].fn();
      fired += 1;
    }
  };
  return { timers, scheduled, fireDue };
}

/** Rejects with the signal's reason when the request is aborted: the only non-retryable exit. */
function aborted(signal: AbortSignal): Promise<never> {
  return new Promise<never>((_resolve, reject) => {
    const fail = (): void => {
      const reason: unknown = signal.reason;
      reject(reason instanceof Error ? reason : new Error('the request was aborted'));
    };
    if (signal.aborted) {
      fail();
      return;
    }
    signal.addEventListener('abort', fail, { once: true });
  });
}

/** The method of a fetch call; the SDK passes a Request, the PUT passes `init`. */
function fetchMethod(input: string | URL | Request, init: RequestInit | undefined): string {
  const method = init?.method ?? (input instanceof Request ? input.method : 'GET');
  return method.toUpperCase();
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

  /** The journal identity an upload of this file derives (contracts/cli.md §12.5). */
  function journalIdentity(
    digest: string,
    size: number,
    overrides: Partial<UploadIdentity> = {},
  ): UploadIdentity {
    return {
      file_sha256: digest,
      size,
      kind: 'trace',
      execution_key: executionKey,
      attempt_no: 1,
      media_type: 'application/zip',
      ...overrides,
    };
  }

  function request(
    input: { readonly opened: UploadFile; readonly sha256: string },
    harnessed: { readonly deps: UploadDeps },
    resume: UploadJournal | undefined,
    overrides: Partial<UploadRequest> = {},
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
      ...overrides,
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

  // N7 (P3): `failure_code` is server text that lands in the message, and the message reaches
  // stderr and every `--json` consumer. Only a plain token is echoed verbatim; anything that
  // could repaint a terminal or forge a line is replaced by a stable marker.
  it.each([
    ['an escape sequence', 'checksum\u001b[31m_mismatch'],
    ['a newline', 'checksum\nmismatch'],
    ['an over-long value', 'a'.repeat(65)],
    ['a colon', 'checksum:mismatch'],
  ])('does_not_echo_a_failure_code_that_carries_%s', async (_case, failureCode) => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) {
        return json(200, {
          artifact_id: artifactId,
          state: 'rejected',
          bytes: null,
          scan_state: null,
          failure_code: failureCode,
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
    expect(error.code).toBe('artifact_rejected');
    expect(error.message).toContain('invalid_failure_code');
    expect(error.message).not.toContain(failureCode);
    expect(error.message).not.toContain('\u001b');
    expect(error.message).not.toContain('\n');
  });

  // The fallback is for unsafe text only: a `rejected` complete with no code at all is
  // `unknown`, which is what actually happened.
  it('names_a_rejection_without_a_failure_code_as_unknown', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) {
        return json(200, {
          artifact_id: artifactId,
          state: 'rejected',
          bytes: null,
          scan_state: null,
          failure_code: null,
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
    expect(error.code).toBe('artifact_rejected');
    expect(error.message).toContain('rejected at complete: unknown');
    expect(error.message).not.toContain('invalid_failure_code');
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
    expect(await resume.findResumable(journalIdentity(sha256, 11))).toMatchObject({
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
    expect(error.hint).toContain(
      `uploads/${journalFileName(journalIdentity(sha256, 11), artifactId)}`,
    );
    expect((await resume.findResumable(journalIdentity(sha256, 11)))?.complete_done).toBe(false);
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
    const stored = await resume.findResumable(journalIdentity(sha256, 11));
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
    expect((await resume.findResumable(journalIdentity(sha256, 11)))?.complete_done).toBe(true);
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

  // F5: `events send` holds spool.lock for the whole send, so a stored upload must not turn
  // into a failure when the journal cannot record it: `complete` is idempotent and the next
  // run replays it.
  it('does_not_fail_a_stored_upload_when_the_journal_lock_stays_busy', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const directory = await mkdtemp(join(tmpdir(), 'evidgate-upload-busy-'));
    dirs.push(directory);
    const clock = { now: 1_000 };
    let locks = 0;
    // The first write (the row itself) gets the lock; every later one — put_done and
    // complete_done — is refused for longer than the bounded wait.
    const busy = await UploadJournal.open({
      producerDirectory: directory,
      withLock: <T>(fn: () => Promise<T>): Promise<T> => {
        locks += 1;
        if (locks === 1) return fn();
        return Promise.reject(new SpoolLockedError(join(directory, 'spool.lock'), 4_242));
      },
      now: () => clock.now,
      sleep: (milliseconds) => {
        clock.now += milliseconds;
        return Promise.resolve();
      },
      deadlineAt: 1_000 + 1_800_000,
    });
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) return json(200, readyBody(11));
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    const outcome = await uploadFile(request({ opened: fileHandle, sha256 }, store, busy));

    expect(outcome.state).toBe('ready');
    expect(outcome.bytes).toBe(11);
    expect(locks).toBeGreaterThan(1);
    expect(store.calls.filter((call) => call.url.endsWith('/complete'))).toHaveLength(1);
    // N1: the wait does not run to the 30 min deadline — the two best-effort updates stop at
    // the short default budget, so a busy `events send` costs seconds, not the whole upload.
    expect(clock.now - 1_000).toBeLessThan(10_000);
  });

  // N1 (P2): the best-effort budget is injectable, and it is per call: each of the two
  // post-transfer updates gives up inside it, so `complete` still runs with the deadline
  // almost untouched. Before the fix the wait ran until `deadlineAt`.
  it('gives_the_best_effort_journal_updates_a_short_lock_budget', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const directory = await mkdtemp(join(tmpdir(), 'evidgate-upload-budget-'));
    dirs.push(directory);
    const clock = { now: 1_000 };
    let locks = 0;
    const busy = await UploadJournal.open({
      producerDirectory: directory,
      withLock: <T>(fn: () => Promise<T>): Promise<T> => {
        locks += 1;
        if (locks === 1) return fn();
        return Promise.reject(new SpoolLockedError(join(directory, 'spool.lock'), 4_242));
      },
      now: () => clock.now,
      sleep: (milliseconds) => {
        clock.now += milliseconds;
        return Promise.resolve();
      },
      deadlineAt: 1_000 + 1_800_000,
    });
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) return json(200, readyBody(11));
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    const outcome = await uploadFile(
      request(
        { opened: fileHandle, sha256 },
        { deps: { ...store.deps, journalBestEffortWaitMs: 250 } },
        busy,
      ),
    );

    expect(outcome.state).toBe('ready');
    expect(store.calls.filter((call) => call.url.endsWith('/complete'))).toHaveLength(1);
    // put_done and complete_done each stop inside the injected 250 ms budget.
    expect(clock.now - 1_000).toBeGreaterThan(0);
    expect(clock.now - 1_000).toBeLessThan(500);
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
    const written = await resume.findResumable(journalIdentity(sha256, 11));
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
    expect(names).toEqual([journalFileName(journalIdentity(sha256, 11), artifactId)]);
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
      kind: 'trace',
      execution_key: executionKey,
      attempt_no: 1,
      media_type: 'application/zip',
      put_done: true,
      complete_done: true,
      mint_no: 0,
    });
  });

  // F1 (P1): the journal is keyed by the whole upload identity, not by (digest, size). The
  // same bytes under another execution, kind or attempt are a different server row, so the
  // command must call `upload-url` instead of replaying the other row's journal record.
  it.each([
    ['execution_key', { executionKey: 'b4'.repeat(32) }],
    ['attempt_no', { attemptNo: 2 }],
    ['kind', { kind: 'raw_report' as const, mediaType: 'application/xml' }],
    ['media_type', { mediaType: 'application/octet-stream' }],
  ])('starts_a_new_upload_for_another_%s', async (_field, overrides) => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const resume = await journal();
    // A real server mints a different artifact row for a different identity (the key
    // differs), so the responder does too.
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) {
        const completed = call.url.slice(
          call.url.lastIndexOf('/artifacts/') + '/artifacts/'.length,
          -'/complete'.length,
        );
        return json(200, readyBody(11, completed));
      }
      const nth = store.calls.filter((entry) => entry.url.endsWith('/upload-url')).length;
      return json(
        201,
        uploadUrlBody({
          bytes: Buffer.from('trace-bytes'),
          sha256,
          artifact: nth === 1 ? artifactId : secondArtifactId,
        }),
      );
    });

    // The first run commits a `complete_done` row for this file (no request on a re-run).
    await uploadFile(request({ opened: fileHandle, sha256 }, store, resume));
    const before = store.calls.length;
    const other = request({ opened: fileHandle, sha256 }, store, resume, overrides);
    await uploadFile(other);

    const urls = store.calls
      .slice(before)
      .filter((call) => call.url.endsWith('/artifacts/upload-url'));
    expect(urls).toHaveLength(1);
    expect(urls[0]?.headers.get('idempotency-key')).toBe(
      deriveUploadUrlIdempotencyKey({
        runId,
        producerId,
        executionKey: other.executionKey,
        attemptNo: other.attemptNo,
        kind: other.kind,
        fileSha256: sha256,
        mintNo: 0,
      }),
    );
  });

  // F3 (P2): `ready` must prove the stored object is the file that was signed. openapi
  // lets `bytes` be null, so a null (or missing) count is a response with no evidence
  // behind it — never defaulted to the local size.
  it.each([
    ['null', { artifact_id: artifactId, state: 'ready', bytes: null, scan_state: 'pending' }],
    ['missing', { artifact_id: artifactId, state: 'ready', scan_state: 'pending' }],
  ])('refuses_a_ready_complete_whose_bytes_is_%s', async (_case, complete) => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) return json(200, complete);
      return json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
    });

    let thrown: unknown;
    try {
      await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));
    } catch (error) {
      thrown = error;
    }
    expect(thrown).toBeInstanceOf(UploadFailedError);
    expect((thrown as UploadFailedError).code).toBe('request_failed');
    expect((thrown as Error).message).toContain(artifactId);
  });

  // F4 (P2, TOCTOU): the PUT streams a descriptor re-opened through the guard, so a file
  // replaced (or symlinked over) between the hash pass and the PUT is refused before a byte
  // leaves — never uploaded from the new path, never completed, never retried.
  it('refuses_a_file_replaced_between_the_passes', async () => {
    const { opened: fileHandle, sha256, path } = await file('trace.zip', 'first-content');
    const replacement = join(root, 'replacement.zip');
    await writeFile(replacement, 'first-content');
    await rename(replacement, path);
    const store = harness((call) =>
      call.method === 'PUT'
        ? new Response(null, { status: 200 })
        : json(201, uploadUrlBody({ bytes: Buffer.from('first-content'), sha256 })),
    );

    // The timer seam arms nothing real: the refusal happens before either PUT timer exists,
    // and a deadline timer armed before the re-open would outlive the refusal and keep the
    // CLI process alive for the rest of the upload budget.
    const armed: number[] = [];
    const cleared: unknown[] = [];
    const timers = {
      set: (milliseconds: number): unknown => {
        armed.push(milliseconds);
        return armed.length;
      },
      clear: (handle: unknown): void => {
        cleared.push(handle);
      },
    };

    await expect(
      uploadFile(
        request({ opened: fileHandle, sha256 }, { deps: { ...store.deps, timers } }, undefined),
      ),
    ).rejects.toMatchObject({ code: 'upload_path_refused' });
    expect(store.calls.filter((call) => call.method === 'PUT')).toHaveLength(0);
    expect(store.calls.some((call) => call.url.endsWith('/complete'))).toBe(false);
    expect(store.sleeps).toEqual([]);
    expect(armed).toEqual([]);
    expect(cleared).toEqual([]);
  });

  it('refuses_a_symlink_swapped_in_between_the_passes', async () => {
    const { opened: fileHandle, sha256, path } = await file('trace.zip', 'first-content');
    const elsewhere = join(root, 'elsewhere.zip');
    await writeFile(elsewhere, 'first-content');
    await rm(path);
    await symlink(elsewhere, path);
    const store = harness((call) =>
      call.method === 'PUT'
        ? new Response(null, { status: 200 })
        : json(201, uploadUrlBody({ bytes: Buffer.from('first-content'), sha256 })),
    );

    await expect(
      uploadFile(request({ opened: fileHandle, sha256 }, store, undefined)),
    ).rejects.toMatchObject({ code: 'upload_path_refused' });
    expect(store.calls.filter((call) => call.method === 'PUT')).toHaveLength(0);
    expect(store.sleeps).toEqual([]);
  });

  // F7 (P3): a 201 must not be able to make the CLI send a credential, retarget the host or
  // add a second body framing. The refusal names the header, never its value.
  it.each([
    'authorization',
    'Authorization',
    'cookie',
    'set-cookie',
    'host',
    'proxy-authorization',
    'transfer-encoding',
    'connection',
    // N5: the transport-owned set. `fetch` throws on most of these, and a thrown TypeError
    // reads as a transport failure — it would be retried until the deadline.
    'expect',
    'te',
    'trailer',
    'upgrade',
    'keep-alive',
    'content-encoding',
  ])('refuses_a_signed_%s_header', async (name) => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const body = uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 });
    const signed = body['required_headers'] as Record<string, string>;
    signed[name] = 'MARKER-VALUE-7';
    const store = harness(() => json(201, body));

    let thrown: unknown;
    try {
      await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));
    } catch (error) {
      thrown = error;
    }
    const error = thrown as UploadFailedError;
    expect(error.code).toBe('request_failed');
    expect(error.message).toContain(name.toLowerCase());
    expect(error.message).not.toContain('MARKER-VALUE-7');
    expect(store.calls.some((call) => call.method === 'PUT')).toBe(false);
    expect(store.sleeps).toEqual([]);
  });

  // N5 (P3): a signed name `fetch` would refuse (a space, a colon, a non-token byte) is a
  // malformed response, not a transport failure. The name is not a token, so it is never
  // echoed — the message names the rule, not the attacker's text.
  it('refuses_a_signed_header_whose_name_is_not_a_token', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const body = uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 });
    const signed = body['required_headers'] as Record<string, string>;
    signed['x-amz-bad name'] = 'MARKER-VALUE-7';
    const store = harness(() => json(201, body));

    let thrown: unknown;
    try {
      await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));
    } catch (error) {
      thrown = error;
    }
    const error = thrown as UploadFailedError;
    expect(error.code).toBe('request_failed');
    expect(error.message).toContain('not an HTTP token');
    expect(error.message).not.toContain('x-amz-bad name');
    expect(error.message).not.toContain('MARKER-VALUE-7');
    expect(store.calls.some((call) => call.method === 'PUT')).toBe(false);
    expect(store.sleeps).toEqual([]);
  });

  // N5: a value `fetch` itself would refuse is refused here first, so it stays a
  // deterministic `request_failed` instead of a TypeError the retry loop repeats: CR/LF (a
  // smuggled second field), NUL, ESC (a terminal escape), DEL and a code point above Latin-1.
  it.each([
    ['a CRLF', 'ok\r\nX-Injected: 1'],
    ['a NUL', 'ok\u0000tail'],
    ['an ESC', 'ok\u001b[31mred'],
    ['a DEL', 'ok\u007ftail'],
    ['a non-Latin-1 code point', 'ok\u20ac'],
  ])('refuses_a_signed_header_value_with_%s', async (_case, value) => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const body = uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 });
    const signed = body['required_headers'] as Record<string, string>;
    signed['x-amz-meta-note'] = value;
    const store = harness(() => json(201, body));

    let thrown: unknown;
    try {
      await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));
    } catch (error) {
      thrown = error;
    }
    const error = thrown as UploadFailedError;
    expect(error.code).toBe('request_failed');
    expect(error.message).toContain('x-amz-meta-note');
    expect(error.message).not.toContain('X-Injected');
    expect(store.calls.some((call) => call.method === 'PUT')).toBe(false);
    expect(store.sleeps).toEqual([]);
  });

  // The class is the one `fetch` accepts, not a blanket ASCII filter: tab and a Latin-1 byte
  // pass through untouched.
  it('accepts_a_signed_header_value_inside_the_fetch_class', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const body = uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 });
    const signed = body['required_headers'] as Record<string, string>;
    signed['x-amz-meta-note'] = 'a\tb\u00e9';
    const store = harness((call) => {
      if (call.method === 'PUT') return new Response(null, { status: 200 });
      if (call.url.endsWith('/complete')) return json(200, readyBody(11));
      return json(201, body);
    });

    await uploadFile(request({ opened: fileHandle, sha256 }, store, undefined));

    const put = store.calls.find((call) => call.method === 'PUT');
    expect(put?.headers.get('x-amz-meta-note')).toBe('a\tb\u00e9');
  });

  // F8 (P3): a malformed artifact id is a malformed response, not a corrupt journal: the
  // strict UUID check refuses it before anything is written.
  it('refuses_a_non_uuid_artifact_id_as_a_bad_response', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const resume = await journal();
    const store = harness(() =>
      json(
        201,
        uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256, artifact: '-'.repeat(36) }),
      ),
    );

    let thrown: unknown;
    try {
      await uploadFile(request({ opened: fileHandle, sha256 }, store, resume));
    } catch (error) {
      thrown = error;
    }
    expect((thrown as UploadFailedError).code).toBe('request_failed');
    expect(await readdir(resume.uploadsDirectory)).toEqual([]);
  });

  // F2 (P1): the presigned PUT is bounded by the remaining upload deadline only. It used to
  // carry the 30 s per-request timeout of the JSON calls, which aborts a trace that takes
  // longer than half a minute to send. Time comes from the injected seam, so nothing races
  // the real file read: the spec advances the clock and fires what is due itself.
  it('does_not_abort_a_put_that_takes_longer_than_thirty_seconds', async () => {
    const content = Buffer.from('trace-bytes');
    const { opened: fileHandle, sha256 } = await file('trace.zip', content);
    const clock = { now: 1_000 };
    const { timers, scheduled, fireDue } = virtualTimers(clock);
    const signals: AbortSignal[] = [];
    const deps: UploadDeps = {
      timers,
      sleep: (milliseconds) => {
        clock.now += milliseconds;
        return Promise.resolve();
      },
      now: () => clock.now,
      random: () => 0.5,
      fetch: async (input, init) => {
        const url = fetchUrl(input);
        if (fetchMethod(input, init) !== 'PUT') {
          return url.endsWith('/complete')
            ? json(200, readyBody(content.byteLength))
            : json(201, uploadUrlBody({ bytes: content, sha256 }));
        }
        const request: RequestInit = init ?? {};
        const signal = request.signal as AbortSignal;
        signals.push(signal);
        // 45 simulated seconds per chunk: past the 30 s per-request cap of the API calls.
        for await (const chunk of request.body as AsyncIterable<Uint8Array>) {
          clock.now += 45_000;
          fireDue();
          if (signal.aborted) throw signal.reason;
          if (chunk.byteLength === 0) break;
        }
        return new Response(null, { status: 200 });
      },
    };

    const outcome = await uploadFile(request({ opened: fileHandle, sha256 }, { deps }, undefined));

    expect(outcome).toMatchObject({ state: 'ready', bytes: content.byteLength });
    expect(signals).toHaveLength(1);
    expect(signals[0]?.aborted).toBe(false);
    // Both timers came from the seam and both were cleared: nothing outlives the PUT.
    expect(scheduled.size).toBe(0);
  });

  it('aborts_the_put_at_the_upload_deadline', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const clock = { now: 1_000 };
    const { timers, scheduled, fireDue } = virtualTimers(clock);
    let abortedAtResponse: boolean | undefined;
    const deps: UploadDeps = {
      timers,
      sleep: (milliseconds) => {
        clock.now += milliseconds;
        return Promise.resolve();
      },
      now: () => clock.now,
      random: () => 0.5,
      fetch: async (input, init) => {
        const url = fetchUrl(input);
        if (fetchMethod(input, init) !== 'PUT') {
          return url.endsWith('/complete')
            ? json(200, readyBody(11))
            : json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
        }
        const request: RequestInit = init ?? {};
        for await (const chunk of request.body as AsyncIterable<Uint8Array>) {
          // the bytes leave; the store never answers
          if (chunk.byteLength === 0) break;
        }
        // Only the upload deadline can end this request, and the seam is its only clock.
        clock.now += 5_000;
        fireDue();
        const signal = request.signal as AbortSignal;
        abortedAtResponse = signal.aborted;
        if (signal.aborted) throw signal.reason;
        return new Response(null, { status: 200 });
      },
    };

    await expect(
      uploadFile(
        request({ opened: fileHandle, sha256 }, { deps }, undefined, { deadlineMs: 5_000 }),
      ),
    ).rejects.toMatchObject({ code: 'deadline_exceeded' });

    expect(abortedAtResponse).toBe(true);
    expect(scheduled.size).toBe(0);
  });

  // N3 (P3): a store that answers 2xx before it has read the body and then drops the request.
  // The body generator never started, so no `finally` runs — only the stream's `close` can
  // settle `done`. The attempt must end as the abort (retried), never as a hang, and the
  // cleared timers must leave nothing armed.
  it('does_not_hang_when_the_store_answers_before_the_body_is_sent', async () => {
    const content = Buffer.from('trace-bytes');
    const { opened: fileHandle, sha256 } = await file('trace.zip', content);
    const clock = { now: 1_000 };
    const { timers, scheduled, fireDue } = virtualTimers(clock);
    let puts = 0;
    const deps: UploadDeps = {
      timers,
      sleep: (milliseconds) => {
        clock.now += milliseconds;
        return Promise.resolve();
      },
      now: () => clock.now,
      random: () => 0.5,
      fetch: async (input, init) => {
        const url = fetchUrl(input);
        if (fetchMethod(input, init) !== 'PUT') {
          return url.endsWith('/complete')
            ? json(200, readyBody(content.byteLength))
            : json(201, uploadUrlBody({ bytes: content, sha256 }));
        }
        puts += 1;
        const request: RequestInit = init ?? {};
        if (puts === 1) {
          // An early 2xx: the store answers before a single byte and closes the connection,
          // which destroys the request body it never read. `putOnce` hands this fake the
          // Node Readable it built, so the cast names the real shape rather than BodyInit.
          const body = request.body as unknown as { destroy(): void };
          body.destroy();
          // The idle timer then aborts the attempt: the body can never make progress.
          clock.now += putStallTimeoutMs;
          fireDue();
          return new Response(null, { status: 200 });
        }
        for await (const chunk of request.body as AsyncIterable<Uint8Array>) {
          if (chunk.byteLength === 0) break;
        }
        return new Response(null, { status: 200 });
      },
    };

    const outcome = await uploadFile(request({ opened: fileHandle, sha256 }, { deps }, undefined));

    expect(outcome.state).toBe('ready');
    expect(puts).toBe(2);
    // Neither attempt left a timer armed: nothing can keep the process alive.
    expect(scheduled.size).toBe(0);
  });

  // N4 (P3): the idle timer guards the SENDING of the body only. A store that needs longer
  // than the idle budget to verify a large checksum after the last byte is not a stall, and
  // the deadline (30 min here) is what bounds the wait for its answer.
  it('waits_for_a_store_that_answers_long_after_the_last_byte', async () => {
    const content = Buffer.from('trace-bytes');
    const { opened: fileHandle, sha256 } = await file('trace.zip', content);
    const clock = { now: 1_000 };
    const { timers, scheduled, fireDue } = virtualTimers(clock);
    let abortedAfterBody: boolean | undefined;
    const deps: UploadDeps = {
      timers,
      sleep: (milliseconds) => {
        clock.now += milliseconds;
        return Promise.resolve();
      },
      now: () => clock.now,
      random: () => 0.5,
      fetch: async (input, init) => {
        const url = fetchUrl(input);
        if (fetchMethod(input, init) !== 'PUT') {
          return url.endsWith('/complete')
            ? json(200, readyBody(content.byteLength))
            : json(201, uploadUrlBody({ bytes: content, sha256 }));
        }
        const request: RequestInit = init ?? {};
        for await (const chunk of request.body as AsyncIterable<Uint8Array>) {
          if (chunk.byteLength === 0) break;
        }
        // Every byte is in; the store verifies the checksum for 90 s before it answers.
        clock.now += 90_000;
        fireDue();
        abortedAfterBody = (request.signal as AbortSignal).aborted;
        return new Response(null, { status: 200 });
      },
    };

    const outcome = await uploadFile(request({ opened: fileHandle, sha256 }, { deps }, undefined));

    expect(outcome).toMatchObject({ state: 'ready', bytes: content.byteLength });
    expect(abortedAfterBody).toBe(false);
    expect(scheduled.size).toBe(0);
  });

  // F2: a connection that stops moving is caught by the idle timeout (which resets on every
  // body chunk) and retried, so a stalled store never eats the rest of the deadline.
  it('retries_a_put_that_stops_making_progress', async () => {
    const { opened: fileHandle, sha256 } = await file('trace.zip');
    const clock = { now: 1_000 };
    const scheduled = new Map<number, { readonly ms: number; readonly fn: () => void }>();
    let nextTimer = 1;
    const timers: UploadTimers = {
      set: (milliseconds, fn) => {
        const id = nextTimer;
        nextTimer += 1;
        scheduled.set(id, { ms: milliseconds, fn });
        return id;
      },
      clear: (handle) => {
        scheduled.delete(handle as number);
      },
    };
    let puts = 0;
    const deps: UploadDeps = {
      timers,
      sleep: (milliseconds) => {
        clock.now += milliseconds;
        return Promise.resolve();
      },
      now: () => clock.now,
      random: () => 0.5,
      fetch: async (input, init) => {
        const url = fetchUrl(input);
        if (fetchMethod(input, init) !== 'PUT') {
          return url.endsWith('/complete')
            ? json(200, readyBody(11))
            : json(201, uploadUrlBody({ bytes: Buffer.from('trace-bytes'), sha256 }));
        }
        puts += 1;
        const request: RequestInit = init ?? {};
        const signal = request.signal as AbortSignal;
        if (puts === 1) {
          // The connection stalls: no body progress, no response — only the idle timeout
          // can end this attempt.
          await aborted(signal);
        }
        for await (const chunk of request.body as AsyncIterable<Uint8Array>) {
          // drain the retried body so `body.done` settles
          if (chunk.byteLength === 0) break;
        }
        return new Response(null, { status: 200 });
      },
    };

    const pending = uploadFile(request({ opened: fileHandle, sha256 }, { deps }, undefined));
    await vi.waitFor(() => {
      expect([...scheduled.values()].map((entry) => entry.ms)).toContain(putStallTimeoutMs);
    });
    for (const [id, entry] of [...scheduled]) {
      if (entry.ms === putStallTimeoutMs) {
        scheduled.delete(id);
        entry.fn();
      }
    }

    await expect(pending).resolves.toMatchObject({ state: 'ready' });
    expect(puts).toBe(2);
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
