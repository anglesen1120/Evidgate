// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: telemetry must never break the
// app): the thin facade the app injects. It is part of the initial bundle and holds no SDK code:
// `@opentelemetry/*` and `web-vitals` arrive through `import('./telemetry-sdk')`, which the
// bundler puts in a lazy chunk (`telemetry-sdk` is referenced from nowhere else).
//
// Two consequences shape this file. Every call is answered from a nullable runtime: before the
// chunk has loaded (and after a load failure) each method is a no-op, so a page whose telemetry
// chunk 404s still routes, calls the API and reports errors exactly as before. And the load
// itself is fire-and-forget: `start()` returns immediately rather than blocking bootstrap, which
// is why `provideTelemetry()` may run it from an app initializer.
//
// The loader is an injection token rather than a bare `import(...)` so that a spec can hand in a
// loader built around an in-memory exporter; the default value is the real dynamic import.
import { DOCUMENT } from '@angular/common';
import { inject, Injectable, InjectionToken, LOCALE_ID } from '@angular/core';
import {
  NavigationCancel,
  NavigationEnd,
  NavigationError,
  NavigationSkipped,
  NavigationStart,
  Router,
  type Event as RouterEvent,
} from '@angular/router';

import { routeTemplate, UNKNOWN_ROUTE } from './route-template';
import { TELEMETRY_CONFIG } from './telemetry-config';
import type { TelemetryConfig } from './telemetry-config';
import type {
  ApiCall,
  ErrorReport,
  RouteChange,
  TelemetryOutcome,
  TelemetryRuntime,
  startTelemetrySdk,
} from './telemetry-sdk';

/** The lazily imported chunk: exactly the SDK entry point's signature. */
export type TelemetrySdkLoader = () => Promise<{ startTelemetrySdk: typeof startTelemetrySdk }>;

/**
 * How the facade reaches the telemetry chunk. The default is the dynamic import; a spec replaces
 * it with a loader that starts the real SDK against an `InMemorySpanExporter`, which is the only
 * way to assert what the app exported without a collector.
 */
export const TELEMETRY_SDK_LOADER = new InjectionToken<TelemetrySdkLoader>(
  'evidgate.telemetry-sdk-loader',
  {
    providedIn: 'root',
    factory: (): TelemetrySdkLoader => () => import('./telemetry-sdk'),
  },
);

/** The three states of the chunk; `disabled` is terminal, so a failure warns once. */
type LoadState = 'idle' | 'loading' | 'ready' | 'disabled';

/**
 * The primary subtag of a locale id, lowercased: `LOCALE_ID` defaults to `en-US`, while the trace
 * attribute is a closed set (`en`, `vi`), so `en-US` and `vi-VN` are reported as `en` and `vi`.
 */
function primarySubtag(localeId: string): string {
  const [primary] = localeId.trim().toLowerCase().split('-');
  return primary ?? '';
}

/** Route changes, API calls and client errors, traced from one facade. */
@Injectable({ providedIn: 'root' })
export class TelemetryService {
  private readonly document = inject(DOCUMENT);
  private readonly config: TelemetryConfig = inject(TELEMETRY_CONFIG);
  private readonly loader = inject(TELEMETRY_SDK_LOADER);
  private readonly locale = primarySubtag(inject(LOCALE_ID));
  /** Optional on purpose: an app without `provideRouter` traces API calls and errors only. */
  private readonly router = inject(Router, { optional: true });

  private state: LoadState = 'idle';
  private runtimeValue: TelemetryRuntime | null = null;
  /** The navigation in flight, so an unfinished one can be closed as cancelled. */
  private routeChange: RouteChange | null = null;
  /** The last route template; a cancelled navigation keeps the route it was leaving. */
  private currentRoute: string = UNKNOWN_ROUTE;

  /** The loaded runtime, or `null` while the chunk loads and after a load failure. */
  get runtime(): TelemetryRuntime | null {
    return this.runtimeValue;
  }

  /**
   * Loads the telemetry chunk and wires navigation, vitals and error listeners. Idempotent: the
   * second call (and every call after a failure) does nothing. Calling it without a `window`
   * (server-side rendering, a unit test with a null `defaultView`) is a no-op, never a throw.
   */
  start(): void {
    if (this.state !== 'idle') {
      return;
    }
    if (this.document.defaultView === null) {
      this.state = 'disabled';
      return;
    }
    this.state = 'loading';
    this.load().catch(() => this.disable());
  }

  /**
   * The span of one API call, or `null` when telemetry is not loaded. The caller reads
   * `traceparent` off the result and never waits for the runtime to exist.
   */
  startApiCall(input: { method: string; route: string }): ApiCall | null {
    return this.runtimeValue === null ? null : this.runtimeValue.startApiCall(input);
  }

  /** Records a client error; a no-op until the chunk has loaded. */
  reportError(report: ErrorReport): void {
    this.runtimeValue?.reportError(report);
  }

  private async load(): Promise<void> {
    const module = await this.loader();
    this.runtimeValue = module.startTelemetrySdk(this.config, { locale: this.locale });
    this.state = 'ready';
    this.wire();
  }

  /** Terminal state of a chunk that failed to load: one warning, then silence. */
  private disable(): void {
    if (this.state === 'disabled') {
      return;
    }
    this.state = 'disabled';
    this.runtimeValue = null;
    console.warn('Telemetry is unavailable; the app continues without it.');
  }

  private wire(): void {
    const view = this.document.defaultView;
    if (view !== null) {
      // Vitals are attributed to the route current when the page is left, which is the only
      // moment they are final: `pagehide` fires in the bfcache path too, so both are cheap.
      view.addEventListener('visibilitychange', () => {
        if (this.document.visibilityState === 'hidden') {
          this.runtimeValue?.flushVitals();
        }
      });
      view.addEventListener('pagehide', () => {
        this.runtimeValue?.flushVitals();
      });
    }
    const router = this.router;
    if (router !== null) {
      router.events.subscribe((event) => this.handleRouterEvent(event));
    }
  }

  private handleRouterEvent(event: RouterEvent): void {
    const router = this.router;
    const runtime = this.runtimeValue;
    if (router === null || runtime === null) {
      return;
    }
    if (event instanceof NavigationStart) {
      // The vitals of the route being left are final now (apps/web/README.md, "Vitals"), and
      // the flush goes first: the spans must be attributed to that route, not to the one that is
      // only just loading.
      runtime.flushVitals();
      // Closes the previous navigation as cancelled when it never ended.
      this.routeChange = runtime.beginRouteChange();
      return;
    }
    if (event instanceof NavigationEnd) {
      const template = routeTemplate(router.routerState.snapshot.root);
      this.currentRoute = template;
      runtime.setRoute(template);
      this.endRouteChange('ok', template);
      return;
    }
    if (event instanceof NavigationCancel || event instanceof NavigationSkipped) {
      this.endRouteChange('cancelled');
      return;
    }
    if (event instanceof NavigationError) {
      this.endRouteChange('error');
    }
  }

  private endRouteChange(outcome: TelemetryOutcome, route: string = this.currentRoute): void {
    const change = this.routeChange;
    this.routeChange = null;
    change?.end(outcome, route);
  }
}
