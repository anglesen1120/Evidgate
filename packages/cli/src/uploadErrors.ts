// The error shapes one upload can end in. `UploadFailedError` carries the HTTP status so a
// 401 can purge the token cache; each sentinel tells the retry loops whether the same
// request may be sent again at all.
import { isErrorEnvelope } from '@evidgate/sdk';

import { CliError } from './errors.js';

/** A `CliError` that also carries the HTTP status, so 401 can purge the token cache. */
export class UploadFailedError extends CliError {
  readonly status: number | undefined;

  constructor(
    message: string,
    options: ConstructorParameters<typeof CliError>[1],
    status?: number,
  ) {
    super(message, options);
    this.status = status;
  }
}

export const checksumMismatchCode = 'upload_checksum_mismatch';

/** The file changed under us: abort, never complete, never retry. */
export class ChecksumMismatchError extends CliError {
  constructor(message: string) {
    super(`the artifact upload was aborted: ${message}`, {
      code: checksumMismatchCode,
      hint: 'the file was modified while it was uploaded; stop writing it and re-run the command',
    });
  }
}

export function checksumMismatch(message: string): ChecksumMismatchError {
  return new ChecksumMismatchError(message);
}

export function toCliError(error: unknown): Error {
  if (error instanceof CliError) {
    return error;
  }
  if (error instanceof Error) {
    return error;
  }
  return new Error('the upload body failed');
}

export function badResponse(message: string): UploadFailedError {
  return new UploadFailedError(message, { code: 'request_failed' });
}

export const authHint = 'run `evidgate auth login` to refresh the cached CI token';

/** 401 means the cached token is dead: surface the server code with the refresh hint. */
export function withAuthHint(error: UploadFailedError, status: number): UploadFailedError {
  if (status !== 401) {
    return error;
  }
  return new UploadFailedError(error.message, { ...errorOptions(error), hint: authHint }, status);
}

/** Wraps a mapped server error with its HTTP status so a caller can act on 401. */
export function withStatus(error: CliError, status: number): UploadFailedError {
  if (error instanceof UploadFailedError) {
    return error;
  }
  return new UploadFailedError(error.message, errorOptions(error), status);
}

function errorOptions(error: CliError): ConstructorParameters<typeof CliError>[1] {
  return {
    code: error.code,
    ...(error.hint === undefined ? {} : { hint: error.hint }),
    ...(error.requestId === undefined ? {} : { requestId: error.requestId }),
    ...(error.details === undefined ? {} : { details: error.details }),
  };
}

/** One line for `deadline_exceeded`: the code, status and request id, never a URL. */
export function describeUploadError(error: unknown): string {
  if (error instanceof UploadFailedError) {
    const status = error.status === undefined ? '' : ` (HTTP ${error.status})`;
    const requestId = error.requestId === undefined ? '' : ` request_id=${error.requestId}`;
    return `${error.code}${status}${requestId}`;
  }
  if (error instanceof CliError) {
    return error.code;
  }
  if (isErrorEnvelope(error)) {
    return error.error.code;
  }
  return 'transport_failed';
}
