// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: ONE trace id end to end — route
// change → API call → server `traceparent`): the interceptor driven directly, with the router, the
// facade and the real SDK behind it, so every assertion below is about an exported span or about
// the header that would leave the browser.
//
// `HttpClient` is not used here on purpose: this directory may not import it
// (`apps/web/lint/rules/no-http-outside-core.cjs` allows only the view types, the provider
// functions and `HttpErrorResponse`). An interceptor is a plain function, so the spec calls it with
// an `HttpRequest` and a handler that returns the response event, the error or a subject it can
// cancel — which is also the only way to test the unsubscribe path.
import { DOCUMENT } from '@angular/common';
import { HttpErrorResponse, HttpHeaders, HttpRequest } from '@angular/common/http';
import type { HttpEvent, HttpHandlerFn } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { NavigationEnd, NavigationStart, Router, type Event as RouterEvent } from '@angular/router';
import { InMemorySpanExporter, type ReadableSpan } from '@opentelemetry/sdk-trace';
import { Subject, firstValueFrom, of, throwError, type Observable } from 'rxjs';

import { type RouteTreeNode } from './route-template';
import { startTelemetrySdk } from './telemetry-sdk';
import type { TelemetrySdkOptions } from './telemetry-sdk';
import type { TelemetryConfig } from './telemetry-config';
import { TELEMETRY_CONFIG } from './telemetry-config';
import { TELEMETRY_SDK_LOADER, TelemetryService } from './telemetry.service';
import { telemetryInterceptor } from './trace.interceptor';

/** A concrete id and request id: neither may appear in an attribute. */
const RUN_UUID = '3f2a1b4c-5d6e-4f70-8a9b-0c1d2e3f4a5b';

const API_URL = `/v1/projects/${RUN_UUID}/runs`;

/** `HttpEventType.Response`; only the interceptor's own structural check needs the field. */
const RESPONSE_EVENT_TYPE = 4;

/** One node per route in the chain; `routeConfig.path` and `firstChild` are all that is read. */
function snapshot(paths: readonly (string | null)[]): RouteTreeNode {
  const [path, ...rest] = paths;
  if (path === undefined) {
    throw new Error('snapshot needs at least one route');
  }
  return {
    routeConfig: path === null ? null : { path },
    firstChild: rest.length === 0 ? null : snapshot(rest),
  };
}

/** The two members `TelemetryService` reads off `Router`, and an event stream the spec drives. */
class RouterDouble {
  readonly events = new Subject<RouterEvent>();
  readonly routerState = { snapshot: { root: snapshot(['']) } };
}

/**
 * The final response event of an exchange. `HttpResponse` is outside this directory's import
 * allow-list, so the double spells out the members `HttpEvent`'s response member declares, and the
 * interceptor reads its `status` + `body` pair.
 */
function responseEvent(
  status: number,
  headers: HttpHeaders = new HttpHeaders(),
): HttpEvent<unknown> {
  return {
    type: RESPONSE_EVENT_TYPE,
    status,
    statusText: '',
    url: null,
    ok: status >= 200 && status < 300,
    headers,
    body: null,
    clone: () => {
      throw new Error('the interceptor never clones a response');
    },
  };
}

interface Harness {
  readonly service: TelemetryService;
  readonly router: RouterDouble;
  readonly exporter: InMemorySpanExporter;
}

/** Lets the dynamic-import promise and the facade's continuation run. */
async function settle(): Promise<void> {
  await Promise.resolve();
  await Promise.resolve();
}

async function createHarness(options: { readonly start?: boolean } = {}): Promise<Harness> {
  const exporter = new InMemorySpanExporter();
  const router = new RouterDouble();
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      { provide: Router, useValue: router },
      {
        provide: TELEMETRY_SDK_LOADER,
        useValue: () => {
          const module = {
            startTelemetrySdk: (config: TelemetryConfig, sdk?: TelemetrySdkOptions) =>
              startTelemetrySdk(config, { ...sdk, exporter }),
          };
          return Promise.resolve(module);
        },
      },
    ],
  });
  const service = TestBed.inject(TelemetryService);
  if (options.start !== false) {
    service.start();
    await settle();
  }
  return { service, router, exporter };
}

/** A navigation that matched `root`, so the facade opens and closes a route trace. */
function navigate(harness: Harness, url: string, root: readonly (string | null)[] = ['']): void {
  harness.router.routerState.snapshot.root = snapshot(root);
  harness.router.events.next(new NavigationStart(1, url));
  harness.router.events.next(new NavigationEnd(2, url, url));
}

/** Runs the interceptor the way `HttpClient` does: inside an injection context. */
function send(
  request: HttpRequest<unknown>,
  handle: HttpHandlerFn,
): Observable<HttpEvent<unknown>> {
  return TestBed.runInInjectionContext(() => telemetryInterceptor(request, handle));
}

function spansNamed(exporter: InMemorySpanExporter, name: string): readonly ReadableSpan[] {
  return exporter.getFinishedSpans().filter((span) => span.name === name);
}

function firstSpanNamed(exporter: InMemorySpanExporter, name: string): ReadableSpan {
  const [span] = spansNamed(exporter, name);
  if (span === undefined) {
    throw new Error(`expected an exported "${name}" span`);
  }
  return span;
}

describe('telemetryInterceptor', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('adds the api.request traceparent and keeps every other header', async () => {
    const harness = await createHarness();
    navigate(harness, '/projects/p-1/runs');
    const request = new HttpRequest('GET', API_URL, {
      headers: new HttpHeaders({ 'X-Custom': 'keep' }),
    });
    let seen = null as HttpRequest<unknown> | null;

    await firstValueFrom(
      send(request, (outgoing) => {
        seen = outgoing;
        return of(responseEvent(200));
      }),
    );

    const header = seen?.headers.get('traceparent') ?? '';
    expect(header).toMatch(/^00-[0-9a-f]{32}-[0-9a-f]{16}-01$/);
    expect(seen?.headers.get('X-Custom')).toBe('keep');
    expect(seen).not.toBe(request);
    const span = firstSpanNamed(harness.exporter, 'api.request');
    expect(header).toBe(`00-${span.spanContext().traceId}-${span.spanContext().spanId}-01`);
  });

  it('continues the trace of the route change the navigation opened', async () => {
    const harness = await createHarness();
    navigate(harness, `/projects/${RUN_UUID}/runs`, ['', 'projects/:projectId', 'runs']);
    let seen = null as HttpRequest<unknown> | null;

    await firstValueFrom(
      send(new HttpRequest('GET', API_URL), (outgoing) => {
        seen = outgoing;
        return of(responseEvent(200));
      }),
    );

    const routeSpan = firstSpanNamed(harness.exporter, 'route.change');
    const apiSpan = firstSpanNamed(harness.exporter, 'api.request');
    expect(apiSpan.spanContext().traceId).toBe(routeSpan.spanContext().traceId);
    expect(apiSpan.parentSpanContext?.spanId).toBe(routeSpan.spanContext().spanId);
    expect(seen?.headers.get('traceparent') ?? '').toContain(routeSpan.spanContext().traceId);
  });

  it('templates the requested path and never sends the concrete id as an attribute', async () => {
    const harness = await createHarness();

    await firstValueFrom(
      send(new HttpRequest('GET', `${API_URL}?status=open`), () => of(responseEvent(200))),
    );

    const span = firstSpanNamed(harness.exporter, 'api.request');
    expect(span.name).toBe('api.request');
    expect(span.attributes['http.request.method']).toBe('GET');
    expect(span.attributes['http.route']).toBe('/v1/projects/:id/runs');
    expect(JSON.stringify(span.attributes)).not.toContain(RUN_UUID);
  });

  it('records the status and the response request id', async () => {
    const harness = await createHarness();

    await firstValueFrom(
      send(new HttpRequest('GET', API_URL), () =>
        of(responseEvent(200, new HttpHeaders({ 'X-Request-Id': RUN_UUID }))),
      ),
    );

    const span = firstSpanNamed(harness.exporter, 'api.request');
    expect(span.attributes['http.response.status_code']).toBe(200);
    expect(span.attributes['evidgate.request_id']).toBe(RUN_UUID);
    expect(span.attributes['evidgate.outcome']).toBe('ok');
  });

  it('ignores a request id that is not a UUID', async () => {
    const harness = await createHarness();

    await firstValueFrom(
      send(new HttpRequest('GET', API_URL), () =>
        of(responseEvent(200, new HttpHeaders({ 'X-Request-Id': 'trace-42' }))),
      ),
    );

    expect(
      firstSpanNamed(harness.exporter, 'api.request').attributes['evidgate.request_id'],
    ).toBeUndefined();
  });

  it('passes a request outside /v1 through untouched and opens no span', async () => {
    const harness = await createHarness();
    let seen = null as HttpRequest<unknown> | null;

    await firstValueFrom(
      send(new HttpRequest('GET', '/internal/metrics'), (outgoing) => {
        seen = outgoing;
        return of(responseEvent(200));
      }),
    );

    expect(seen?.headers.has('traceparent')).toBe(false);
    expect(harness.exporter.getFinishedSpans()).toHaveLength(0);
  });

  it('passes through when the telemetry chunk has not loaded', async () => {
    const harness = await createHarness({ start: false });
    const request = new HttpRequest('GET', API_URL);
    let seen = null as HttpRequest<unknown> | null;

    await firstValueFrom(
      send(request, (outgoing) => {
        seen = outgoing;
        return of(responseEvent(200));
      }),
    );

    expect(harness.service.runtime).toBeNull();
    expect(seen).toBe(request);
    expect(seen?.headers.has('traceparent')).toBe(false);
    expect(harness.exporter.getFinishedSpans()).toHaveLength(0);
  });

  it('opens no span and leaves the request untouched when the caller set a traceparent', async () => {
    const harness = await createHarness();
    const callerTraceparent = '00-11111111111111111111111111111111-2222222222222222-01';
    const request = new HttpRequest('GET', API_URL, {
      headers: new HttpHeaders({ traceparent: callerTraceparent }),
    });
    let seen = null as HttpRequest<unknown> | null;

    await firstValueFrom(
      send(request, (outgoing) => {
        seen = outgoing;
        return of(responseEvent(200));
      }),
    );

    // The server continues the caller's trace, so a span here would hold ids that never reach the
    // wire: the interceptor does not create one and hands the request through as it is.
    expect(seen).toBe(request);
    expect(seen?.headers.get('traceparent')).toBe(callerTraceparent);
    expect(spansNamed(harness.exporter, 'api.request')).toHaveLength(0);
  });

  it('reads the document url once when there is no runtime, so no template is computed', async () => {
    // `apiPathTemplate` is pure and has no injection seam, and the Angular runner rejects mocking
    // a relative module, so the work is observed where it has to touch the world: the document
    // url. `isApiRequestUrl` reads it once; an api.request template computed before the runtime
    // guard — or the old eager `service.startApiCall({ route: apiPathTemplate(...) })` — reads it
    // a second time.
    let hrefReads = 0;
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        {
          provide: DOCUMENT,
          useValue: {
            location: {
              get href(): string {
                hrefReads += 1;
                return 'http://localhost/';
              },
            },
          },
        },
        // The facade takes its endpoint and ratio from the document's meta tags; this document is
        // a bare url, so the config is provided instead of parsed.
        { provide: TELEMETRY_CONFIG, useValue: { endpoint: null, sampleRatio: 1 } },
      ],
    });
    const service = TestBed.inject(TelemetryService);
    const request = new HttpRequest('GET', '/v1/runs');
    let seen = null as HttpRequest<unknown> | null;

    await firstValueFrom(
      send(request, (outgoing) => {
        seen = outgoing;
        return of(responseEvent(200));
      }),
    );

    expect(service.runtime).toBeNull();
    expect(hrefReads).toBe(1);
    // No span means no clone: the handler receives the caller's request itself.
    expect(seen).toBe(request);
    expect(seen?.headers.has('traceparent')).toBe(false);
  });

  it('records the envelope code and the retryable flag of a refused call', async () => {
    const harness = await createHarness();
    const error = new HttpErrorResponse({
      status: 404,
      error: { error: { code: 'run_not_found', retryable: true } },
      headers: new HttpHeaders({ 'X-Request-Id': RUN_UUID }),
    });

    await expect(
      firstValueFrom(send(new HttpRequest('GET', API_URL), () => throwError(() => error))),
    ).rejects.toBe(error);

    const span = firstSpanNamed(harness.exporter, 'api.request');
    expect(span.attributes['http.response.status_code']).toBe(404);
    expect(span.attributes['evidgate.api.error_code']).toBe('run_not_found');
    expect(span.attributes['evidgate.api.retryable']).toBe(true);
    expect(span.attributes['evidgate.request_id']).toBe(RUN_UUID);
    expect(span.attributes['evidgate.outcome']).toBe('error');
  });

  it('reads an envelope that arrived as a JSON string', async () => {
    const harness = await createHarness();
    const error = new HttpErrorResponse({
      status: 422,
      error: JSON.stringify({ error: { code: 'invalid_suite', retryable: false } }),
    });

    await expect(
      firstValueFrom(send(new HttpRequest('POST', API_URL, null), () => throwError(() => error))),
    ).rejects.toBe(error);

    const span = firstSpanNamed(harness.exporter, 'api.request');
    expect(span.attributes['evidgate.api.error_code']).toBe('invalid_suite');
    expect(span.attributes['evidgate.api.retryable']).toBe(false);
  });

  it('drops an envelope whose code or retryable flag is not the declared shape', async () => {
    const harness = await createHarness();
    const error = new HttpErrorResponse({
      status: 500,
      error: { error: { code: 'Not A Code!', retryable: 'yes' } },
    });

    await expect(
      firstValueFrom(send(new HttpRequest('GET', API_URL), () => throwError(() => error))),
    ).rejects.toBe(error);

    const span = firstSpanNamed(harness.exporter, 'api.request');
    expect(span.attributes['evidgate.api.error_code']).toBeUndefined();
    expect(span.attributes['evidgate.api.retryable']).toBeUndefined();
    expect(span.attributes['http.response.status_code']).toBe(500);
  });

  it('records a call the caller cancelled as cancelled', async () => {
    const harness = await createHarness();
    const response = new Subject<HttpEvent<unknown>>();

    const subscription = send(new HttpRequest('GET', API_URL), () => response).subscribe();
    subscription.unsubscribe();

    const span = firstSpanNamed(harness.exporter, 'api.request');
    expect(span.attributes['evidgate.outcome']).toBe('cancelled');
    expect(span.attributes['http.response.status_code']).toBeUndefined();
  });

  it('records a transport failure, which has no status', async () => {
    const harness = await createHarness();
    const error = new HttpErrorResponse({ status: 0 });

    await expect(
      firstValueFrom(send(new HttpRequest('GET', API_URL), () => throwError(() => error))),
    ).rejects.toBe(error);

    const span = firstSpanNamed(harness.exporter, 'api.request');
    expect(span.attributes['evidgate.outcome']).toBe('error');
    expect(span.attributes['http.response.status_code']).toBeUndefined();
  });

  it('leaves the document href untouched while resolving the url', async () => {
    const harness = await createHarness();
    const href = TestBed.inject(DOCUMENT).location.href;

    await firstValueFrom(
      send(new HttpRequest('GET', new URL(API_URL, href).toString()), () => of(responseEvent(204))),
    );

    expect(firstSpanNamed(harness.exporter, 'api.request').attributes['http.route']).toBe(
      '/v1/projects/:id/runs',
    );
  });
});
