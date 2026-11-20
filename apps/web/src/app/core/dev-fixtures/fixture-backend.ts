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
// three marker strings this module re-exports (plus the corpus's own long string values) to prove
// it. The wire shapes those helpers build live in `fixture-backend-wire.ts`, which only this
// module and the fixture specs reach.

import { AuthoringPendingFixtureScenario } from './fixture-authoring-pending';
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
import type { RetentionPolicy } from '@evidgate/sdk';
import { mergeMap, type Observable, of, throwError, timer } from 'rxjs';

import {
  findFixtureEntry,
  findFixtureEntryByCursor,
  findFixtureEntryById,
  type FixtureEntry,
} from './fixture-catalog';
import { findRetentionFixture } from './fixture-retention';
import { RunCancelFixtureScenario } from './fixture-run-cancel';
import {
  apiRelativePath,
  matchFixtureRoute,
  pathParameter,
  queryParameter,
  type FixtureRoute,
} from './fixture-routes';
import { findSettingsRetentionWriteFixture } from './fixture-settings-permissions';
import { findCatalogOwnerPermissionsFixture } from './fixture-catalog-owner-permissions';
import { FixtureToggles } from './fixture-toggles';
import { findUsageSummaryFixture } from './fixture-usage-summary';
import { caseListFixtureBody } from './fixture-case-list';
import { findBulkCaseListFixture } from './fixture-case-list-bulk';
import { findBrowseCapFixture } from './fixture-browse-cap';
import { AuthoringTreeFixtureScenario } from './fixture-authoring-tree';
import { AuthoringCasesFixtureScenario } from './fixture-authoring-cases';
import { AiSettingsFixtureScenario, aiSettingsPermissions } from './fixture-ai-settings';
import {
  AuthoringEditorFixtureScenario,
  authoringEditorPermissions,
} from './fixture-authoring-editor';

import {
  FIXTURE_CORPUS_HINT,
  FIXTURE_NOT_FOUND_CODE,
  FIXTURE_RESPONSE_HEADER,
  fixtureHeaders,
  fixtureNotFoundBody,
  JSON_CONTENT_TYPE,
  listDecisionBody,
  recordRequest,
  STATUS_TEXT,
  wireBody,
} from './fixture-backend-wire';

export { FIXTURE_CORPUS_HINT, FIXTURE_NOT_FOUND_CODE, FIXTURE_RESPONSE_HEADER };

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

/**
 * The dev fixture `HttpBackend`. One instance answers every request (`useExisting`), so the toggles
 * are read per request instead of being captured at construction.
 */
@Injectable()
export class FixtureHttpBackend implements HttpBackend {
  private readonly toggles: FixtureToggles = inject(FixtureToggles);
  private readonly retentionPolicies = new Map<string, RetentionPolicy>();
  private readonly runCancellation = new RunCancelFixtureScenario();
  private readonly authoring = new AuthoringTreeFixtureScenario();
  private readonly authoringCases = new AuthoringCasesFixtureScenario();
  private readonly aiSettings = new AiSettingsFixtureScenario();
  private readonly authoringPending = new AuthoringPendingFixtureScenario();
  private readonly authoringEditor = new AuthoringEditorFixtureScenario();

  handle(request: HttpRequest<unknown>): Observable<HttpEvent<unknown>> {
    const relativePath = apiRelativePath(request.url);
    recordRequest(request, relativePath);
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
      entry.document.operation_id === 'listCases'
        ? caseListFixtureBody(entry, request)
        : listDecisionBody(entry, request),
      fixtureHeaders(entry),
      Math.max(0, latency),
    );
  }

  private authoringEntry(request: HttpRequest<unknown>, path: string): FixtureEntry | undefined {
    if (request.method !== 'GET') return undefined;
    if (/^\/projects\/[^/]+\/authoring\/tree$/.test(path)) {
      return this.authoring.entry(request, this.toggles.variantFor('listAuthoringTree'));
    }
    if (/^\/projects\/[^/]+\/authoring\/manual-cases$/.test(path)) {
      return this.authoringCases.entry(request, this.toggles.variantFor('listManualCases'));
    }
    return undefined;
  }

  private xlsxEntry(request: HttpRequest<unknown>, relativePath: string): FixtureEntry | undefined {
    if (queryParameter(request.urlWithParams, 'format') !== 'xlsx') return undefined;
    const importing =
      request.method === 'POST' && /^\/projects\/[^/]+\/authoring\/import$/.test(relativePath);
    const exporting =
      request.method === 'GET' && /^\/projects\/[^/]+\/authoring\/export$/.test(relativePath);
    const operation = importing ? 'importAuthoring' : exporting ? 'exportAuthoring' : null;
    if (!operation) return undefined;
    const variant = this.toggles.variantFor(operation);
    if (variant !== 'ok' && variant !== 'xlsx') return undefined;
    const confirmed = importing && queryParameter(request.urlWithParams, 'dry_run') === 'false';
    return findFixtureEntry(operation, confirmed ? 'xlsx-confirmed' : 'xlsx');
  }

  private requestEntry(
    request: HttpRequest<unknown>,
    relativePath: string,
  ): FixtureEntry | undefined {
    const capped = findBrowseCapFixture(
      request.method,
      relativePath,
      request.urlWithParams,
      (operation) => this.toggles.variantFor(operation),
    );
    if (capped !== undefined) return capped;
    const pending = this.authoringPending.entry(request, relativePath, (operation) =>
      this.toggles.variantFor(operation),
    );
    if (pending !== undefined) return pending;
    const authoring = this.authoringEntry(request, relativePath);
    if (authoring !== undefined) return authoring;
    const settings = this.aiSettings.entry(request, relativePath, (operation) =>
      this.toggles.variantFor(operation),
    );
    if (settings !== undefined) return settings;
    const editor = this.authoringEditor.entry(request, relativePath, (operation) =>
      this.toggles.variantFor(operation),
    );
    if (editor !== undefined) return editor;
    const cancellation = this.runCancellation.entry(
      request,
      relativePath,
      this.toggles.variantFor('getRun'),
      this.toggles.variantFor('cancelRun'),
    );
    if (cancellation !== undefined) return cancellation;
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
      const aiPermissions = aiSettingsPermissions(this.toggles.variantFor('listPermissions'));
      if (aiPermissions !== undefined) return aiPermissions;
      const editorPermissions = authoringEditorPermissions(
        this.toggles.variantFor('listPermissions'),
      );
      if (editorPermissions !== undefined) return editorPermissions;
      const supplemental = findSettingsRetentionWriteFixture(
        this.toggles.variantFor('listPermissions'),
      );
      if (supplemental !== undefined) return supplemental;
      const catalogOwner = findCatalogOwnerPermissionsFixture(
        this.toggles.variantFor('listPermissions'),
      );
      if (catalogOwner !== undefined) return catalogOwner;
    }
    // The catalogue's 200-row threshold is reviewed on a computed page, like usage summary: the
    // variant names no corpus file, and every other toggle still answers its directory.
    if (request.method === 'GET' && /^\/projects\/[^/]+\/cases$/.test(relativePath)) {
      const bulk = findBulkCaseListFixture(this.toggles.variantFor('listCases'));
      if (bulk !== undefined) return bulk;
    }
    const workbook = this.xlsxEntry(request, relativePath);
    if (workbook) return workbook;
    if (
      request.method === 'POST' &&
      /^\/projects\/[^/]+\/authoring\/import$/.test(relativePath) &&
      queryParameter(request.urlWithParams, 'dry_run') === 'false' &&
      this.toggles.variantFor('importAuthoring') === 'ok'
    ) {
      return findFixtureEntry('importAuthoring', 'confirmed');
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
