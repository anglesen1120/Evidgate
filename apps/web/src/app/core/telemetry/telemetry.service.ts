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
import type { Subscription } from 'rxjs';

import { routeTemplate, UNKNOWN_ROUTE, type RouteTreeNode } from './route-template';
import { TELEMETRY_CONFIG, TELEMETRY_OPT_OUT } from './telemetry-config';
import type { TelemetryConfig } from './telemetry-config';
import type { TelemetryEventName } from './telemetry-names';
import type {
  ApiCall,
  ErrorReport,
  RouteChange,
  TelemetryOutcome,
  TelemetryRuntime,
  startTelemetrySdk,
} from './telemetry-sdk';

/**
 * The route tree of a router that has not matched anything yet: `routeTemplate` reads it as the
 * unknown-route placeholder, which is what an unmatched router state reports as well.
 */
const EMPTY_ROUTE_ROOT: RouteTreeNode = {
  routeConfig: null,
  firstChild: null,
};

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

/** The origin of an absolute URL, or `null`; total, because an event carries whatever it carries. */
function originOf(value: string | null): string | null {
  if (value === null || value === '') {
    return null;
  }
  try {
    return new URL(value).origin;
  } catch {
    return null;
  }
}

/**
 * The members of a violation report the guard reads. Narrow on purpose: the browser sends a
 * `SecurityPolicyViolationEvent`, but a policed page can also see a plain event, and a missing
 * field must read as "not our block", never as a thrown error. Exported as the contract the
 * registered listener accepts, so a spec can call it with the fields a raised violation carries.
 */
export interface ViolationFields {
  readonly blockedURI?: string;
  readonly effectiveDirective?: string;
  readonly violatedDirective?: string;
  readonly disposition?: string;
  /**
   * Only a violation the browser itself raised counts: anything the page can construct reports
   * `isTrusted: false`, and a synthetic event must never switch telemetry off.
   */
  readonly isTrusted?: boolean;
}

/**
 * The navigation that happened before the chunk was ready. One slot, no queue: `Router.events`
 * has no replay, so without it the landing page would have no `route.change` at all, and its
 * vitals and errors would be attributed to `/unknown`.
 */
interface PendingNavigation {
  /** `Date.now()` of the `NavigationStart`; the replayed span keeps this start. */
  readonly startedAt: number;
  /** `Date.now()` of the terminal event, or `null` while the navigation is still in flight. */
  readonly endedAt: number | null;
  /** `ok` for a `NavigationEnd`; `cancelled`/`error` for the other terminal events. */
  readonly outcome: TelemetryOutcome;
  /** The route template matched when it ended, or `null` while it is in flight. */
  readonly route: string | null;
}

/** Route changes, API calls and client errors, traced from one facade. */
@Injectable({ providedIn: 'root' })
export class TelemetryService {
  private readonly document = inject(DOCUMENT);
  private readonly config: TelemetryConfig = inject(TELEMETRY_CONFIG);
  private readonly loader = inject(TELEMETRY_SDK_LOADER);
  private readonly optedOut = inject(TELEMETRY_OPT_OUT);
  private readonly locale = primarySubtag(inject(LOCALE_ID));
  /** Optional on purpose: an app without `provideRouter` traces API calls and errors only. */
  private readonly router = inject(Router, { optional: true });

  private state: LoadState = 'idle';
  private runtimeValue: TelemetryRuntime | null = null;
  /** The navigation in flight, so an unfinished one can be closed as cancelled. */
  private routeChange: RouteChange | null = null;
  /** The last route template; a cancelled navigation keeps the route it was leaving. */
  private currentRoute: string = UNKNOWN_ROUTE;
  /** Origin of the configured exporter, compared against the URI a CSP violation blocks. */
  private readonly endpointOrigin = originOf(this.config.endpoint);
  /** True once a CSP violation showed that export cannot leave the page; warns exactly once. */
  private exportDisabled = false;
  /** Every listener this facade owns, so a failed load can release all of them. */
  private routerSubscription: Subscription | null = null;
  /** The landing navigation, recorded until the runtime exists (see {@link PendingNavigation}). */
  private pendingNavigation: PendingNavigation | null = null;

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
    if (this.document.defaultView === null || this.optedOut) {
      // Opted out (Do-Not-Track / Global Privacy Control): nothing is loaded, subscribed or sent.
      this.state = 'disabled';
      return;
    }
    this.state = 'loading';
    this.listen();
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
    try {
      this.runtimeValue?.reportError(report);
    } catch {
      // telemetry loss stays silent
    }
  }

  /**
   * Records one named client event; a no-op until the chunk has loaded, when the visit opted out,
   * and after a failed load. Never throws: telemetry loss must not break the caller.
   */
  reportEvent(name: TelemetryEventName): void {
    try {
      this.runtimeValue?.reportEvent(name);
    } catch {
      // telemetry loss stays silent
    }
  }

  private async load(): Promise<void> {
    const module = await this.loader();
    this.runtimeValue = module.startTelemetrySdk(this.config, {
      locale: this.locale,
      ...(this.config.serviceVersion === undefined
        ? {}
        : { serviceVersion: this.config.serviceVersion }),
    });
    this.state = 'ready';
    if (this.exportDisabled) {
      this.runtimeValue.disableExport();
    }
    this.replayPendingNavigation();
  }

  /** Terminal state of a chunk that failed to load: one warning, then silence. */
  private disable(): void {
    if (this.state === 'disabled') {
      return;
    }
    this.state = 'disabled';
    this.runtimeValue = null;
    this.releaseListeners();
    console.warn('Telemetry is unavailable; the app continues without it.');
  }

  /**
   * The app cannot read the CSP header, so a blocked export is learned from the violation event:
   * the deploy allows exactly the exporter origin in `connect-src`. Only that directive and that
   * origin switch export off, and the warning names the directive alone — the URI could carry a
   * token in its query.
   */
  private readonly onSecurityPolicyViolation = (event: ViolationFields): void => {
    if (event.isTrusted !== true) {
      return;
    }
    const directive = event.effectiveDirective ?? event.violatedDirective ?? '';
    if (this.exportDisabled || event.disposition === 'report-only') {
      return;
    }
    if (!directive.startsWith('connect-src') || !this.blocksEndpoint(event.blockedURI ?? '')) {
      return;
    }
    this.exportDisabled = true;
    this.runtimeValue?.disableExport();
    console.warn(
      `Telemetry export is blocked by the Content-Security-Policy ${directive} directive; ` +
        'spans stay local.',
    );
  };

  /** Whether the blocked resource is the configured exporter: the same URL, or the same origin. */
  private blocksEndpoint(blockedURI: string): boolean {
    const endpoint = this.config.endpoint;
    if (endpoint === null || blockedURI === '') {
      return false;
    }
    return blockedURI === endpoint || originOf(blockedURI) === this.endpointOrigin;
  }

  /**
   * Every listener is registered before the chunk resolves: the router does not replay `events`,
   * and a CSP violation can arrive while the chunk is still loading. `releaseListeners` undoes
   * all of it.
   */
  private listen(): void {
    const view = this.document.defaultView;
    if (view !== null) {
      view.addEventListener('visibilitychange', this.onVisibilityChange);
      view.addEventListener('pagehide', this.onPageHide);
    }
    this.document.addEventListener('securitypolicyviolation', this.onSecurityPolicyViolation);
    this.routerSubscription =
      this.router?.events.subscribe((event) => this.handleRouterEvent(event)) ?? null;
  }

  /** Removes every listener this facade added; idempotent. */
  private releaseListeners(): void {
    const view = this.document.defaultView;
    if (view !== null) {
      view.removeEventListener('visibilitychange', this.onVisibilityChange);
      view.removeEventListener('pagehide', this.onPageHide);
    }
    this.document.removeEventListener('securitypolicyviolation', this.onSecurityPolicyViolation);
    this.routerSubscription?.unsubscribe();
    this.routerSubscription = null;
  }

  private readonly onVisibilityChange = (): void => {
    if (this.document.visibilityState === 'hidden') {
      this.flushVitalsNow();
    }
  };

  private readonly onPageHide = (): void => {
    this.flushVitalsNow();
  };

  /**
   * Emits the dirty vitals, then exports them. `BatchSpanProcessor` registers its own page-leave
   * listeners when it is constructed — before these spans exist — so without the explicit flush
   * they would sit in the queue until the batch timer, on a page that may already be gone.
   */
  private flushVitalsNow(): void {
    const runtime = this.runtimeValue;
    if (runtime === null) {
      return;
    }
    runtime.flushVitals();
    runtime.forceFlush();
  }

  private handleRouterEvent(event: RouterEvent): void {
    const runtime = this.runtimeValue;
    if (runtime === null) {
      this.recordWhileLoading(event);
      return;
    }
    const router = this.router;
    if (router === null) {
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

  /** Keeps the landing navigation in one slot while the chunk loads; a later one replaces it. */
  private recordWhileLoading(event: RouterEvent): void {
    if (event instanceof NavigationStart) {
      this.pendingNavigation = {
        startedAt: Date.now(),
        endedAt: null,
        outcome: 'cancelled',
        route: null,
      };
      return;
    }
    const pending = this.pendingNavigation;
    if (pending === null || pending.endedAt !== null) {
      return;
    }
    const endedAt = Date.now();
    if (event instanceof NavigationEnd) {
      this.pendingNavigation = {
        startedAt: pending.startedAt,
        endedAt,
        outcome: 'ok',
        route: routeTemplate(this.routerStateRoot()),
      };
      return;
    }
    if (
      event instanceof NavigationCancel ||
      event instanceof NavigationSkipped ||
      event instanceof NavigationError
    ) {
      this.pendingNavigation = {
        startedAt: pending.startedAt,
        endedAt,
        outcome: event instanceof NavigationError ? 'error' : 'cancelled',
        route: pending.route,
      };
    }
  }

  /**
   * Gives the landing page its trace. The runtime exists now, so the recorded navigation is
   * replayed with the timestamps the router used: an ended one becomes a start-and-close span, an
   * in-flight one becomes the span its `NavigationEnd` will close. `setRoute` runs first, so
   * vitals and errors of this page carry a real route and not `/unknown`.
   */
  private replayPendingNavigation(): void {
    const runtime = this.runtimeValue;
    if (runtime === null || this.router === null) {
      return;
    }
    const pending = this.pendingNavigation;
    this.pendingNavigation = null;
    const route = pending?.route ?? routeTemplate(this.routerStateRoot());
    this.currentRoute = route;
    runtime.setRoute(route);
    if (pending === null) {
      return;
    }
    if (pending.endedAt === null) {
      this.routeChange = runtime.beginRouteChange(pending.startedAt);
      return;
    }
    runtime.beginRouteChange(pending.startedAt).end(pending.outcome, route, pending.endedAt);
  }

  /** The live route tree root, or an empty chain when the router has none yet. */
  private routerStateRoot(): RouteTreeNode {
    return this.router?.routerState.snapshot.root ?? EMPTY_ROUTE_ROOT;
  }

  private endRouteChange(outcome: TelemetryOutcome, route: string = this.currentRoute): void {
    const change = this.routeChange;
    this.routeChange = null;
    change?.end(outcome, route);
  }
}
