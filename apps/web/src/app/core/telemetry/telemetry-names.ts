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
// a name (`route.change`, `api.request`, `client.error`, `web_vital`). Anything else reaching the
// exporter is instrumentation this app did not write, and the exporter does not export it.

/** The four fixed span names. */
export const ROUTE_SPAN_NAME = 'route.change';
export const API_SPAN_NAME = 'api.request';
export const ERROR_SPAN_NAME = 'client.error';
export const VITAL_SPAN_NAME = 'web_vital';

/** The span name the exporter substitutes for a span it does not recognise. */
export const UNKNOWN_SPAN_NAME = 'unknown';

/** Every span name the exporter admits. A span with another name is exported as `unknown`. */
export const SPAN_NAMES: ReadonlySet<string> = new Set([
  ROUTE_SPAN_NAME,
  API_SPAN_NAME,
  ERROR_SPAN_NAME,
  VITAL_SPAN_NAME,
]);

/**
 * Event names the exporter admits. Empty today: the facade records no span events (a client
 * error is its own span, and no exception event is added), so every event on an exported span
 * came from somewhere else and an event name is content, not a constant.
 */
export const EVENT_NAMES: ReadonlySet<string> = new Set<string>();

/** Instrumentation scope name: one scope for the whole facade, and the only one exported. */
export const INSTRUMENTATION_NAME = 'evidgate-web-telemetry';
