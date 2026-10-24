// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; 00 §9.1 ids only, never content or PII):
// the single source of truth for what a web span may carry.
//
// Two gates, both closed by default. `ALLOWED_ATTRIBUTE_KEYS` is what `scrub.ts` keeps from a
// span's attributes and events; `ALLOWED_RESOURCE_KEYS` is what `scrubbing-exporter.ts` keeps
// from the span's resource. A key that is not listed here can never reach an OTLP endpoint,
// however it got onto the span, so a future instrumentation cannot widen the surface by
// accident — it has to add the key here, in front of the reviewer, and give it a rule in
// `scrub.ts`.
//
// What is deliberately absent: message text of any kind — not even a scrubbed one (D-S69-Q2,
// 00 §9.1 "ids only") — no stack trace, no URL, no host, no org/project name, and no user or
// session identifier other than the page's own `X-Request-Id`, which the server already
// correlates on.

/**
 * Attribute and span-event keys the web exporter may forward, with exactly the values
 * `scrub.ts` accepts for each. `http.route` and `evidgate.route` hold path *templates*
 * (`/v1/projects/:id/runs`, `/projects/:projectId/runs`), never a concrete URL.
 */
export const ALLOWED_ATTRIBUTE_KEYS: ReadonlySet<string> = new Set<string>([
  // HTTP (CLIENT spans; semantic-convention names where one exists).
  'http.request.method',
  'http.response.status_code',
  'http.route',
  // Route changes.
  'evidgate.route',
  'evidgate.outcome',
  // Request correlation: the same UUID the `X-Request-Id` header carries.
  'evidgate.request_id',
  'evidgate.locale',
  // API failures, ids only (00 §9.2 envelope fields).
  'evidgate.api.error_code',
  'evidgate.api.error_kind',
  'evidgate.api.retryable',
  // Client errors: where it came from and the error's constructor name; no message text.
  'evidgate.error.source',
  'error.type',
  // Web vitals (core web vitals naming; `web_vital.*` is this plan's own namespace).
  'web_vital.name',
  'web_vital.value',
  'web_vital.rating',
  'web_vital.navigation_type',
]);

/**
 * Resource attributes kept from the resource the SDK built: the three OTel SDK identity keys
 * OTLP receivers expect, plus this service's own name and version. Anything else on the
 * resource — host, process, a deployment environment naming the customer — is dropped.
 */
export const ALLOWED_RESOURCE_KEYS: ReadonlySet<string> = new Set<string>([
  'service.name',
  'service.version',
  'telemetry.sdk.language',
  'telemetry.sdk.name',
  'telemetry.sdk.version',
]);
