// The wire side of the dev fixture backend: the response headers, per-status lines and the body
// shape a caller asked for. Kept apart from request selection so the answer a review inspects is
// readable without the routing rules beside it.

import { HttpHeaders, type HttpRequest } from '@angular/common/http';
import type { RunPage } from '@evidgate/sdk';

import { newUuid } from '../http/random-id';
import { AUTHORING_XLSX_MEDIA_TYPE } from '../data/authoring-import-export-gateway';
import { AUTHORING_XLSX_BASE64 } from './fixture-catalog-authoring-impex-rows';
import type { FixtureEntry } from './fixture-catalog';
import { FIXTURE_REQUEST_LOG, type FixtureCallRecord } from './fixture-request-log';
import { securityEventsBody } from './fixture-security-events';
import { queryParameter } from './fixture-routes';

/** Response header naming the corpus directory that answered; also a production-leak marker. */
export const FIXTURE_RESPONSE_HEADER = 'X-Evidgate-Fixture';

/** The 00 §9.2 `error.code` of a request no fixture covers; also a production-leak marker. */
export const FIXTURE_NOT_FOUND_CODE = 'fixture_not_found';

/** The corpus path quoted by the 501 message; also a production-leak marker. */
export const FIXTURE_CORPUS_HINT = 'fixtures/web-api';

export const JSON_CONTENT_TYPE = 'application/json';

/** Status lines for the statuses the corpus and this backend use; anything else stays `''`. */
export const STATUS_TEXT: Readonly<Record<number, string>> = {
  200: 'OK',
  201: 'Created',
  204: 'No Content',
  400: 'Bad Request',
  401: 'Unauthorized',
  403: 'Forbidden',
  404: 'Not Found',
  409: 'Conflict',
  412: 'Precondition Failed',
  422: 'Unprocessable Content',
  429: 'Too Many Requests',
  500: 'Internal Server Error',
  501: 'Not Implemented',
  503: 'Service Unavailable',
};

/**
 * The body in the shape the caller asked for. The T3 adapter always asks for `blob` (it converts
 * the body to a fetch `Response` itself), `json` stays the parsed value, and `text`/`arraybuffer`
 * keep working for any other `HttpClient` caller a spec adds.
 *
 * No branch may hand out the object a fixture document holds: the catalog is module-level, so a
 * caller that mutated the `json` body in place would corrupt every later answer from that fixture.
 * The serializing branches already produce fresh values, and `json` clones (the body is plain
 * `input.json` data, so `structuredClone` is enough).
 */
export function wireBody(body: unknown, responseType: string, contentType: string): unknown {
  if (body === null || body === undefined) {
    return null;
  }
  if (contentType === AUTHORING_XLSX_MEDIA_TYPE) {
    const bytes = Uint8Array.from(atob(AUTHORING_XLSX_BASE64), (value) => value.charCodeAt(0));
    if (responseType === 'blob') return new Blob([bytes], { type: contentType });
    if (responseType === 'arraybuffer') return bytes.buffer;
    throw new Error('XLSX fixture requires a binary response type');
  }
  const text = JSON.stringify(body);
  switch (responseType) {
    case 'blob':
      return new Blob([text], { type: contentType });
    case 'text':
      return text;
    case 'arraybuffer':
      // Host-provided encoders may belong to another realm; HttpClient buffers belong to this one.
      return Uint8Array.from(new TextEncoder().encode(text)).buffer;
    default:
      return structuredClone(body);
  }
}

// Filter the selected fixture page without consuming its continuation; filtered pages may be empty.
export function listDecisionBody(entry: FixtureEntry, request: HttpRequest<unknown>): unknown {
  if (entry.document.operation_id === 'listSecurityEvents')
    return securityEventsBody(entry, request);
  const decision = queryParameter(request.urlWithParams, 'decision');
  if (
    entry.document.operation_id !== 'listRuns' ||
    entry.document.status !== 200 ||
    decision === null
  ) {
    return entry.document.body;
  }
  // Catalog success bodies are checked against the RunPage contract by fixture-contract.spec.
  const page = entry.document.body as RunPage;
  return {
    ...page,
    items: page.items.filter((row) =>
      decision === 'pending' ? row.decision === null : row.decision?.toLowerCase() === decision,
    ),
  };
}

/**
 * The 00 §9.2 envelope of a request no fixture answers. `request_id` echoes the header the request
 * interceptor set (so the page shows the same id a real answer would carry) and falls back to a
 * fresh UUID for a request that never passed the interceptor — a spec calling the backend directly,
 * say.
 */
export function fixtureNotFoundBody(request: HttpRequest<unknown>, target: string): unknown {
  return {
    error: {
      code: FIXTURE_NOT_FOUND_CODE,
      message: `No fixture in ${FIXTURE_CORPUS_HINT}/<name>/input.json answers ${request.method} ${target}: no route, or no document for the selected variant.`,
      request_id: request.headers.get('X-Request-Id') ?? newUuid(),
      retryable: false,
    },
  };
}

/** The corpus stores logical JSON; workbook fixtures choose their binary wire media type here. */
export function fixtureHeaders(entry: FixtureEntry): HttpHeaders {
  const workbook =
    entry.document.operation_id === 'exportAuthoring' &&
    entry.document.variant === 'xlsx' &&
    entry.document.status === 200;
  let headers = new HttpHeaders({
    'Content-Type': workbook ? AUTHORING_XLSX_MEDIA_TYPE : JSON_CONTENT_TYPE,
  });
  for (const [name, value] of Object.entries(entry.document.headers)) {
    headers = headers.set(name, value);
  }
  return headers.set(FIXTURE_RESPONSE_HEADER, entry.name);
}

/**
 * Appends the request the backend is about to answer to the page-global log. A spec runs in Node
 * (`window` absent), and the log is a review seam, never app state, so a missing window is a no-op.
 */
export function recordRequest(request: HttpRequest<unknown>, path: string | null): void {
  if (typeof window === 'undefined') {
    return;
  }
  const headers: Record<string, string[]> = {};
  for (const name of request.headers.keys()) {
    headers[name.toLowerCase()] = request.headers.getAll(name) ?? [];
  }
  const captureBody =
    request.method === 'POST' &&
    path !== null &&
    /^\/projects\/[^/]+\/authoring\/changes$/.test(path);
  const recorded: FixtureCallRecord = {
    method: request.method,
    url: request.url,
    headers,
    ...(captureBody ? { body: request.body } : {}),
  };
  const log = (window[FIXTURE_REQUEST_LOG] ??= []);
  log.push(recorded);
}
