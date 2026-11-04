// The happy path of one attachment: the token exchange, then `upload-url`, the presigned PUT
// and `complete`, in that order, with the API bearer never crossing to the store.
import { describe, expect, it } from 'vitest';

import {
  acceptingStore,
  artifactId,
  digestOfFile,
  executionKey,
  input,
  presignedUrl,
  producerId,
  runId,
  useAttachmentUploadSpec,
} from './attachmentUpload.testing.js';
import { devToken, mintedToken } from './commands/devExchange.testing.js';

describe('createAttachmentUploader', () => {
  const { attachment, uploader } = useAttachmentUploadSpec();

  // AC5: one attachment in, one artifact id out — the token exchange, then upload-url, the
  // presigned PUT and complete, in that order, with the API bearer never crossing to the store.
  it('uploads_one_attachment_and_returns_the_artifact_id', async () => {
    const { path, content } = await attachment('trace.zip');
    const digest = await digestOfFile(path);
    const store = acceptingStore(digest);

    const result = await uploader(store).upload(input(path));

    expect(result).toEqual({ artifactId, bytes: content.byteLength });
    expect(store.calls.map((call) => call.method)).toEqual(['POST', 'POST', 'PUT', 'POST']);

    const [exchange, mint, put, complete] = store.calls;
    // The dev credential is presented as an ingest token; the bearer is what it mints.
    expect(exchange === undefined ? '(no call)' : new URL(exchange.url).pathname).toBe(
      '/v1/ci/token',
    );
    expect(exchange?.json).toEqual({ ingest_token: devToken });
    expect(mint === undefined ? '(no call)' : new URL(mint.url).pathname).toBe(
      '/v1/artifacts/upload-url',
    );
    expect(mint?.headers.get('authorization')).toBe(`Bearer ${mintedToken}`);
    expect(mint?.json).toEqual({
      run_id: runId,
      producer_id: producerId,
      execution_key: executionKey,
      attempt_no: 1,
      kind: 'trace',
      media_type: 'application/zip',
      bytes: content.byteLength,
      sha256: digest.sha256,
    });
    // The presigned URL is the destination, and its signature is the ONLY credential on
    // the wire: the bearer never leaves the API host (the secrets rule).
    expect(put?.url).toBe(presignedUrl);
    expect(put?.headers.get('authorization')).toBeNull();
    expect(put?.headers.get('x-amz-checksum-sha256')).toBe(
      Buffer.from(digest.sha256.slice('sha256:'.length), 'hex').toString('base64'),
    );
    expect(put?.body.equals(content)).toBe(true);
    // Never buffered whole by the client: the body crossed in bounded chunks.
    expect(put?.chunks.length).toBeGreaterThan(0);
    expect(put?.chunks.every((size) => size <= 64 * 1024)).toBe(true);
    expect(complete?.url.endsWith(`/v1/artifacts/${artifactId}/complete`)).toBe(true);
    expect(complete?.headers.get('authorization')).toBe(`Bearer ${mintedToken}`);
  });
});
