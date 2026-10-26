// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: PII/secret scrubbing, telemetry
// loss must never break the app): the closed set of names a span or an event may carry, in one
// module with no imports of its own.
//
// Two files need the same list and neither may own it: `telemetry-sdk.ts` names the spans it
// starts, and `scrubbing-exporter.ts` is the gate that admits a span only when its name is one of
// these constants. Writing the list here is what makes "the exporter allows exactly what the
// facade emits" a property of the code rather than a coincidence of two string literals that can
// drift apart. The module imports nothing, so both sides can use it without a cycle.
//
// A span name is a constant, never content: the facade never puts a route, a URL or a message in
// a name (`route.change`, `api.request`, `client.error`, `web_vital`, `client.event`). Anything
// else reaching the exporter is instrumentation this app did not write, and the exporter does not
// export it. Event names are constants by the same rule, and the list holds exactly the events the
// facade records itself, so a name that carries content is dropped rather than rewritten.

/** The five fixed span names. */
export const ROUTE_SPAN_NAME = 'route.change';
export const API_SPAN_NAME = 'api.request';
export const ERROR_SPAN_NAME = 'client.error';
export const VITAL_SPAN_NAME = 'web_vital';
/** The zero-duration span one named client event arrives on (see {@link EVENT_NAMES}). */
export const CLIENT_EVENT_SPAN_NAME = 'client.event';

/** The span name the exporter substitutes for a span it does not recognise. */
export const UNKNOWN_SPAN_NAME = 'unknown';

/** Every span name the exporter admits. A span with another name is exported as `unknown`. */
export const SPAN_NAMES: ReadonlySet<string> = new Set([
  ROUTE_SPAN_NAME,
  API_SPAN_NAME,
  ERROR_SPAN_NAME,
  VITAL_SPAN_NAME,
  CLIENT_EVENT_SPAN_NAME,
]);

/**
 * The audit export cap hit (09.1.1-T9.2; plan 09.7.1 §8 error funnel): the one event the facade
 * records, on a `client.event` span, with ids only — the event itself carries no attribute.
 */
export const AUDIT_EXPORT_CAPPED_EVENT = 'audit.export_capped';

/**
 * Event names the exporter admits. One name, because the facade records exactly one event:
 * `audit.export_capped` on a `client.event` span, attribute-free. Anything else — an exception
 * event, a message, a URL — came from instrumentation this app did not write, and an event name
 * is content, not a constant.
 */
export const EVENT_NAMES: ReadonlySet<string> = new Set<string>([AUDIT_EXPORT_CAPPED_EVENT]);

/**
 * A named client event. The name is the whole payload: `reportEvent` gives it a `client.event`
 * span whose attributes are the route and the locale, and no attribute of its own.
 */
export type TelemetryEventName = typeof AUDIT_EXPORT_CAPPED_EVENT;

/** Instrumentation scope name: one scope for the whole facade, and the only one exported. */
export const INSTRUMENTATION_NAME = 'evidgate-web-telemetry';
