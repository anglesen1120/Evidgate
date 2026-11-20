// The opt-in switch for the binary path of the `HttpClient` fetch adapter. The caller names the
// transport per request; the adapter never infers it from a server `Content-Type`. The marker is
// removed before the request reaches `HttpClient`, so interceptors and the wire never see it.

import { HttpContext, HttpContextToken } from '@angular/common/http';

/** Request header a data service sets to choose the binary path for one call. */
export const BINARY_TRANSPORT_HEADER = 'X-Evidgate-Transport';

/** Send the request body as the exact bytes of its `Blob` (no UTF-8 decode). */
export const BINARY_REQUEST = 'binary-request';

/** Return the 2xx response body as the exact bytes the server sent (no text decode). */
export const BINARY_RESPONSE = 'binary-response';

/**
 * Hard ceiling on one binary body in either direction. The authoring routes cap their files at
 * 5 MiB; the extra headroom covers container overhead, and anything larger is refused locally.
 */
export const BINARY_BODY_LIMIT_BYTES = 8 * 1024 * 1024;

/** Header value for the given directions, e.g. `binaryTransport(BINARY_RESPONSE)`. */
export function binaryTransport(
  ...modes: readonly (typeof BINARY_REQUEST | typeof BINARY_RESPONSE)[]
): string {
  return modes.join(', ');
}

const CAPPED_BINARY_CALL = new HttpContextToken<boolean>(() => false);

/**
 * Request context the adapter sets on the calls it caps, so the telemetry interceptor measures
 * exactly those; `reportProgress` alone also belongs to callers that never opted in.
 */
export function cappedBinaryContext(): HttpContext {
  return new HttpContext().set(CAPPED_BINARY_CALL, true);
}

/** Whether the adapter capped this request (see {@link cappedBinaryContext}). */
export function isCappedBinaryCall(request: { readonly context: HttpContext }): boolean {
  return request.context.get(CAPPED_BINARY_CALL);
}
