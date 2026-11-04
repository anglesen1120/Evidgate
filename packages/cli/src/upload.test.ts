// `uploadFile()` against the scripted store: the `upload-url` mint and its deterministic
// idempotency keys, the presigned PUT (exactly the signed headers, the body streamed and
// re-hashed), the retries that keep a single artifact row, and what a bad mint answer or an
// expired token earns. The shared harness advances a virtual clock, so no case waits on one.
import { createHash } from 'node:crypto';
import { readFile, writeFile } from 'node:fs/promises';

import { describe, expect, it } from 'vitest';

import { CliError } from './errors.js';
import {
  ChecksumMismatchError,
  UploadFailedError,
  deriveUploadUrlIdempotencyKey,
  maxMintAttempts,
  uploadChunkBytes,
  uploadFile,
} from './upload.js';
import {
  artifactId,
  executionKey,
  harness,
  json,
  producerId,
  readyBody,
  runId,
  secondArtifactId,
  uploadUrlBody,
  uploadUrlCall,
  useUploadSpec,
} from './upload.testing.js';

describe('uploadFile', () => {
  const { file, request } = useUploadSpec();

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
});
