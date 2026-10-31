// The deterministic keys and the local digest: both are pure functions of the upload's own
// inputs, so a retry replays the same server row instead of minting a second one.
import { createHash } from 'node:crypto';

import { checksumMismatch } from './uploadErrors.js';
import { uploadChunkBytes } from './uploadOptions.js';

import type { ArtifactKind, UploadFile } from './uploadPaths.js';

/** The digest form the server's `sha256` body member and the journal use. */
export const checksumPattern = /^sha256:[0-9a-f]{64}$/;

/** Inputs of the `upload-url` key: exactly the values the request body is built from. */
export interface UploadUrlKeyInput {
  readonly runId: string;
  readonly producerId: string;
  readonly executionKey: string;
  readonly attemptNo: number;
  readonly kind: ArtifactKind;
  /** `sha256:<64 hex>` — the local digest, not the base64 form the header uses. */
  readonly fileSha256: string;
  /** The mint number: `0` is the first key, `n` a deterministic renewal (see the module doc). */
  readonly mintNo: number;
}

/**
 * The `upload-url` `Idempotency-Key`. The pre-image is length-prefixed per field, not a
 * bare concatenation, because `ab|c` and `a|bc` would otherwise hash the same; `mint_no` is
 * appended only for a renewal, so the first key is exactly the six-field digest.
 */
export function deriveUploadUrlIdempotencyKey(input: UploadUrlKeyInput): string {
  const fields = [
    input.runId,
    input.producerId,
    input.executionKey,
    String(input.attemptNo),
    input.kind,
    input.fileSha256,
  ];
  let preImage = fields.map((field) => `${Buffer.byteLength(field, 'utf8')}:${field}`).join('');
  if (input.mintNo > 0) {
    const mint = String(input.mintNo);
    preImage += `${Buffer.byteLength(mint, 'utf8')}:${mint}`;
  }
  return createHash('sha256').update(preImage, 'utf8').digest('hex');
}

/** Inputs of the derived `complete` key. */
export interface CompleteKeyInput {
  readonly artifactId: string;
  readonly fileSha256: string;
  readonly size: number;
}

/**
 * The `complete` `Idempotency-Key`: the lowercase hex SHA-256 of the UTF-8 JSON array
 * `['evidgate.artifact-complete-key.v1', artifact_id, file_sha256, size]`. A JSON array is
 * unambiguous and versioned, so a resumed `complete` is the same request while a different
 * artifact never reuses the key.
 */
export function deriveCompleteIdempotencyKey(input: CompleteKeyInput): string {
  return createHash('sha256')
    .update(
      JSON.stringify([
        'evidgate.artifact-complete-key.v1',
        input.artifactId,
        input.fileSha256,
        input.size,
      ]),
      'utf8',
    )
    .digest('hex');
}

/**
 * Pass 1: hashes the open descriptor in bounded chunks and requires the byte count to equal
 * the `fstat` size. Never buffers the file; a file that changes length while it is hashed is
 * `upload_checksum_mismatch` rather than a digest for bytes nobody will upload.
 */
export async function hashUploadFile(file: UploadFile): Promise<string> {
  const hash = createHash('sha256');
  let bytes = 0;
  // The descriptor, not the path: this pins the exact inode the guard `fstat`ed, so a
  // symlink swapped in between `realpath` and the open cannot be hashed instead.
  const stream = file.handle.createReadStream({
    start: 0,
    autoClose: false,
    highWaterMark: uploadChunkBytes,
  });
  for await (const chunk of stream) {
    const bytesChunk = chunk as Uint8Array;
    hash.update(bytesChunk);
    bytes += bytesChunk.byteLength;
  }
  if (bytes !== file.size) {
    throw checksumMismatch(
      `the file changed while it was hashed (${file.size} bytes expected, ${bytes} read)`,
    );
  }
  return `sha256:${hash.digest('hex')}`;
}
