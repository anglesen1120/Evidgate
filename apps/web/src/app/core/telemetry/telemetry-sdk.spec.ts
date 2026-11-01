// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: one trace id end to end, Web
// Vitals per route, no PII): what the telemetry chunk actually exports, read back from an
// `InMemorySpanExporter` — the real `TracerProvider`, the real samplers, the real
// `ScrubbingSpanExporter`. Nothing here stubs the runtime, so a sampler that stops sampling, a
// parent link that stops being set or a scrubber that stops scrubbing fails these tests.
//
// Two seams are handed in and both exist for this file: `options.exporter` (read spans instead of
// POSTing them) and `options.vitalReporters` (jsdom has no `PerformanceObserver`, so `web-vitals`
// cannot report anything by itself; the spec plays the browser and calls the registered
// callbacks). Everything else — names, attributes, statuses, parent contexts — is the production
// path.
import { SpanKind, SpanStatusCode } from '@opentelemetry/api';
import {
  InMemorySpanExporter,
  type ReadableSpan,
  type SpanExporter,
} from '@opentelemetry/sdk-trace';
import type { Metric, ReportOpts } from 'web-vitals';

import {
  startTelemetrySdk,
  type ErrorReport,
  type ErrorSource,
  type TelemetryRuntime,
  type VitalReporters,
} from './telemetry-sdk';

/** A request id the server could have echoed; its shape is what `evidgate.request_id` accepts. */
const REQUEST_ID = '3f2a1b4c-5d6e-4f70-8a9b-0c1d2e3f4a5b';

/** A JWT-shaped sample assembled here: no committed line holds a credential-shaped literal. */
function jwtSample(): string {
  return ['eyJhbGciOiJIUzI1NiJ9', 'eyJzdWIiOiJ1c2VyIn0', 'c2lnbmF0dXJlLXZhbHVl'].join('.');
}

/** A `web-vitals` metric with the fields the runtime reads. */
function metricOf(name: Metric['name'], value: number, rating: Metric['rating']): Metric {
  return {
    name,
    value,
    rating,
    delta: value,
    id: `${name}-1`,
    entries: [],
    navigationType: 'navigate',
    navigationId: 1,
  };
}

/** A reporter set whose callbacks the spec calls, standing in for a browser's observers. */
interface VitalProbe {
  readonly reporters: VitalReporters;
  /** The second argument each reporter was registered with, so the options can be asserted. */
  readonly options: Partial<Record<'LCP' | 'INP' | 'CLS', ReportOpts | undefined>>;
  report(name: 'LCP' | 'INP' | 'CLS', value: number, rating?: Metric['rating']): void;
}

function vitalProbe(): VitalProbe {
  const callbacks: Partial<Record<'LCP' | 'INP' | 'CLS', (metric: Metric) => void>> = {};
  const options: Partial<Record<'LCP' | 'INP' | 'CLS', ReportOpts | undefined>> = {};
  return {
    options,
    reporters: {
      onLCP: (onReport, opts) => {
        callbacks.LCP = onReport;
        options.LCP = opts;
      },
      onINP: (onReport, opts) => {
        callbacks.INP = onReport;
        options.INP = opts;
      },
      onCLS: (onReport, opts) => {
        callbacks.CLS = onReport;
        options.CLS = opts;
      },
    },
    report(name, value, rating = 'good') {
      callbacks[name]?.(metricOf(name, value, rating));
    },
  };
}

interface Harness {
  readonly runtime: TelemetryRuntime;
  readonly exporter: InMemorySpanExporter;
  readonly vitals: VitalProbe;
}

function startSdk(
  options: {
    readonly sampleRatio?: number;
    readonly endpoint?: string | null;
    readonly locale?: string;
    readonly serviceVersion?: string;
  } = {},
): Harness {
  const exporter = new InMemorySpanExporter();
  const vitals = vitalProbe();
  const runtime = startTelemetrySdk(
    { endpoint: options.endpoint ?? null, sampleRatio: options.sampleRatio ?? 1 },
    {
      exporter,
      vitalReporters: vitals.reporters,
      ...(options.locale === undefined ? {} : { locale: options.locale }),
      ...(options.serviceVersion === undefined ? {} : { serviceVersion: options.serviceVersion }),
    },
  );
  return { runtime, exporter, vitals };
}

function spansNamed(exporter: InMemorySpanExporter, name: string): readonly ReadableSpan[] {
  return exporter.getFinishedSpans().filter((span) => span.name === name);
}

function firstSpanNamed(exporter: InMemorySpanExporter, name: string): ReadableSpan {
  const [span] = spansNamed(exporter, name);
  if (span === undefined) {
    throw new Error(`expected an exported "${name}" span; exported: ${exportedNames(exporter)}`);
  }
  return span;
}

/**
 * Text a hostile (or merely outdated) caller might offer as an error message: a URL with a
 * credential, a JWT and an e-mail address. `ErrorReport` has no `message` field any more
 * (D-S69-Q2), so the specs below hand it over through a cast to prove the span never carries it.
 */
function hostileErrorText(): string {
  const url = `https://acme.example.test/v1/runs?token=${jwtSample()}`;
  return `POST ${url} failed for ops@example.com`;
}

/** A report of any source carrying hostile `message` text, as a stale caller would build it. */
function hostileReport(source: ErrorSource, text: string): ErrorReport {
  const stale: ErrorReport & { readonly message: string } = {
    source,
    type: 'TypeError',
    message: text,
  };
  return stale;
}

/** The span names an exporter holds, for a failure message that says what did arrive. */
function exportedNames(exporter: InMemorySpanExporter): string {
  return exporter
    .getFinishedSpans()
    .map((span) => span.name)
    .join(', ');
}

describe('startTelemetrySdk', () => {
  it('starts a root route.change span and ends it with the route template', () => {
    const harness = startSdk({ locale: 'vi' });
    const change = harness.runtime.beginRouteChange();
    change.end('ok', '/projects/:projectId/runs');

    const span = firstSpanNamed(harness.exporter, 'route.change');

    expect(span.parentSpanContext).toBeUndefined();
    expect(span.kind).toBe(SpanKind.INTERNAL);
    expect(span.attributes['evidgate.route']).toBe('/projects/:projectId/runs');
    expect(span.attributes['evidgate.outcome']).toBe('ok');
    expect(span.attributes['evidgate.locale']).toBe('vi');
    expect(span.status.code).toBe(SpanStatusCode.UNSET);
    expect(span.resource.attributes['service.name']).toBe('evidgate-web');
    expect(span.events).toHaveLength(0);
  });

  it('records service.version on the resource when the facade passes one', () => {
    const harness = startSdk({ serviceVersion: '2.4.1' });
    harness.runtime.beginRouteChange().end('ok', '/');

    expect(
      firstSpanNamed(harness.exporter, 'route.change').resource.attributes['service.version'],
    ).toBe('2.4.1');
  });

  it('drops a locale that is not en or vi instead of exporting it', () => {
    const harness = startSdk({ locale: 'fr' });
    harness.runtime.beginRouteChange().end('ok', '/');

    expect(
      firstSpanNamed(harness.exporter, 'route.change').attributes['evidgate.locale'],
    ).toBeUndefined();
  });

  it('closes a navigation that never ended as cancelled and keeps its route', () => {
    const harness = startSdk();
    harness.runtime.setRoute('/projects/:projectId');
    harness.runtime.beginRouteChange();
    harness.runtime.beginRouteChange().end('ok', '/runs');

    const [cancelled, ended] = spansNamed(harness.exporter, 'route.change');

    expect(cancelled?.attributes['evidgate.outcome']).toBe('cancelled');
    expect(cancelled?.attributes['evidgate.route']).toBe('/projects/:projectId');
    expect(ended?.attributes['evidgate.outcome']).toBe('ok');
    expect(ended?.attributes['evidgate.route']).toBe('/runs');
  });

  it('ends a route change once, however often the facade calls end', () => {
    const harness = startSdk();
    const change = harness.runtime.beginRouteChange();
    change.end('ok', '/a');
    change.end('error', '/b');
    harness.runtime.flushVitals();

    const spans = spansNamed(harness.exporter, 'route.change');

    expect(spans).toHaveLength(1);
    expect(spans[0]?.attributes['evidgate.outcome']).toBe('ok');
  });

  it('keeps one trace id from the route change to the API call', () => {
    const harness = startSdk();
    harness.runtime.beginRouteChange().end('ok', '/projects/:projectId/runs');
    const call = harness.runtime.startApiCall({ method: 'GET', route: '/v1/projects/:id/runs' });
    const traceparent = call.traceparent;
    call.end({ status: 200, outcome: 'ok', requestId: REQUEST_ID });

    const routeSpan = firstSpanNamed(harness.exporter, 'route.change');
    const apiSpan = firstSpanNamed(harness.exporter, 'api.request');
    const routeContext = routeSpan.spanContext();
    const apiContext = apiSpan.spanContext();

    expect(apiContext.traceId).toBe(routeContext.traceId);
    expect(traceparent).toBe(`00-${apiContext.traceId}-${apiContext.spanId}-01`);
    expect(apiSpan.parentSpanContext?.spanId).toBe(routeContext.spanId);
    expect(apiSpan.kind).toBe(SpanKind.CLIENT);
    expect(apiSpan.attributes['http.request.method']).toBe('GET');
    expect(apiSpan.attributes['http.route']).toBe('/v1/projects/:id/runs');
    expect(apiSpan.attributes['http.response.status_code']).toBe(200);
    expect(apiSpan.attributes['evidgate.request_id']).toBe(REQUEST_ID);
    expect(apiSpan.attributes['evidgate.outcome']).toBe('ok');
    expect(apiSpan.status.code).toBe(SpanStatusCode.UNSET);
  });

  it('starts its own root trace before any route change exists', () => {
    const harness = startSdk();
    const call = harness.runtime.startApiCall({ method: 'POST', route: '/v1/login' });
    call.end({
      status: 401,
      outcome: 'error',
      errorCode: 'unauthenticated',
      errorKind: 'unauthenticated',
      retryable: false,
    });

    const span = firstSpanNamed(harness.exporter, 'api.request');

    expect(span.parentSpanContext).toBeUndefined();
    expect(span.attributes['http.response.status_code']).toBe(401);
    expect(span.attributes['evidgate.api.error_code']).toBe('unauthenticated');
    expect(span.attributes['evidgate.api.retryable']).toBe(false);
    expect(span.status.code).toBe(SpanStatusCode.ERROR);
  });

  it('marks a transport failure as an error without a status code', () => {
    const harness = startSdk();
    harness.runtime.startApiCall({ method: 'GET', route: '/v1/runs' }).end({
      status: 0,
      outcome: 'error',
      errorKind: 'network',
    });

    const span = firstSpanNamed(harness.exporter, 'api.request');

    expect(span.status.code).toBe(SpanStatusCode.ERROR);
    // 0 is not a status code: the scrubber drops it rather than exporting a lie.
    expect(span.attributes['http.response.status_code']).toBeUndefined();
    expect(span.attributes['evidgate.api.error_kind']).toBe('network');
  });

  it('records a cancellation as an outcome, not as an error', () => {
    const harness = startSdk();
    harness.runtime
      .startApiCall({ method: 'GET', route: '/v1/runs' })
      .end({ outcome: 'cancelled' });

    const span = firstSpanNamed(harness.exporter, 'api.request');

    expect(span.attributes['evidgate.outcome']).toBe('cancelled');
    expect(span.status.code).toBe(SpanStatusCode.UNSET);
  });

  it('samples no root trace at ratio 0 and still hands out a valid traceparent', () => {
    const harness = startSdk({ sampleRatio: 0 });
    const change = harness.runtime.beginRouteChange();
    const call = harness.runtime.startApiCall({ method: 'GET', route: '/v1/runs' });
    const traceparent = call.traceparent;
    call.end({ status: 200, outcome: 'ok' });
    change.end('ok', '/runs');

    expect(traceparent).toMatch(/^00-[0-9a-f]{32}-[0-9a-f]{16}-00$/);
    expect(harness.exporter.getFinishedSpans()).toHaveLength(0);
  });

  it('keeps the sampled flag on the traceparent at ratio 1', () => {
    const harness = startSdk({ sampleRatio: 1 });
    harness.runtime.beginRouteChange();
    const call = harness.runtime.startApiCall({ method: 'GET', route: '/v1/runs' });
    call.end({ status: 200, outcome: 'ok' });

    expect(call.traceparent).toMatch(/^00-[0-9a-f]{32}-[0-9a-f]{16}-01$/);
    expect(spansNamed(harness.exporter, 'api.request')).toHaveLength(1);
  });

  it('reports a client error with its classification and the current route', () => {
    const harness = startSdk();
    harness.runtime.setRoute('/projects/:projectId');
    harness.runtime.beginRouteChange().end('ok', '/projects/:projectId');
    harness.runtime.reportError({ source: 'angular', type: 'TypeError' });
    harness.runtime.reportError({ source: 'unhandledrejection', type: 'RangeError' });
    harness.runtime.reportError({ source: 'window.error', type: 'ReferenceError' });

    const [angular, rejection, windowError] = spansNamed(harness.exporter, 'client.error');

    expect(angular?.attributes['evidgate.error.source']).toBe('angular');
    expect(angular?.attributes['error.type']).toBe('TypeError');
    expect(angular?.attributes['evidgate.route']).toBe('/projects/:projectId');
    expect(angular?.status.code).toBe(SpanStatusCode.ERROR);
    expect(rejection?.attributes['error.type']).toBe('RangeError');
    expect(windowError?.attributes['evidgate.error.source']).toBe('window.error');
  });

  it('keeps an API error envelope and never its message', () => {
    const harness = startSdk();
    harness.runtime.reportError({
      source: 'api',
      type: 'ApiRequestError',
      status: 404,
      errorKind: 'not_found',
      errorCode: 'run_not_found',
      retryable: false,
      requestId: REQUEST_ID,
    });

    const span = firstSpanNamed(harness.exporter, 'client.error');

    expect(span.attributes['error.message']).toBeUndefined();
    expect(span.attributes['http.response.status_code']).toBe(404);
    expect(span.attributes['evidgate.api.error_kind']).toBe('not_found');
    expect(span.attributes['evidgate.api.error_code']).toBe('run_not_found');
    expect(span.attributes['evidgate.api.retryable']).toBe(false);
    expect(span.attributes['evidgate.request_id']).toBe(REQUEST_ID);
    // No stack trace, no exception event and no links: an error span carries a classification.
    expect(span.events).toHaveLength(0);
    expect(span.links).toHaveLength(0);
  });

  it('a client.error span carries no message attribute, for any source', () => {
    const harness = startSdk();
    const text = hostileErrorText();
    for (const source of ['angular', 'window.error', 'unhandledrejection', 'api'] as const) {
      harness.runtime.reportError(hostileReport(source, text));
    }

    const spans = spansNamed(harness.exporter, 'client.error');
    expect(spans).toHaveLength(4);
    for (const span of spans) {
      // Structural: no key, whatever it is called, mentions a message.
      for (const key of Object.keys(span.attributes)) {
        expect(key.toLowerCase()).not.toContain('message');
      }
      // Textual: none of what the caller offered survives in the serialised span.
      const serialised = JSON.stringify({
        name: span.name,
        attributes: span.attributes,
        events: span.events,
        status: span.status,
      });
      expect(serialised).not.toContain('acme.example.test');
      expect(serialised).not.toContain('token=');
      expect(serialised).not.toContain('@example.com');
      expect(serialised).not.toContain('failed for');
    }
  });

  it('adds no attribute for the text a thrown error offered', () => {
    const harness = startSdk();
    harness.runtime.setRoute('/projects/:projectId/runs');
    harness.runtime.reportError(hostileReport('window.error', hostileErrorText()));

    const [span] = spansNamed(harness.exporter, 'client.error');
    if (span === undefined) {
      throw new Error('expected an exported "client.error" span');
    }
    // Exactly the classification, the source and the route: the hostile text bought no key.
    expect(Object.keys(span.attributes).sort()).toEqual([
      'error.type',
      'evidgate.error.source',
      'evidgate.route',
    ]);
  });

  it('templates a concrete id that reaches a span through setRoute', () => {
    const harness = startSdk();
    harness.runtime.setRoute(`/projects/${REQUEST_ID}/runs`);
    harness.runtime.reportError({ source: 'angular', type: 'Error' });

    expect(firstSpanNamed(harness.exporter, 'client.error').attributes['evidgate.route']).toBe(
      '/projects/:id/runs',
    );
  });

  it('emits one web_vital span per dirty metric, attributed to the current route', () => {
    const harness = startSdk();
    harness.runtime.setRoute('/projects/:projectId/runs');
    harness.runtime.beginRouteChange().end('ok', '/projects/:projectId/runs');

    harness.vitals.report('LCP', 1200);
    harness.vitals.report('CLS', 0.05, 'needs-improvement');
    harness.runtime.flushVitals();

    const spans = spansNamed(harness.exporter, 'web_vital');

    expect(spans.map((span) => span.attributes['web_vital.name'])).toEqual(['LCP', 'CLS']);
    expect(spans[0]?.attributes['web_vital.value']).toBe(1200);
    expect(spans[0]?.attributes['web_vital.rating']).toBe('good');
    expect(spans[0]?.attributes['web_vital.navigation_type']).toBe('navigate');
    expect(spans[0]?.attributes['evidgate.route']).toBe('/projects/:projectId/runs');
    expect(spans[0]?.parentSpanContext?.traceId).toBe(
      firstSpanNamed(harness.exporter, 'route.change').spanContext().traceId,
    );
    expect(spans[1]?.attributes['web_vital.rating']).toBe('needs-improvement');
  });

  it('reports every change of the cumulative metrics and only the final candidate of LCP', () => {
    const harness = startSdk();

    // LCP is finalised by the browser on the first input or on hide, so this app never asks for
    // early candidates; INP and CLS are cumulative, so an intermediate value is the current best.
    expect(harness.vitals.options.LCP).toBeUndefined();
    expect(harness.vitals.options.INP).toEqual({ reportAllChanges: true });
    expect(harness.vitals.options.CLS).toEqual({ reportAllChanges: true });

    void harness.runtime.shutdown();
  });

  it('dates a route trace with the timestamps it is given', () => {
    const harness = startSdk();
    const endedAt = Date.now();
    const startedAt = endedAt - 40;

    // The facade replays a navigation that happened before the chunk arrived, so the span must
    // carry that navigation's times rather than the time the replay ran.
    harness.runtime.beginRouteChange(startedAt).end('ok', '/runs', endedAt);

    const [span] = spansNamed(harness.exporter, 'route.change');
    if (span === undefined) {
      throw new Error('expected an exported "route.change" span');
    }
    const durationMs = span.duration[0] * 1e3 + span.duration[1] / 1e6;
    expect(durationMs).toBeGreaterThanOrEqual(40);
    expect(durationMs).toBeLessThan(4000);
    expect(span.startTime[0] * 1e3 + span.startTime[1] / 1e6).toBeLessThanOrEqual(startedAt + 1);
  });

  it('exports through forceFlush, and swallows an exporter that cannot flush', async () => {
    // An exporter whose own `forceFlush` rejects is the only way this test bites: the synchronous
    // processor already exported the span in `onEnd`, so a plain assertion on the exported span
    // would pass with `forceFlush`'s body deleted. What it must do is reach the exporter and keep
    // a failure out of the page (an unhandled rejection fails this file).
    const log: string[] = [];
    const exporter: SpanExporter = {
      export(spans, resultCallback): void {
        for (const span of spans) {
          log.push(`export:${span.name}`);
        }
        resultCallback({ code: 0 });
      },
      shutdown: () => Promise.resolve(),
      forceFlush: () => {
        log.push('forceFlush');
        return Promise.reject(new Error('exporter unavailable'));
      },
    };
    const vitals = vitalProbe();
    const runtime = startTelemetrySdk(
      { endpoint: null, sampleRatio: 1 },
      { exporter, vitalReporters: vitals.reporters },
    );

    vitals.report('CLS', 0.05);
    runtime.flushVitals();
    expect(() => {
      runtime.forceFlush();
    }).not.toThrow();

    // `processor.forceFlush()` awaits its pending exports before it reaches the exporter, so the
    // delegation lands on a later task; letting it settle also surfaces a missing `.catch` as an
    // unhandled rejection, which fails this file.
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });

    expect(log).toEqual(['export:web_vital', 'forceFlush']);
  });

  it('does not re-emit an unchanged metric, and emits LCP only once', () => {
    const harness = startSdk();
    harness.vitals.report('LCP', 1200);
    harness.runtime.flushVitals();
    harness.runtime.flushVitals();
    harness.vitals.report('LCP', 1400);
    harness.runtime.flushVitals();

    expect(spansNamed(harness.exporter, 'web_vital')).toHaveLength(1);

    harness.vitals.report('INP', 90, 'needs-improvement');
    harness.runtime.flushVitals();

    const names = spansNamed(harness.exporter, 'web_vital').map(
      (span) => span.attributes['web_vital.name'],
    );
    expect(names).toEqual(['LCP', 'INP']);
  });

  it('flushes the dirty vitals on shutdown, then stops exporting', async () => {
    // Not `InMemorySpanExporter`: it clears its buffer when the provider shuts it down, and the
    // question here is what shutdown exported. This exporter keeps the names it was handed.
    const exported: string[] = [];
    const exporter: SpanExporter = {
      export(spans, resultCallback): void {
        for (const span of spans) {
          exported.push(span.name);
        }
        resultCallback({ code: 0 });
      },
      shutdown: () => Promise.resolve(),
      forceFlush: () => Promise.resolve(),
    };
    const vitals = vitalProbe();
    const runtime = startTelemetrySdk(
      { endpoint: null, sampleRatio: 1 },
      { exporter, vitalReporters: vitals.reporters },
    );
    vitals.report('INP', 240, 'poor');

    await runtime.shutdown();

    expect(exported).toEqual(['web_vital']);
    runtime.reportError({ source: 'angular', type: 'Error' });
    expect(exported).toEqual(['web_vital']);
  });

  it('survives a reporter set that throws (a browser without PerformanceObserver)', () => {
    const exporter = new InMemorySpanExporter();
    const runtime = startTelemetrySdk(
      { endpoint: null, sampleRatio: 1 },
      {
        exporter,
        vitalReporters: {
          onLCP: () => {
            throw new Error('no PerformanceObserver');
          },
          onINP: () => {
            throw new Error('no PerformanceObserver');
          },
          onCLS: () => {
            throw new Error('no PerformanceObserver');
          },
        },
      },
    );

    runtime.beginRouteChange().end('ok', '/runs');

    expect(spansNamed(exporter, 'route.change')).toHaveLength(1);
  });
});
