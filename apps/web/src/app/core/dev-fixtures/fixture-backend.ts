// 09.1.1-T12.1 (plan 09.1.1 §2 AC10, §10 T12; ADR-0008 shape A): a dev-only Angular `HttpBackend`
// that answers the SDK's requests from `fixtures/web-api/<name>/` instead of the network, so the
// app can be run and reviewed before the API exists (`npm run start:fixtures -w apps/web`).
//
// It is installed as `HttpBackend` — the last link of `HttpClient`'s chain — rather than as an
// interceptor, and that placement is the whole point: `provideApi()` builds the SDK's `fetch` on
// `HttpClient` (`http-client-fetch.ts`), so requests still pass the `X-Request-Id`/`traceparent`
// interceptor, the `Idempotency-Key` interceptor and Angular's XSRF interceptor, and responses
// still travel back through the adapter and `ApiGateway`'s error mapper. A fixture refusal is
// therefore the same typed `ApiRequestError` a real 403/412 produces, which is exactly what a UI
// state review needs to exercise.
//
// Request -> fixture: the method and `/v1`-relative path select a corpus operation or the
// supplemental usage-summary read; `FixtureToggles` selects its variant (default `ok`).
// Missing routes and variants answer `501 fixture_not_found` rather than inventing an empty page.
//
// ID-keyed routes answer the document whose declared resource-id field matches the path value.
// This lets policy comparisons and release selection read distinct bodies. Two are CURSOR-KEYED
// (`cursorQueryParameter`):
// `listCiBindings` answers the catalog row whose `cursor` equals the request's cursor, so the
// Secrets tab's Load more gets a second page from the same backend and the control's terminal state
// is reachable in a browser; `listRuns` answers the same way for the Runs list (09.3.1-T1). An id no
// document carries, a cursor no page declares, and every request-agnostic operation still answer by
// variant alone; `selectEntry` below holds that order and nothing else changes.
// `getRun` is likewise variant-only: run-detail's ready/404/403/ref-format/null-evidence
// documents exercise the real SDK response/error path without manufacturing missing wire fields.
//
// Shapes: a `>= 400` answer is raised as `HttpErrorResponse` and a `2xx` as `HttpResponse`, which is
// what Angular's real backends produce; the adapter reads text (09.1.1-T8), so the body is the JSON
// text for `responseType: 'text'` (a `Blob` for 'blob', the parsed value otherwise). Every answer
// carries `Content-Type: application/json`, the fixture's own headers and
// `X-Evidgate-Fixture: <fixture name>` so a review can identify the supplied specimen.
//
// Dev-only: this module is reachable only from `dev-backend.fixtures.ts`, which the `fixtures`
// angular.json configuration substitutes for the empty `dev-backend.ts`; the production build
// therefore contains none of it, and `scripts/check-no-fixture-code.mjs` greps the bundle for the
// three marker strings below (plus the corpus's own long string values) to prove it.

import {
  HttpBackend,
  HttpErrorResponse,
  HttpHeaders,
  type HttpEvent,
  type HttpRequest,
  HttpResponse,
} from '@angular/common/http';
import {
  inject,
  Injectable,
  InjectionToken,
  makeEnvironmentProviders,
  type EnvironmentProviders,
} from '@angular/core';
import type { RetentionPolicy, RunPage } from '@evidgate/sdk';
import { mergeMap, type Observable, of, throwError, timer } from 'rxjs';

import { newUuid } from '../http/random-id';
import {
  findFixtureEntry,
  findFixtureEntryByCursor,
  findFixtureEntryById,
  type FixtureEntry,
} from './fixture-catalog';
import { FIXTURE_REQUEST_LOG, type FixtureCallRecord } from './fixture-request-log';
import { findRetentionFixture } from './fixture-retention';
import {
  apiRelativePath,
  matchFixtureRoute,
  pathParameter,
  queryParameter,
  type FixtureRoute,
} from './fixture-routes';
import { findSettingsRetentionWriteFixture } from './fixture-settings-permissions';
import { FixtureToggles } from './fixture-toggles';
import { findUsageSummaryFixture } from './fixture-usage-summary';
import { securityEventsBody } from './fixture-security-events';

/** Response header naming the corpus directory that answered; also a production-leak marker. */
export const FIXTURE_RESPONSE_HEADER = 'X-Evidgate-Fixture';

/** The 00 §9.2 `error.code` of a request no fixture covers; also a production-leak marker. */
export const FIXTURE_NOT_FOUND_CODE = 'fixture_not_found';

/** The corpus path quoted by the 501 message; also a production-leak marker. */
export const FIXTURE_CORPUS_HINT = 'fixtures/web-api';

/** The token description; also a production-leak marker. */
export const FIXTURE_BACKEND_TOKEN_DESCRIPTION = 'evidgate.fixture-backend';

/**
 * The fixture backend itself, injectable on its own so a spec (or a future review-only state panel)
 * can reach the instance the request chain uses. `provideFixtureBackend()` binds the same instance
 * to `HttpBackend`.
 */
export const FIXTURE_BACKEND = new InjectionToken<FixtureHttpBackend>(
  FIXTURE_BACKEND_TOKEN_DESCRIPTION,
);

const JSON_CONTENT_TYPE = 'application/json';

/** Status lines for the statuses the corpus and this backend use; anything else stays `''`. */
const STATUS_TEXT: Readonly<Record<number, string>> = {
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
function wireBody(body: unknown, responseType: string, contentType: string): unknown {
  if (body === null || body === undefined) {
    return null;
  }
  const text = JSON.stringify(body);
  switch (responseType) {
    case 'blob':
      return new Blob([text], { type: contentType });
    case 'text':
      return text;
    case 'arraybuffer':
      return new TextEncoder().encode(text).buffer;
    default:
      return structuredClone(body);
  }
}

// Filter the selected fixture page without consuming its continuation; filtered pages may be empty.
function listDecisionBody(entry: FixtureEntry, request: HttpRequest<unknown>): unknown {
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
function fixtureNotFoundBody(request: HttpRequest<unknown>, target: string): unknown {
  return {
    error: {
      code: FIXTURE_NOT_FOUND_CODE,
      message: `No fixture in ${FIXTURE_CORPUS_HINT}/<name>/input.json answers ${request.method} ${target}: no route, or no document for the selected variant.`,
      request_id: request.headers.get('X-Request-Id') ?? newUuid(),
      retryable: false,
    },
  };
}

/** `Content-Type: application/json`, then the fixture's headers, then the answering-fixture header. */
function fixtureHeaders(entry: FixtureEntry): HttpHeaders {
  let headers = new HttpHeaders({ 'Content-Type': JSON_CONTENT_TYPE });
  for (const [name, value] of Object.entries(entry.document.headers)) {
    headers = headers.set(name, value);
  }
  return headers.set(FIXTURE_RESPONSE_HEADER, entry.name);
}

/**
 * Appends the request the backend is about to answer to the page-global log. A spec runs in Node
 * (`window` absent), and the log is a review seam, never app state, so a missing window is a no-op.
 */
function recordRequest(request: HttpRequest<unknown>): void {
  if (typeof window === 'undefined') {
    return;
  }
  const headers: Record<string, string[]> = {};
  for (const name of request.headers.keys()) {
    headers[name.toLowerCase()] = request.headers.getAll(name) ?? [];
  }
  const recorded: FixtureCallRecord = { method: request.method, url: request.url, headers };
  const log = (window[FIXTURE_REQUEST_LOG] ??= []);
  log.push(recorded);
}

/**
 * The dev fixture `HttpBackend`. One instance answers every request (`useExisting`), so the toggles
 * are read per request instead of being captured at construction.
 */
@Injectable()
export class FixtureHttpBackend implements HttpBackend {
  private readonly toggles: FixtureToggles = inject(FixtureToggles);
  private readonly retentionPolicies = new Map<string, RetentionPolicy>();

  handle(request: HttpRequest<unknown>): Observable<HttpEvent<unknown>> {
    recordRequest(request);
    const relativePath = apiRelativePath(request.url);
    const entry = relativePath === null ? undefined : this.requestEntry(request, relativePath);
    if (entry === undefined) {
      return this.respond(
        request,
        501,
        fixtureNotFoundBody(request, relativePath ?? request.url),
        new HttpHeaders({ 'Content-Type': JSON_CONTENT_TYPE }),
        0,
      );
    }
    const latency =
      this.toggles.latencyFor(entry.document.operation_id) ?? entry.document.latency_ms;
    return this.respond(
      request,
      entry.document.status,
      listDecisionBody(entry, request),
      fixtureHeaders(entry),
      Math.max(0, latency),
    );
  }

  private requestEntry(
    request: HttpRequest<unknown>,
    relativePath: string,
  ): FixtureEntry | undefined {
    const retention = /^\/projects\/([^/]+)\/retention$/.exec(relativePath);
    if (retention?.[1] && (request.method === 'GET' || request.method === 'PUT')) {
      const operation = request.method === 'GET' ? 'getRetentionPolicy' : 'setRetentionPolicy';
      // A variant the supplement does not model (the corpus's `out_of_range` 422) falls through.
      const supplement = findRetentionFixture(
        request.method,
        this.toggles.variantFor(operation),
        retention[1],
        request.body,
        this.retentionPolicies,
      );
      // The supplement stays authoritative for `ok`; a malformed write must not reach the corpus row.
      const variant = this.toggles.variantFor(operation);
      if (supplement !== undefined || variant === 'ok') return supplement;
    }
    // Keep computed UI specimens outside the corpus registry, whose rows mirror on-disk examples.
    if (request.method === 'GET' && /^\/orgs\/[^/]+\/usage\/summary$/.test(relativePath)) {
      return findUsageSummaryFixture(this.toggles.variantFor('getUsageSummary'));
    }
    // The retention writer session needs one grant no corpus row holds; a toggle that names no
    // specimen falls through, so every corpus variant answers exactly as before.
    if (request.method === 'GET' && relativePath === '/permissions') {
      const supplemental = findSettingsRetentionWriteFixture(
        this.toggles.variantFor('listPermissions'),
      );
      if (supplemental !== undefined) return supplemental;
    }
    const route = matchFixtureRoute(request.method, relativePath);
    return route === undefined ? undefined : this.selectEntry(route, relativePath, request);
  }

  /** A matching resource id or cursor wins over the fallback variant selected in the URL. */
  private selectEntry(
    route: FixtureRoute,
    relativePath: string,
    request: HttpRequest<unknown>,
  ): FixtureEntry | undefined {
    const parameter = route.idKeyParameter;
    const requestedId =
      parameter === undefined ? undefined : pathParameter(route.path, relativePath, parameter);
    if (requestedId !== undefined) {
      const byId = findFixtureEntryById(route.operationId, requestedId, route.idKeyField);
      if (byId !== undefined) {
        return byId;
      }
    }
    const cursorParameter = route.cursorQueryParameter;
    if (cursorParameter !== undefined) {
      const requestedCursor = queryParameter(request.urlWithParams, cursorParameter);
      // An empty cursor is the first page (the app omits the parameter, and a `cursor=` would name
      // no page); only a row that declares THIS value answers.
      if (requestedCursor !== null && requestedCursor !== '') {
        const byCursor = findFixtureEntryByCursor(route.operationId, requestedCursor);
        if (byCursor !== undefined) {
          return byCursor;
        }
      }
    }
    return findFixtureEntry(route.operationId, this.toggles.variantFor(route.operationId));
  }

  /**
   * One answer: a `>= 400` status becomes the `HttpErrorResponse` the adapter expects from a real
   * backend (so `ApiGateway` maps the envelope), and a delay is applied with `timer` — a promise or
   * zone-aware scheduler is not available, the app is zoneless.
   */
  private respond(
    request: HttpRequest<unknown>,
    status: number,
    body: unknown,
    headers: HttpHeaders,
    latencyMs: number,
  ): Observable<HttpEvent<unknown>> {
    const contentType = headers.get('Content-Type') ?? JSON_CONTENT_TYPE;
    const event = (): Observable<HttpEvent<unknown>> => {
      const wire = wireBody(body, request.responseType, contentType);
      const statusText = STATUS_TEXT[status] ?? '';
      if (status >= 400) {
        const failure = new HttpErrorResponse({
          status,
          statusText,
          headers,
          url: request.url,
          error: wire,
        });
        return throwError(() => failure);
      }
      const response = new HttpResponse<unknown>({
        status,
        statusText,
        headers,
        url: request.url,
        body: wire,
      });
      return of(response);
    };
    return latencyMs > 0 ? timer(latencyMs).pipe(mergeMap(event)) : event();
  }
}

/**
 * The dev-only provider set: the fixture `HttpBackend` (replacing the XHR/fetch backend
 * `provideHttpClient()` installed) and the toggles it reads. Registration order matters — this must
 * come after `provideApi()`, whose `provideHttpClient()` also provides `HttpBackend`; the last
 * provider of a token wins, so the fixture backend answers and the interceptors still run.
 *
 * Imported by `dev-backend.fixtures.ts` only (the `fixtures` build/serve configuration replaces
 * `dev-backend.ts` with it). Production code importing this file would ship fixture data, which
 * `scripts/check-no-fixture-code.mjs` fails the build for.
 */
export function provideFixtureBackend(): EnvironmentProviders {
  return makeEnvironmentProviders([
    { provide: FIXTURE_BACKEND, useClass: FixtureHttpBackend },
    { provide: HttpBackend, useExisting: FIXTURE_BACKEND },
  ]);
}
