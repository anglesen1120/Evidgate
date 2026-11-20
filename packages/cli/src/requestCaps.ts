// The one table of request-body caps. Every body the SDK client sends is measured here
// before a byte leaves the process; the presigned artifact PUT does not go through the client
// and is bounded by its own streaming counter (`uploadBody.ts`) and the per-kind caps.
// Each cap sits at or under the limit the server enforces for the same route, so an
// over-cap body is refused locally instead of costing a request the server would answer 413.
import { CliError } from './errors.js';
import { maxBatchBytes } from './batchLimits.js';

/** The server's published manifest body cap (`IngestionLimits.DefaultMaxManifestBytes`, 32 MiB). */
export const maxManifestBytes = 32 * 1024 * 1024;
/** `POST /projects/{p}/runs`: the server's 64 KiB run-create body cap. */
export const maxRunCreateBytes = 64 * 1024;
/** `POST /runs/{r}/finalize`: the server's 8 KiB finalize body cap. */
export const maxFinalizeBytes = 8 * 1024;
/** `POST /ci/token`: the server's 4096-byte exchange body cap. */
export const maxTokenExchangeBytes = 4096;
/** `POST /artifacts/upload-url`: the server's 8 KiB upload-url body cap. */
export const maxUploadUrlBytes = 8 * 1024;

/** Cap per `METHOD path-template` of the generated operations; a body route missing here is refused. */
export const requestBodyCaps: Readonly<Record<string, number>> = {
  'POST /runs/{r}/events': maxBatchBytes,
  'POST /projects/{p}/manifests': maxManifestBytes,
  'POST /projects/{p}/runs': maxRunCreateBytes,
  'POST /runs/{r}/finalize': maxFinalizeBytes,
  'POST /ci/token': maxTokenExchangeBytes,
  'POST /artifacts/upload-url': maxUploadUrlBytes,
};

/** The serialized body the SDK is about to send, as the request interceptor sees it. */
export type SerializedBody = unknown;

/**
 * The body the client puts on the wire, chosen exactly as the generated client does: the
 * serialized form when a serializer ran, otherwise the raw `body` (a manifest is sent with
 * `bodySerializer: null`, so its `serializedBody` is empty). A serializer that produced nothing
 * leaves the request bodyless, so the result is `undefined` and never a zero-length body.
 */
export function wireBody(options: {
  readonly body?: unknown;
  readonly bodySerializer?: unknown;
  readonly serializedBody?: unknown;
}): SerializedBody {
  if (options.body === undefined) {
    return undefined;
  }
  if (!options.bodySerializer) {
    return options.body;
  }
  const serialized = options.serializedBody;
  return serialized === undefined || serialized === '' ? undefined : serialized;
}

/**
 * Byte length without a copy: `Buffer.byteLength` counts a string's UTF-8 form in place and a
 * typed array reports its own `byteLength`, so a 32 MiB manifest is never duplicated. Absent
 * bodies are answered before this runs; a body of unmeasurable length answers `undefined`.
 */
function bodyBytes(body: SerializedBody): number | undefined {
  if (typeof body === 'string') {
    return Buffer.byteLength(body, 'utf8');
  }
  if (ArrayBuffer.isView(body)) {
    return body.byteLength;
  }
  return undefined;
}

/**
 * Throws before the request is sent when the body is over the cap of its route. Only an absent
 * body (`undefined` or `null`) passes freely: a present body of any length, a body of
 * unmeasurable length, or a body on a route with no cap is measured and refused when it does
 * not fit. The message names the route template and the numbers only: never a body byte, a path
 * or a token.
 */
export function assertRequestBodyWithinCap(
  method: string | undefined,
  urlTemplate: string | undefined,
  body: SerializedBody,
): void {
  if (body === undefined || body === null) {
    return;
  }
  const size = bodyBytes(body);
  const route = `${(method ?? '').toUpperCase()} ${urlTemplate ?? ''}`;
  const cap = requestBodyCaps[route];
  if (cap === undefined || size === undefined) {
    throw new CliError(`no request body cap applies to ${route}`, { code: 'request_failed' });
  }
  if (size > cap) {
    throw new CliError(`the request body for ${route} is ${size} bytes, over its ${cap}-byte cap`, {
      code: 'request_too_large',
      hint: 'this request was not sent',
    });
  }
}
