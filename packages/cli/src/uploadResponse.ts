// Nothing from a 200 is trusted until it is the documented shape: the volatile fields of
// `upload-url` and `complete` are validated here, and a body that is not that shape is
// `request_failed` rather than a URL or a byte count the rest of the flow would act on.
import type { CompleteResponse, UploadUrlResponse } from '@evidgate/sdk';

import { badResponse } from './uploadErrors.js';
import { artifactIdPattern } from './uploadJournal.js';

/**
 * The server's volatile fields are validated before anything advances (never trust a 200):
 * a body that is not the documented shape is `request_failed`, and no URL from it is used.
 */
export function readUploadUrl(data: unknown): UploadUrlResponse {
  if (typeof data !== 'object' || data === null) {
    throw badResponse('the upload-url response has no body');
  }
  const body = data as Record<string, unknown>;
  const artifactId = body['artifact_id'];
  const objectKey = body['object_key'];
  const uploadUrl = body['upload_url'];
  const requiredHeaders = body['required_headers'];
  const expiresAt = body['expires_at'];
  if (typeof artifactId !== 'string' || !artifactIdPattern.test(artifactId)) {
    throw badResponse('the upload-url response carries no artifact id');
  }
  // `object_key` is required by the contract (openapi `UploadUrlResponse`), so a body
  // without one is refused exactly like a missing `artifact_id` — never defaulted to ''.
  if (typeof objectKey !== 'string' || objectKey.length === 0) {
    throw badResponse('the upload-url response carries no object key');
  }
  if (typeof uploadUrl !== 'string') {
    throw badResponse('the upload-url response carries no presigned URL');
  }
  // Required, `format: date-time`: an expiry the client cannot read is a malformed body,
  // not a URL with an unknown lifetime.
  if (typeof expiresAt !== 'string' || !Number.isFinite(Date.parse(expiresAt))) {
    throw badResponse('the upload-url response carries no parseable expiry');
  }
  if (typeof requiredHeaders !== 'object' || requiredHeaders === null) {
    throw badResponse('the upload-url response carries no signed headers');
  }
  const headers: Record<string, string> = {};
  for (const [name, value] of Object.entries(requiredHeaders as Record<string, unknown>)) {
    if (typeof value !== 'string') {
      throw badResponse('the upload-url response carries a non-string signed header');
    }
    headers[name] = value;
  }
  return {
    artifact_id: artifactId,
    object_key: objectKey,
    upload_url: uploadUrl,
    required_headers: headers,
    expires_at: expiresAt,
  };
}

/** The `complete` body: `state` decides success, `bytes` is verified against the file. */
export function readComplete(data: unknown, artifactId: string): CompleteResponse {
  if (typeof data !== 'object' || data === null) {
    throw badResponse('the complete response has no body');
  }
  const body = data as Record<string, unknown>;
  const state = body['state'];
  const reported = body['artifact_id'];
  if (state !== 'ready' && state !== 'rejected') {
    throw badResponse('the complete response carries no artifact state');
  }
  if (typeof reported !== 'string' || reported.toLowerCase() !== artifactId.toLowerCase()) {
    throw badResponse('the complete response names a different artifact');
  }
  const bytes = body['bytes'];
  if (bytes !== null && bytes !== undefined && !Number.isSafeInteger(bytes)) {
    throw badResponse('the complete response reports a non-integer byte count');
  }
  return {
    artifact_id: reported,
    state,
    bytes: typeof bytes === 'number' ? bytes : null,
    scan_state: typeof body['scan_state'] === 'string' ? body['scan_state'] : null,
    failure_code: typeof body['failure_code'] === 'string' ? body['failure_code'] : null,
  };
}

const failureCodeFallback = 'invalid_failure_code';

/**
 * The server's `failure_code`, as it may appear in a message. A `rejected` complete puts it
 * into the message, and the message is printed to stderr and copied by every `--json`
 * consumer, so only a plain token is echoed verbatim; anything else (an escape sequence that
 * would repaint the terminal, a newline that would forge an `evidgate:` line, an over-long
 * value) becomes {@link failureCodeFallback}. The machine-readable code therefore stays
 * stable, and the raw server text never reaches a stream. A missing code is
 * `unknown`, which is what the caller says, not this fallback.
 */
export function failureCodeLabel(raw: string): string {
  return /^[A-Za-z0-9_.-]{1,64}$/.test(raw) ? raw : failureCodeFallback;
}
