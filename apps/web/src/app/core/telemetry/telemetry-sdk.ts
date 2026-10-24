// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: one trace id end to end, Web
// Vitals per route, no PII): the ONLY module of the app that imports `@opentelemetry/*` runtime
// code and `web-vitals`. `telemetry.service.ts` reaches this file through a dynamic import, so
// the SDK, the OTLP exporter and web-vitals form a lazy chunk of their own and never enter the
// initial bundle (`check-bundle-budget.mjs` holds the initial one).
//
// The provider is private to this runtime: the global tracer provider and the global context
// manager are never set, so a spec that starts and shuts a runtime down cannot leak a sampler, a
// processor or an active span into the next spec. Parent links are explicit contexts
// (`trace.setSpan(ROOT_CONTEXT, span)` passed to `startSpan`), which is what keeps every
// `api.request` and `client.error` span inside the trace of the route change it belongs to, and
// what makes the W3C `traceparent` handed back by `startApiCall` continue that same trace on the
// server.
//
// Nothing here throws into the app: the facade calls through a nullable runtime, and the vitals
// registration is wrapped so a browser without `PerformanceObserver` (or with it disabled) still
// gets route and API tracing.
import {
  ROOT_CONTEXT,
  SpanKind,
  SpanStatusCode,
  TraceFlags,
  trace,
  type Attributes,
  type Context,
  type Span,
  type Tracer,
} from '@opentelemetry/api';
import { OTLPTraceExporter } from '@opentelemetry/exporter-trace-otlp-http';
import { resourceFromAttributes } from '@opentelemetry/resources';
import {
  BatchSpanProcessor,
  ParentBasedSampler,
  SimpleSpanProcessor,
  TraceIdRatioBasedSampler,
  TracerProvider,
  type SpanExporter,
} from '@opentelemetry/sdk-trace';
import { onCLS, onINP, onLCP, type Metric, type ReportOpts } from 'web-vitals';

import { ScrubbingSpanExporter } from './scrubbing-exporter';
import type { TelemetryConfig } from './telemetry-config';
import {
  API_SPAN_NAME,
  ERROR_SPAN_NAME,
  INSTRUMENTATION_NAME,
  ROUTE_SPAN_NAME,
  VITAL_SPAN_NAME,
} from './telemetry-names';
import { UNKNOWN_ROUTE } from './route-template';

/** `service.name` of every span this app exports. */
const SERVICE_NAME = 'evidgate-web';

/** W3C trace context version this facade emits (`00`, the only one defined). */
const TRACE_CONTEXT_VERSION = '00';

/** Outcome of a navigation or an API call; also the value of `evidgate.outcome`. */
export type TelemetryOutcome = 'ok' | 'cancelled' | 'error';

/** Where a reported client error happened; the value of `evidgate.error.source`. */
export type ErrorSource = 'window.error' | 'unhandledrejection' | 'angular' | 'api';

/** One route change in flight (`NavigationStart` … `NavigationEnd`/`Cancel`/`Skipped`/`Error`). */
export interface RouteChange {
  /**
   * Ends the route span with `outcome` and the route *template* (`/projects/:projectId/runs`).
   * Idempotent: a facade that ends a change twice (an explicit end plus the next
   * `beginRouteChange`) records one span. The context stays the parent of later API and error
   * spans until the next `beginRouteChange`.
   *
   * `endTime` (epoch milliseconds) is for a navigation the facade only learns about after it
   * finished — the first one, replayed once the chunk is ready — so the span keeps the duration
   * the router measured.
   */
  end(outcome: TelemetryOutcome, route: string, endTime?: number): void;
}

/** What an API call learned when it finished. Every field but `outcome` is optional. */
export interface ApiCallResult {
  /** HTTP status, when a response arrived; a transport failure has none. */
  status?: number;
  outcome: TelemetryOutcome;
  /** The server `error.code`, only when the body carried a well-formed envelope. */
  errorCode?: string;
  errorKind?: string;
  retryable?: boolean;
  /** The `X-Request-Id` UUID, when the response carried one. */
  requestId?: string;
}

/** One API call in flight; `traceparent` is fixed when the span is created. */
export interface ApiCall {
  /**
   * W3C header value `00-<32 hex trace id>-<16 hex span id>-<01|00>`: the trace of the current
   * route change, the span of this call, and `01` only when the trace is sampled.
   */
  readonly traceparent: string;
  /** Ends the span; idempotent, so a cancel after a response records one span. */
  end(result: ApiCallResult): void;
}

/** A client error the facade wants a `client.error` span for. */
export interface ErrorReport {
  source: ErrorSource;
  /** The error's constructor name (`TypeError`, `ApiRequestError`, …) or `Error`. */
  type: string;
  status?: number;
  errorKind?: string;
  errorCode?: string;
  retryable?: boolean;
  requestId?: string;
}

/** The handle the facade holds: the whole runtime surface, all of it optional to call. */
export interface TelemetryRuntime {
  /**
   * Opens a new root trace for a navigation, ending the previous unfinished one as cancelled.
   * `startTime` (epoch milliseconds) backdates the span for a replayed navigation.
   */
  beginRouteChange(startTime?: number): RouteChange;
  /** Remembers the matched route template; later vitals and errors are attributed to it. */
  setRoute(route: string): void;
  /** Opens a CLIENT `api.request` span as a child of the current route trace. */
  startApiCall(input: { method: string; route: string }): ApiCall;
  /** Records a `client.error` span: source, type and ids; no message text, ever. */
  reportError(report: ErrorReport): void;
  /** Emits one `web_vital` span per metric that changed since the last flush. */
  flushVitals(): void;
  /**
   * Asks the processor to export what it still holds. `BatchSpanProcessor` installs its own
   * page-leave listeners when it is constructed, so a `web_vital` span created by the facade
   * afterwards would otherwise wait for the next batch timer: the facade flushes, then calls this.
   * Never throws; a failing export is the exporter's problem, not the page's.
   */
  forceFlush(): void;
  /**
   * Stops exporting without stopping tracing: the queue is dropped and every later span goes to
   * a no-op exporter, while span ids and the `traceparent` handed to the server keep working.
   * Idempotent; the facade calls it when the app CSP blocks the exporter origin.
   */
  disableExport(): void;
  /** Flushes vitals, then shuts the provider down. */
  shutdown(): Promise<void>;
}

/** One `web-vitals` reporter (`onLCP`/`onINP`/`onCLS`), by signature. */
export type VitalReporter = (onReport: (metric: Metric) => void, opts?: ReportOpts) => void;

/** The three reporter functions; a seam because jsdom has no `PerformanceObserver`. */
export interface VitalReporters {
  readonly onLCP: VitalReporter;
  readonly onINP: VitalReporter;
  readonly onCLS: VitalReporter;
}

/**
 * The knobs `startTelemetrySdk` accepts besides {@link TelemetryConfig}.
 *
 * `exporter` and `vitalReporters` are test seams, both defaulted in production: a spec hands in
 * an `InMemorySpanExporter` (so it reads spans instead of the network) and, because `web-vitals`
 * needs a `PerformanceObserver` that jsdom does not implement, a reporter set it can drive.
 */
export interface TelemetrySdkOptions {
  /** Overrides the exporter the config would build; also switches to the synchronous processor. */
  exporter?: SpanExporter;
  /** `en`/`vi`; recorded on the route span as `evidgate.locale`. */
  locale?: string;
  /** Recorded as `service.version` on the resource. */
  serviceVersion?: string;
  /** Test seam: replaces the three `web-vitals` reporters. */
  vitalReporters?: VitalReporters;
}

/** A metric's latest value, and whether it changed since the last flush. */
interface VitalSnapshot {
  readonly value: number;
  readonly rating: string;
  readonly navigationType: string;
  dirty: boolean;
}

type VitalName = 'LCP' | 'INP' | 'CLS';

/** Registration and flush order; one span per metric, never two for one value. */
const VITAL_NAMES: readonly VitalName[] = ['LCP', 'INP', 'CLS'];

/** The production reporters (`web-vitals` itself). */
const WEB_VITALS_REPORTERS: VitalReporters = { onLCP, onINP, onCLS };

/**
 * INP and CLS are page-cumulative (worst interaction, layout shift across the visit), so the
 * running value is what the next flush should carry. LCP is registered *without* this option:
 * `web-vitals` finalises it on the first user input or when the page is hidden, and freezing an
 * early candidate would report a metric the browser itself does not consider final.
 */
const CUMULATIVE_VITAL_OPTIONS: ReportOpts = { reportAllChanges: true };

/**
 * The exporter used when no OTLP endpoint is configured: it accepts every export and drops it.
 * Tracing still runs — ids, `traceparent` flags and the route/api parent links are all local —
 * and no network call is ever made.
 *
 * `ExportResult.code` is `0` (`ExportResultCode.SUCCESS`); naming that enum would import
 * `@opentelemetry/core`, which this app does not depend on directly.
 */
const NOOP_EXPORTER: SpanExporter = {
  export(_spans, resultCallback): void {
    resultCallback({ code: 0 });
  },
  shutdown(): Promise<void> {
    return Promise.resolve();
  },
  forceFlush(): Promise<void> {
    return Promise.resolve();
  },
};

/** The `evidgate.locale` attribute, only when the id is one the exporter accepts (`en`/`vi`). */
function localeAttributes(locale: string | undefined): Attributes {
  return locale === 'en' || locale === 'vi' ? { 'evidgate.locale': locale } : {};
}

/** The W3C `traceparent` of a span: version, its ids, and the sampled bit. */
function traceparentOf(span: Span): string {
  const spanContext = span.spanContext();
  const sampled = (spanContext.traceFlags & TraceFlags.SAMPLED) !== 0;
  return `${TRACE_CONTEXT_VERSION}-${spanContext.traceId}-${spanContext.spanId}-${sampled ? '01' : '00'}`;
}

/**
 * Starts the tracing runtime: a private `TracerProvider` whose root decision is
 * `TraceIdRatioBasedSampler(config.sampleRatio)` and whose every child follows its parent. The
 * exporter is always wrapped in {@link ScrubbingSpanExporter}, so no span leaves this function
 * unscrubbed; with a test exporter the processor is synchronous (`SimpleSpanProcessor`), which
 * is what lets a spec read a span the instant it ends.
 *
 * @param config where to export and what fraction of route traces to keep.
 * @param options test seams and the two identity values (`locale`, `serviceVersion`).
 */
export function startTelemetrySdk(
  config: TelemetryConfig,
  options: TelemetrySdkOptions = {},
): TelemetryRuntime {
  const innerExporter =
    options.exporter ??
    (config.endpoint === null ? NOOP_EXPORTER : new OTLPTraceExporter({ url: config.endpoint }));
  /**
   * Where the processor sends spans. A CSP violation can point it at the no-op exporter after the
   * provider is built, which is the only way to stop the POSTs without ending the trace.
   */
  let exportTarget: SpanExporter = new ScrubbingSpanExporter(innerExporter);
  const switchableExporter: SpanExporter = {
    export(spans, resultCallback): void {
      exportTarget.export(spans, resultCallback);
    },
    shutdown: () => exportTarget.shutdown(),
    forceFlush: () => exportTarget.forceFlush?.() ?? Promise.resolve(),
  };
  const processor =
    options.exporter === undefined
      ? new BatchSpanProcessor({ exporter: switchableExporter })
      : new SimpleSpanProcessor({ exporter: switchableExporter });
  const resourceAttributes: Attributes = { 'service.name': SERVICE_NAME };
  if (options.serviceVersion !== undefined) {
    resourceAttributes['service.version'] = options.serviceVersion;
  }
  const provider = new TracerProvider({
    sampler: new ParentBasedSampler({ root: new TraceIdRatioBasedSampler(config.sampleRatio) }),
    resource: resourceFromAttributes(resourceAttributes),
    spanProcessors: [processor],
  });
  const tracer: Tracer = provider.getTracer(INSTRUMENTATION_NAME, options.serviceVersion);

  /** The span of the route change whose trace API and error spans continue. */
  let routeSpan: Span | null = null;
  /** `routeSpan` as a parent context; `ROOT_CONTEXT` until the first route change. */
  let routeContext: Context = ROOT_CONTEXT;
  /** The change the facade has not ended yet, so a new navigation can cancel it. */
  let openRouteChange: RouteChange | null = null;
  /** The last matched route template: the attribution of vitals and client errors. */
  let currentRoute: string = UNKNOWN_ROUTE;

  const vitals: Record<VitalName, VitalSnapshot | null> = { LCP: null, INP: null, CLS: null };
  /** `LCP` is emitted at most once per page, whatever the browser reports afterwards. */
  let lcpFlushed = false;
  /** Set by {@link disableExport}; keeps a second call from flushing the queue twice. */
  let exportDisabled = false;

  function parentContext(): Context {
    return routeSpan === null ? ROOT_CONTEXT : routeContext;
  }

  function setRoute(route: string): void {
    currentRoute = route;
  }

  function beginRouteChange(startTime?: number): RouteChange {
    // A navigation that never reached `NavigationEnd` (a redirect, a guard, a crashed resolver)
    // still gets a span: it is the previous route trace, closed as cancelled.
    openRouteChange?.end('cancelled', currentRoute);
    const span = tracer.startSpan(
      ROUTE_SPAN_NAME,
      {
        kind: SpanKind.INTERNAL,
        attributes: localeAttributes(options.locale),
        ...(startTime === undefined ? {} : { startTime }),
      },
      ROOT_CONTEXT,
    );
    routeSpan = span;
    routeContext = trace.setSpan(ROOT_CONTEXT, span);
    let ended = false;
    const change: RouteChange = {
      end(outcome: TelemetryOutcome, route: string, endTime?: number): void {
        if (ended) {
          return;
        }
        ended = true;
        span.setAttribute('evidgate.route', route);
        span.setAttribute('evidgate.outcome', outcome);
        if (outcome === 'error') {
          span.setStatus({ code: SpanStatusCode.ERROR });
        }
        if (endTime === undefined) {
          span.end();
        } else {
          span.end(endTime);
        }
        if (openRouteChange === change) {
          openRouteChange = null;
        }
      },
    };
    openRouteChange = change;
    return change;
  }

  function startApiCall(input: { method: string; route: string }): ApiCall {
    const span = tracer.startSpan(
      API_SPAN_NAME,
      {
        kind: SpanKind.CLIENT,
        attributes: {
          ...localeAttributes(options.locale),
          'http.request.method': input.method,
          'http.route': input.route,
        },
      },
      parentContext(),
    );
    const traceparent = traceparentOf(span);
    let ended = false;
    return {
      traceparent,
      end(result: ApiCallResult): void {
        if (ended) {
          return;
        }
        ended = true;
        if (result.status !== undefined) {
          span.setAttribute('http.response.status_code', result.status);
        }
        if (result.requestId !== undefined) {
          span.setAttribute('evidgate.request_id', result.requestId);
        }
        if (result.errorCode !== undefined) {
          span.setAttribute('evidgate.api.error_code', result.errorCode);
        }
        if (result.errorKind !== undefined) {
          span.setAttribute('evidgate.api.error_kind', result.errorKind);
        }
        if (result.retryable !== undefined) {
          span.setAttribute('evidgate.api.retryable', result.retryable);
        }
        span.setAttribute('evidgate.outcome', result.outcome);
        // A refusal status is a failed call even if a caller passed `outcome: 'ok'`; a
        // transport failure (`outcome: 'error'`, no status) is one too.
        const statusIsRefusal = result.status !== undefined && result.status >= 400;
        if (result.outcome === 'error' || statusIsRefusal) {
          span.setStatus({ code: SpanStatusCode.ERROR });
        }
        span.end();
      },
    };
  }

  function reportError(report: ErrorReport): void {
    const attributes: Attributes = {
      ...localeAttributes(options.locale),
      'evidgate.error.source': report.source,
      'error.type': report.type,
      'evidgate.route': currentRoute,
    };
    // No message text at all (D-S69-Q2): a `client.error` span is a classification and ids, so
    // the report has nothing to offer here and the allowlist has no key for it either.
    if (report.status !== undefined) {
      attributes['http.response.status_code'] = report.status;
    }
    if (report.errorCode !== undefined) {
      attributes['evidgate.api.error_code'] = report.errorCode;
    }
    if (report.errorKind !== undefined) {
      attributes['evidgate.api.error_kind'] = report.errorKind;
    }
    if (report.retryable !== undefined) {
      attributes['evidgate.api.retryable'] = report.retryable;
    }
    if (report.requestId !== undefined) {
      attributes['evidgate.request_id'] = report.requestId;
    }
    const span = tracer.startSpan(
      ERROR_SPAN_NAME,
      { kind: SpanKind.INTERNAL, attributes },
      parentContext(),
    );
    // No exception event and no stack trace, and no message text: a `client.error` span carries
    // the classification, the ids and the current route, nothing the error said.
    span.setStatus({ code: SpanStatusCode.ERROR });
    span.end();
  }

  function recordVital(metric: Metric): void {
    const name = metric.name;
    if (name !== 'LCP' && name !== 'INP' && name !== 'CLS') {
      return;
    }
    vitals[name] = {
      value: metric.value,
      rating: metric.rating,
      navigationType: metric.navigationType,
      dirty: true,
    };
  }

  const reporters = options.vitalReporters ?? WEB_VITALS_REPORTERS;
  try {
    reporters.onLCP(recordVital);
    reporters.onINP(recordVital, CUMULATIVE_VITAL_OPTIONS);
    reporters.onCLS(recordVital, CUMULATIVE_VITAL_OPTIONS);
  } catch {
    // A browser without `PerformanceObserver` (or with observability disabled by policy) must
    // still get route and API tracing; only the vitals of this page are lost.
  }

  function flushVitals(): void {
    for (const name of VITAL_NAMES) {
      const vital = vitals[name];
      if (vital === null || vital.dirty === false) {
        continue;
      }
      if (name === 'LCP') {
        if (lcpFlushed) {
          vital.dirty = false;
          continue;
        }
        lcpFlushed = true;
      }
      const span = tracer.startSpan(
        VITAL_SPAN_NAME,
        {
          kind: SpanKind.INTERNAL,
          attributes: {
            ...localeAttributes(options.locale),
            'web_vital.name': name,
            'web_vital.value': vital.value,
            'web_vital.rating': vital.rating,
            'web_vital.navigation_type': vital.navigationType,
            'evidgate.route': currentRoute,
          },
        },
        parentContext(),
      );
      span.end();
      vital.dirty = false;
    }
  }

  /** Exports the queue now; a rejected flush stays inside the exporter. */
  function forceFlush(): void {
    void processor.forceFlush().catch(() => undefined);
  }

  /**
   * Points the processor at the no-op exporter. Whatever the batch queue still holds leaves it
   * now (and is dropped), so no POST to a blocked origin is retried; the sampler, the span ids and
   * `traceparentOf` are untouched, which is what keeps the server-side trace continuing.
   */
  function disableExport(): void {
    if (exportDisabled) {
      return;
    }
    exportDisabled = true;
    exportTarget = NOOP_EXPORTER;
    forceFlush();
  }

  async function shutdown(): Promise<void> {
    flushVitals();
    await provider.shutdown();
  }

  return {
    beginRouteChange,
    setRoute,
    startApiCall,
    reportError,
    flushVitals,
    forceFlush,
    disableExport,
    shutdown,
  };
}
