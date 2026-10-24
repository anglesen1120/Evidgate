// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: telemetry loss must never break the
// app): the facade in the initial bundle — when it loads the chunk, what it hands the chunk, how
// it maps router events onto route spans, and what it does when the chunk never arrives.
//
// Two harnesses. The default one starts the REAL runtime through an in-memory exporter, so the
// navigation assertions below are about exported spans (the route template, and the absence of the
// URL in any attribute). The stub one replaces the runtime with a recorder, which is how the wiring
// that has no span of its own is asserted: the `pagehide`/`visibilitychange` flush, the delegation
// of `startApiCall`/`reportError`, and the fact that everything is a no-op before the load lands.
import { DOCUMENT } from '@angular/common';
import {
  NavigationCancel,
  NavigationEnd,
  NavigationError,
  NavigationSkipped,
  NavigationStart,
  Router,
  type ActivatedRouteSnapshot,
  type Event as RouterEvent,
} from '@angular/router';
import { LOCALE_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { InMemorySpanExporter, type ReadableSpan } from '@opentelemetry/sdk-trace';
import { Subject } from 'rxjs';

import { startTelemetrySdk } from './telemetry-sdk';
import type {
  ApiCall,
  ApiCallResult,
  ErrorReport,
  RouteChange,
  TelemetryOutcome,
  TelemetryRuntime,
  TelemetrySdkOptions,
} from './telemetry-sdk';
import { TELEMETRY_CONFIG, type TelemetryConfig } from './telemetry-config';
import { TELEMETRY_SDK_LOADER, TelemetryService } from './telemetry.service';
import { UNKNOWN_ROUTE } from './route-template';

/** A concrete id and query a navigation can carry but no span attribute may. */
const RUN_UUID = '3f2a1b4c-5d6e-4f70-8a9b-0c1d2e3f4a5b';

/** One node per route in the chain; `routeConfig.path` and `firstChild` are all that is read. */
function snapshot(paths: readonly (string | null)[]): ActivatedRouteSnapshot {
  const [path, ...rest] = paths;
  if (path === undefined) {
    throw new Error('snapshot needs at least one route');
  }
  const node = {
    routeConfig: path === null ? null : { path },
    firstChild: rest.length === 0 ? null : snapshot(rest),
  };
  return node as unknown as ActivatedRouteSnapshot;
}

/** The two members `TelemetryService` reads off `Router`, and an event stream the spec drives. */
class RouterDouble {
  readonly events = new Subject<RouterEvent>();
  readonly routerState = { snapshot: { root: snapshot(['']) } };
}

/** A runtime that records every call, for the wiring that produces no span of its own. */
class RuntimeDouble implements TelemetryRuntime {
  readonly calls: string[] = [];

  beginRouteChange(): RouteChange {
    this.calls.push('beginRouteChange');
    return {
      end: (outcome: TelemetryOutcome, route: string): void => {
        this.calls.push(`end:${outcome}:${route}`);
      },
    };
  }

  setRoute(route: string): void {
    this.calls.push(`setRoute:${route}`);
  }

  startApiCall(input: { method: string; route: string }): ApiCall {
    this.calls.push(`startApiCall:${input.method} ${input.route}`);
    return {
      traceparent: 'stub-traceparent',
      end: (result: ApiCallResult): void => {
        this.calls.push(`apiEnd:${result.outcome}`);
      },
    };
  }

  reportError(report: ErrorReport): void {
    this.calls.push(`reportError:${report.source}`);
  }

  flushVitals(): void {
    this.calls.push('flushVitals');
  }

  disableExport(): void {
    this.calls.push('disableExport');
  }

  shutdown(): Promise<void> {
    this.calls.push('shutdown');
    return Promise.resolve();
  }
}

interface Harness {
  readonly service: TelemetryService;
  readonly router: RouterDouble;
  readonly exporter: InMemorySpanExporter;
  readonly configs: TelemetryConfig[];
  readonly sdkOptions: (TelemetrySdkOptions | undefined)[];
  /** `true` when the harness stubbed the runtime instead of starting the real SDK. */
  readonly stub: RuntimeDouble | null;
  loadCount(): number;
}

interface HarnessOptions {
  readonly router?: boolean;
  readonly document?: Document;
  readonly config?: TelemetryConfig;
  readonly locale?: string;
  /** Loader outcome: `'reject'` fails the chunk, `'stub'` returns a recording runtime. */
  readonly loader?: 'real' | 'stub' | 'reject';
}

function createHarness(options: HarnessOptions = {}): Harness {
  const exporter = new InMemorySpanExporter();
  const router = new RouterDouble();
  const stub = options.loader === 'stub' ? new RuntimeDouble() : null;
  const configs: TelemetryConfig[] = [];
  const sdkOptions: (TelemetrySdkOptions | undefined)[] = [];
  let loads = 0;

  const loader = (): Promise<{ startTelemetrySdk: typeof startTelemetrySdk }> => {
    loads += 1;
    if (options.loader === 'reject') {
      return Promise.reject(new Error('chunk unavailable'));
    }
    return Promise.resolve({
      startTelemetrySdk: (config: TelemetryConfig, sdk?: TelemetrySdkOptions) => {
        configs.push(config);
        sdkOptions.push(sdk);
        if (stub !== null) {
          return stub;
        }
        return startTelemetrySdk(config, { ...sdk, exporter });
      },
    });
  };

  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      { provide: TELEMETRY_SDK_LOADER, useValue: loader },
      ...(options.router === false ? [] : [{ provide: Router, useValue: router }]),
      ...(options.config === undefined
        ? []
        : [{ provide: TELEMETRY_CONFIG, useValue: options.config }]),
      ...(options.document === undefined
        ? []
        : [{ provide: DOCUMENT, useValue: options.document }]),
      ...(options.locale === undefined ? [] : [{ provide: LOCALE_ID, useValue: options.locale }]),
    ],
  });
  return {
    service: TestBed.inject(TelemetryService),
    router,
    exporter,
    configs,
    sdkOptions,
    stub,
    loadCount: () => loads,
  };
}

/**
 * Dispatches a `visibilitychange` while the document reports itself hidden. jsdom has no API to
 * hide a document, so the getter is shadowed for the duration of the dispatch and restored after.
 */
function dispatchHiddenVisibilityChange(): void {
  const original = Object.getOwnPropertyDescriptor(document, 'visibilityState');
  Object.defineProperty(document, 'visibilityState', {
    configurable: true,
    get: () => 'hidden',
  });
  try {
    window.dispatchEvent(new Event('visibilitychange'));
  } finally {
    if (original === undefined) {
      Reflect.deleteProperty(document, 'visibilityState');
    } else {
      Object.defineProperty(document, 'visibilityState', original);
    }
  }
}

/** A configuration exporting to `origin`, so a violation has an origin to match. */
function configFor(origin: string): TelemetryConfig {
  return { endpoint: `${origin}/v1/traces`, sampleRatio: 1 };
}

/**
 * Dispatches a `securitypolicyviolation` on the document. jsdom has no
 * `SecurityPolicyViolationEvent` constructor, so the fields are defined on a plain `Event` — the
 * same shape the browser sends, without the browser.
 */
function dispatchCspViolation(
  blockedURI: string,
  directive: string = 'connect-src',
  disposition: string = 'enforce',
): void {
  const event = new Event('securitypolicyviolation') as unknown as SecurityPolicyViolationEvent;
  Object.defineProperties(event, {
    blockedURI: { value: blockedURI },
    effectiveDirective: { value: directive },
    violatedDirective: { value: directive },
    disposition: { value: disposition },
  });
  document.dispatchEvent(event);
}

/** A `traceparent` a server can continue, independent of any exporter. */
const TRACEPARENT_SHAPE = /^00-[0-9a-f]{32}-[0-9a-f]{16}-0[01]$/;

/** Lets the dynamic-import promise and the service's continuation run. */
async function settle(): Promise<void> {
  await Promise.resolve();
  await Promise.resolve();
}

function spansNamed(exporter: InMemorySpanExporter, name: string): readonly ReadableSpan[] {
  return exporter.getFinishedSpans().filter((span) => span.name === name);
}

describe('TelemetryService', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('loads the chunk once, however often start() is called', async () => {
    const harness = createHarness();

    harness.service.start();
    harness.service.start();
    await settle();

    expect(harness.loadCount()).toBe(1);
    expect(harness.service.runtime).not.toBeNull();
  });

  it('hands the configuration and the locale to the chunk', async () => {
    const harness = createHarness({
      config: { endpoint: 'https://otlp.evidgate.example/v1/traces', sampleRatio: 0.25 },
    });

    harness.service.start();
    await settle();

    expect(harness.configs).toEqual([
      { endpoint: 'https://otlp.evidgate.example/v1/traces', sampleRatio: 0.25 },
    ]);
    expect(harness.sdkOptions).toEqual([{ locale: 'en' }]);
  });

  it('reports the primary subtag of the application locale', async () => {
    const harness = createHarness({ locale: 'vi-VN' });

    harness.service.start();
    await settle();

    expect(harness.sdkOptions).toEqual([{ locale: 'vi' }]);
  });

  it('uses the configuration the document yields when none is provided', async () => {
    const harness = createHarness();

    harness.service.start();
    await settle();

    expect(harness.configs).toEqual([{ endpoint: null, sampleRatio: 1 }]);
  });

  it('does nothing at all without a window', async () => {
    const noWindow = document.implementation.createHTMLDocument('no-window');
    const harness = createHarness({ document: noWindow });

    harness.service.start();
    await settle();

    expect(harness.loadCount()).toBe(0);
    expect(harness.service.runtime).toBeNull();
    expect(harness.service.startApiCall({ method: 'GET', route: '/v1/runs' })).toBeNull();
    expect(() => harness.service.reportError({ source: 'angular', type: 'Error' })).not.toThrow();
  });

  it('stays disabled, with one warning, when the chunk fails to load', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    try {
      const harness = createHarness({ loader: 'reject' });

      harness.service.start();
      await settle();

      expect(harness.service.runtime).toBeNull();
      expect(warn).toHaveBeenCalledTimes(1);

      harness.service.start();
      await settle();

      expect(harness.loadCount()).toBe(1);
      expect(warn).toHaveBeenCalledTimes(1);
    } finally {
      warn.mockRestore();
    }
  });

  it('traces a navigation with the route template only, never the URL', async () => {
    const harness = createHarness();
    harness.service.start();
    await settle();
    harness.router.routerState.snapshot.root = snapshot(['', 'projects/:projectId', 'runs/:runId']);

    const url = `/projects/${RUN_UUID}/runs/r-9?tab=2`;
    harness.router.events.next(new NavigationStart(1, url));
    harness.router.events.next(new NavigationEnd(2, url, url));

    const spans = spansNamed(harness.exporter, 'route.change');

    expect(spans).toHaveLength(1);
    expect(spans[0]?.attributes['evidgate.route']).toBe('/projects/:projectId/runs/:runId');
    expect(spans[0]?.attributes['evidgate.outcome']).toBe('ok');

    const serialized = JSON.stringify(spans[0]?.attributes);
    expect(serialized).not.toContain(RUN_UUID);
    expect(serialized).not.toContain('r-9');
    expect(serialized).not.toContain('tab=2');
  });

  it('ends a cancelled navigation as cancelled and keeps the route it was leaving', async () => {
    const harness = createHarness();
    harness.service.start();
    await settle();
    harness.router.routerState.snapshot.root = snapshot(['', 'projects/:projectId']);

    harness.router.events.next(new NavigationStart(1, '/projects/p-1'));
    harness.router.events.next(new NavigationEnd(2, '/projects/p-1', '/projects/p-1'));
    harness.router.events.next(new NavigationStart(3, '/projects/p-1/runs'));
    harness.router.events.next(new NavigationCancel(4, '/projects/p-1/runs', 'guard'));

    const spans = spansNamed(harness.exporter, 'route.change');

    expect(spans).toHaveLength(2);
    expect(spans[0]?.attributes['evidgate.route']).toBe('/projects/:projectId');
    expect(spans[0]?.attributes['evidgate.outcome']).toBe('ok');
    expect(spans[1]?.attributes['evidgate.route']).toBe('/projects/:projectId');
    expect(spans[1]?.attributes['evidgate.outcome']).toBe('cancelled');
  });

  it('ends a skipped navigation as cancelled and a failed one as error', async () => {
    const harness = createHarness();
    harness.service.start();
    await settle();

    harness.router.events.next(new NavigationStart(1, '/runs'));
    harness.router.events.next(new NavigationSkipped(2, '/runs', 'ignored'));
    harness.router.events.next(new NavigationStart(3, '/runs/1'));
    harness.router.events.next(new NavigationError(4, '/runs/1', new Error('resolver failed')));

    const spans = spansNamed(harness.exporter, 'route.change');

    expect(spans.map((span) => span.attributes['evidgate.outcome'])).toEqual([
      'cancelled',
      'error',
    ]);
    expect(spans[0]?.attributes['evidgate.route']).toBe(UNKNOWN_ROUTE);
  });

  it('closes a navigation that never finished when the next one starts', async () => {
    const harness = createHarness();
    harness.service.start();
    await settle();

    harness.router.events.next(new NavigationStart(1, '/a'));
    harness.router.events.next(new NavigationStart(2, '/b'));
    harness.router.events.next(new NavigationEnd(3, '/b', '/b'));

    const spans = spansNamed(harness.exporter, 'route.change');

    expect(spans.map((span) => span.attributes['evidgate.outcome'])).toEqual(['cancelled', 'ok']);
  });

  it('works without a router, tracing only what it is asked to trace', async () => {
    const harness = createHarness({ router: false });

    harness.service.start();
    await settle();

    expect(harness.service.runtime).not.toBeNull();
    expect(spansNamed(harness.exporter, 'route.change')).toHaveLength(0);
  });

  it('delegates API spans and error reports to the loaded runtime', async () => {
    const harness = createHarness({ loader: 'stub' });
    harness.service.start();
    await settle();

    const call = harness.service.startApiCall({ method: 'POST', route: '/v1/runs' });
    call?.end({ outcome: 'ok' });
    harness.service.reportError({ source: 'angular', type: 'Error' });

    expect(call?.traceparent).toBe('stub-traceparent');
    expect(harness.stub?.calls).toContain('startApiCall:POST /v1/runs');
    expect(harness.stub?.calls).toContain('apiEnd:ok');
    expect(harness.stub?.calls).toContain('reportError:angular');
  });

  it('flushes the vitals of the route being left before it opens the next one', async () => {
    const harness = createHarness({ loader: 'stub' });
    harness.service.start();
    await settle();

    harness.router.events.next(new NavigationStart(1, '/projects/p-1/runs'));

    const calls = harness.stub?.calls ?? [];
    const flush = calls.indexOf('flushVitals');
    expect(flush).toBeGreaterThanOrEqual(0);
    expect(flush).toBeLessThan(calls.indexOf('beginRouteChange'));
  });

  it('flushes vitals when the page is hidden away', async () => {
    const harness = createHarness({ loader: 'stub' });
    harness.service.start();
    await settle();

    window.dispatchEvent(new Event('pagehide'));
    dispatchHiddenVisibilityChange();

    expect(harness.stub?.calls.filter((call) => call === 'flushVitals')).toHaveLength(2);
  });

  it('switches export off when the CSP blocks the exporter origin, and keeps tracing', async () => {
    const origin = 'https://otel-blocked.evidgate.example';
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const harness = createHarness({ config: configFor(origin) });
    harness.service.start();
    await settle();

    harness.service.runtime?.beginRouteChange().end('ok', '/runs');
    expect(spansNamed(harness.exporter, 'route.change')).toHaveLength(1);

    dispatchCspViolation(origin);

    // Tracing continues — new spans, a usable `traceparent` — but nothing else is exported.
    harness.service.runtime?.beginRouteChange().end('ok', '/runs');
    const call = harness.service.startApiCall({ method: 'GET', route: '/v1/runs' });
    call?.end({ outcome: 'ok' });

    expect(harness.exporter.getFinishedSpans()).toHaveLength(1);
    expect(call?.traceparent).toMatch(TRACEPARENT_SHAPE);
    expect(warn).toHaveBeenCalledTimes(1);
    const message = String(warn.mock.calls[0]?.[0]);
    expect(message).toContain('connect-src');
    // The warning names the directive only: the endpoint could carry a token.
    expect(message).not.toContain('/v1/traces');

    // One warning per page, however many violations the policy reports.
    dispatchCspViolation(origin);
    expect(warn).toHaveBeenCalledTimes(1);
    warn.mockRestore();
  });

  it('ignores a violation that blocks another origin', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const harness = createHarness({ config: configFor('https://otel-other.evidgate.example') });
    harness.service.start();
    await settle();

    dispatchCspViolation('https://cdn.other.example/app.js');
    harness.service.runtime?.beginRouteChange().end('ok', '/runs');

    expect(spansNamed(harness.exporter, 'route.change')).toHaveLength(1);
    expect(warn).not.toHaveBeenCalled();
    warn.mockRestore();
  });

  it('ignores a violation of another directive on the exporter origin', async () => {
    const origin = 'https://otel-directive.evidgate.example';
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const harness = createHarness({ config: configFor(origin) });
    harness.service.start();
    await settle();

    dispatchCspViolation(origin, 'img-src');
    harness.service.runtime?.beginRouteChange().end('ok', '/runs');

    expect(spansNamed(harness.exporter, 'route.change')).toHaveLength(1);
    expect(warn).not.toHaveBeenCalled();
    warn.mockRestore();
  });

  it('ignores a report-only policy, which blocks nothing', async () => {
    const origin = 'https://otel-report.evidgate.example';
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const harness = createHarness({ config: configFor(origin) });
    harness.service.start();
    await settle();

    dispatchCspViolation(origin, 'connect-src', 'report-only');
    harness.service.runtime?.beginRouteChange().end('ok', '/runs');

    expect(spansNamed(harness.exporter, 'route.change')).toHaveLength(1);
    expect(warn).not.toHaveBeenCalled();
    warn.mockRestore();
  });

  it('does not flush on a visibilitychange that is still visible', async () => {
    const harness = createHarness({ loader: 'stub' });
    harness.service.start();
    await settle();

    window.dispatchEvent(new Event('visibilitychange'));

    expect(harness.stub?.calls).not.toContain('flushVitals');
  });
});
