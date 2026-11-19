// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: ONE trace id end to end —
// route change → API call → server `traceparent`): the interceptor that puts the browser span's
// W3C header on the request and closes the span when the response or the failure arrives.
//
// It runs FIRST in `provideApi()`'s interceptor list, so `requestIdInterceptor` sees a request
// that already carries `traceparent` and leaves it alone (it only fills a header the caller did
// not set). Every other header, and every request outside same-origin `/v1`, is untouched: the
// interceptor answers with `next(request)` unchanged when telemetry has not loaded, which is why
// a failed telemetry chunk still produces the T3 random `traceparent` and never a missing one.
// A request the caller already stamped with its own `traceparent` passes through the same way:
// its trace context is the one the server continues, so there is no span this app could record
// whose ids would reach the wire.
//
// The span is ended on all three exits — a response, an error, and an unsubscribe before either
// (`finalize`), where a cancelled navigation or a cancelled query must not leave a span open.
import { DOCUMENT } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import type { HttpEvent, HttpHeaders, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { finalize, tap } from 'rxjs';

import { isApiRequestUrl } from '../http/api-origin';
import { isCappedBinaryCall } from '../http/binary-transport';
import { exceedsBinaryCap } from '../http/http-client-fetch-binary';
import { utf8ExceedsBytes } from '../http/utf8-size';
import { apiPathTemplate } from './api-path-template';
import { TelemetryService } from './telemetry.service';
import type { ApiCallResult } from './telemetry-sdk';

/** The response header the server echoes `error.request_id` in (00 §9.2). */
const REQUEST_ID_HEADER = 'X-Request-Id';

/** The `error.code` shape the server emits; anything else is not a code and is not sent. */
const API_ERROR_CODE = /^[a-z][a-z0-9_]{0,63}$/;

/** An error envelope is a few hundred bytes; a larger binary error body is not parsed for the span. */
const MAX_TRACED_ERROR_BYTES = 64 * 1024;

const UUID_SHAPE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** The `{ code, retryable }` fields of the `{"error": {…}}` envelope, or nulls. */
interface ErrorEnvelopeFields {
  readonly code: string | null;
  readonly retryable: boolean | null;
}

const NO_ENVELOPE: ErrorEnvelopeFields = { code: null, retryable: null };

/**
 * A successful response event, or `null` for every other event of the stream (sent, headers,
 * progress, user).
 *
 * The test is structural on purpose: `HttpResponse` and `HttpEventType` are outside the
 * `@angular/common/http` import allow-list of this directory
 * (`apps/web/lint/rules/no-http-outside-core.cjs`), so the discriminator is the pair of fields
 * only a response carries — `status` (also on the header event) and `body` (only on the final
 * response). The header event is therefore not mistaken for the end of the call.
 */
function resultOfResponse(event: HttpEvent<unknown>): ApiCallResult | null {
  if (!('status' in event) || !('body' in event)) {
    return null;
  }
  const result: ApiCallResult = {
    status: event.status,
    outcome: event.status >= 400 ? 'error' : 'ok',
  };
  const requestId = readRequestId(event.headers);
  if (requestId !== null) {
    result.requestId = requestId;
  }
  return result;
}

/** An over-cap byte reply: a failed call of the `network` kind, the shape the gateway reports. */
function refusedBinaryResult(event: HttpEvent<unknown>): ApiCallResult {
  const result: ApiCallResult = { outcome: 'error', errorKind: 'network' };
  if ('status' in event) {
    result.status = event.status;
  }
  return result;
}

/** The `X-Request-Id` UUID of a response, lowercased, or `null` when absent or not a UUID. */
function readRequestId(headers: HttpHeaders): string | null {
  const value = headers.get(REQUEST_ID_HEADER);
  return value !== null && UUID_SHAPE.test(value) ? value.toLowerCase() : null;
}

/** Whether a string error body is within the bound, in UTF-8 bytes like the binary decode. */
function stringFitsTracedBound(text: string): boolean {
  return !utf8ExceedsBytes(text, MAX_TRACED_ERROR_BYTES);
}

/** The body as JSON, when the server sent it as a string instead of parsed JSON. */
function parseJsonBody(body: unknown): unknown {
  // A binary-response call reads its error as bytes (the interceptor sits outside the adapter,
  // so it sees the raw `HttpClient` error); decode them, bounded, so the span keeps its code.
  if (body instanceof ArrayBuffer) {
    if (body.byteLength > MAX_TRACED_ERROR_BYTES) {
      return null;
    }
    return parseJsonBody(new TextDecoder().decode(body));
  }
  if (typeof body !== 'string') {
    return body;
  }
  if (!stringFitsTracedBound(body)) {
    return null;
  }
  try {
    return JSON.parse(body) as unknown;
  } catch {
    return null;
  }
}

/**
 * `{ code, retryable }` from a `{"error": {…}}` body (00 §9.2). Both fields are read defensively:
 * the envelope is server input, so a body that is not an object, an `error` member that is not an
 * object, a `code` that does not match {@link API_ERROR_CODE} and a non-boolean `retryable` are
 * each simply absent. The message and `details` of the envelope are never read here.
 */
function readErrorEnvelope(body: unknown): ErrorEnvelopeFields {
  const parsed = parseJsonBody(body);
  if (typeof parsed !== 'object' || parsed === null) {
    return NO_ENVELOPE;
  }
  const envelope = (parsed as Record<string, unknown>)['error'];
  if (typeof envelope !== 'object' || envelope === null) {
    return NO_ENVELOPE;
  }
  const fields = envelope as Record<string, unknown>;
  const code = fields['code'];
  const retryable = fields['retryable'];
  return {
    code: typeof code === 'string' && API_ERROR_CODE.test(code) ? code : null,
    retryable: typeof retryable === 'boolean' ? retryable : null,
  };
}

/** What the span learns from a failed call: status, envelope code/retryable, request id. */
function resultOfError(error: unknown): ApiCallResult {
  if (!(error instanceof HttpErrorResponse)) {
    // A failure that never reached the transport (a chain bug) has no status to report.
    return { outcome: 'error' };
  }
  const result: ApiCallResult = { status: error.status, outcome: 'error' };
  const requestId = readRequestId(error.headers);
  if (requestId !== null) {
    result.requestId = requestId;
  }
  const envelope = readErrorEnvelope(error.error);
  if (envelope.code !== null) {
    result.errorCode = envelope.code;
  }
  if (envelope.retryable !== null) {
    result.retryable = envelope.retryable;
  }
  return result;
}

/**
 * Traces every same-origin `/v1` request as a child span of the current route change and adds
 * its `traceparent`.
 *
 * Three requests pass through byte for byte, with no span and no URL work: one outside `/v1`,
 * one that already carries a `traceparent` (the caller's trace is the one the server continues,
 * and a span whose ids never reach the wire would be a second trace nothing joins), and one made
 * before the telemetry chunk loaded (there is no runtime to record it, so the route template is
 * not computed either). A request that does get a span is the one case where the caller did not
 * set the header and the facade has a runtime.
 */
export const telemetryInterceptor: HttpInterceptorFn = (request, next) => {
  const document = inject(DOCUMENT);
  if (!isApiRequestUrl(request.url, document.location.href)) {
    return next(request);
  }
  if (request.headers.has('traceparent')) {
    return next(request);
  }
  const runtime = inject(TelemetryService).runtime;
  if (runtime === null) {
    return next(request);
  }
  const call = runtime.startApiCall({
    method: request.method,
    route: apiPathTemplate(request.url, document.location.href),
  });
  const outgoing = request.clone({
    headers: request.headers.set('traceparent', call.traceparent),
  });
  return next(outgoing).pipe(
    tap({
      next: (event: HttpEvent<unknown>) => {
        // The adapter refuses an over-cap capped reply after this interceptor has seen it; the
        // span records that refusal instead of an ok response. Only calls the adapter capped
        // carry the context marker; a caller's own `reportProgress` request is not measured.
        if (isCappedBinaryCall(request) && exceedsBinaryCap(event)) {
          call.end(refusedBinaryResult(event));
          return;
        }
        const result = resultOfResponse(event);
        if (result !== null) {
          call.end(result);
        }
      },
      error: (error: unknown) => call.end(resultOfError(error)),
    }),
    // Also runs after a response and after an error; `end` ignores a second call, so this arm
    // only records the one case the other two cannot see: an unsubscribe before either.
    finalize(() => call.end({ outcome: 'cancelled' })),
  );
};
