// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: PII/secret scrubbing, telemetry
// loss must never break the app): the last gate before a span leaves the app.
//
// Whatever instrumentation recorded, this exporter hands the inner exporter a NEW plain
// `ReadableSpan`-shaped object: the span name is narrowed to `SPAN_NAMES` (anything else is
// exported under the fixed name `unknown`, because a name is where a hostile instrumentation
// would put a URL or a message), attributes and event attributes go through `scrubAttributes`
// (allowlist + per-key rules), links are dropped, the status message is dropped (code kept), the
// event list keeps only the names in `EVENT_NAMES`, the instrumentation scope is reduced to
// `name` + `version` with the name pinned to `INSTRUMENTATION_NAME`, and the resource keeps only
// `ALLOWED_RESOURCE_KEYS`. The input span is never mutated, so a test exporter or the SDK's own
// bookkeeping still sees what was recorded; only the copy that crosses the network is redacted.
//
// Why this file imports `@opentelemetry/resources` at runtime (the only runtime OTel import
// outside `telemetry-sdk.ts`): a filtered resource has to be a real `Resource` (the OTLP
// transformer reads `getRawAttributes()` as well as `attributes`), and `resourceFromAttributes`
// is the only supported way to build one. This file is reachable only from the lazy
// `telemetry-sdk` chunk, so nothing here enlarges the main bundle.
import { resourceFromAttributes } from '@opentelemetry/resources';
import type { Resource } from '@opentelemetry/resources';
import type { Attributes } from '@opentelemetry/api';
import type { ReadableSpan, SpanExporter, TimedEvent } from '@opentelemetry/sdk-trace';

import { ALLOWED_RESOURCE_KEYS } from './attribute-allowlist';
import { scrubAttributes } from './scrub';
import {
  EVENT_NAMES,
  INSTRUMENTATION_NAME,
  SPAN_NAMES,
  UNKNOWN_SPAN_NAME,
} from './telemetry-names';

/** The SDK's export callback, taken from the interface so `@opentelemetry/core` stays unimported. */
type ExportResultCallback = Parameters<SpanExporter['export']>[1];

/**
 * Wraps a real exporter with the scrubbing gate. `shutdown`/`forceFlush` go straight through:
 * the wrapper holds no buffers of its own, and the batch processor above it owns the queue.
 */
export class ScrubbingSpanExporter implements SpanExporter {
  private readonly inner: SpanExporter;

  constructor(inner: SpanExporter) {
    this.inner = inner;
  }

  export(spans: ReadableSpan[], resultCallback: ExportResultCallback): void {
    this.inner.export(spans.map(toScrubbedSpan), resultCallback);
  }

  shutdown(): Promise<void> {
    return this.inner.shutdown();
  }

  forceFlush(): Promise<void> {
    return this.inner.forceFlush === undefined ? Promise.resolve() : this.inner.forceFlush();
  }
}

/** A value the SDK can serialize: string, boolean, or a finite number. */
function isSimpleValue(value: unknown): value is string | number | boolean {
  if (typeof value === 'string' || typeof value === 'boolean') {
    return true;
  }
  return typeof value === 'number' && Number.isFinite(value);
}

/** The resource with only the allowlisted keys kept; the input resource is left alone. */
function scrubbedResource(resource: Resource): Resource {
  const attributes: Attributes = {};
  for (const [key, value] of Object.entries(resource.attributes)) {
    if (ALLOWED_RESOURCE_KEYS.has(key) && isSimpleValue(value)) {
      attributes[key] = value;
    }
  }
  const { schemaUrl } = resource;
  return resourceFromAttributes(attributes, schemaUrl === undefined ? undefined : { schemaUrl });
}

/**
 * The event with scrubbed attributes; timing is copied as it is. Its name is already known to be
 * in `EVENT_NAMES`, so it is one of this app's constants and needs no scrubbing of its own.
 */
function scrubbedEvent(event: TimedEvent): TimedEvent {
  const { droppedAttributesCount } = event;
  return {
    time: event.time,
    name: event.name,
    attributes: scrubAttributes(event.attributes ?? {}),
    ...(droppedAttributesCount === undefined ? {} : { droppedAttributesCount }),
  };
}

/**
 * The events that cross the network: only the names in `EVENT_NAMES` (none today). An event name
 * this app did not write is content — it is where a message or a URL would sit — so it is
 * dropped rather than renamed: a scrubbed name would still be a name the receiver cannot trust.
 */
function scrubbedEvents(events: readonly TimedEvent[]): TimedEvent[] {
  return events.filter((event) => EVENT_NAMES.has(event.name)).map(scrubbedEvent);
}

/** A build version (`1.2.3`, `2.0.0-rc.1+abc`); anything else on a scope is content, not a version. */
const SCOPE_VERSION = /^[0-9A-Za-z.+-]{1,32}$/;

/**
 * The export copy of one span. Contexts are captured as values (`spanContext` is a function
 * on the SDK's span, so the copy closes over the result rather than re-entering the original
 * with a foreign `this`), times and counters are copied, `status.message` and links are gone.
 *
 * The name is a constant: an allowlisted one is kept and anything else is exported as `unknown`,
 * because a name is where a hostile instrumentation puts a URL or a message. The instrumentation
 * scope is reduced to the one scope this app uses plus its own build version — a foreign scope's
 * name is content, and `schemaUrl` is a URL, so neither is exported.
 */
function toScrubbedSpan(span: ReadableSpan): ReadableSpan {
  const spanContext = span.spanContext();
  const { parentSpanContext, instrumentationScope } = span;
  const { version: recordedVersion } = instrumentationScope;
  const scopeVersion = SCOPE_VERSION.test(recordedVersion ?? '') ? recordedVersion : undefined;
  return {
    name: SPAN_NAMES.has(span.name) ? span.name : UNKNOWN_SPAN_NAME,
    kind: span.kind,
    spanContext: () => spanContext,
    startTime: span.startTime,
    endTime: span.endTime,
    status: { code: span.status.code },
    attributes: scrubAttributes(span.attributes),
    links: [],
    events: scrubbedEvents(span.events),
    duration: span.duration,
    ended: span.ended,
    resource: scrubbedResource(span.resource),
    instrumentationScope: {
      name: INSTRUMENTATION_NAME,
      ...(scopeVersion === undefined ? {} : { version: scopeVersion }),
    },
    droppedAttributesCount: span.droppedAttributesCount,
    droppedEventsCount: span.droppedEventsCount,
    droppedLinksCount: span.droppedLinksCount,
    ...(parentSpanContext === undefined ? {} : { parentSpanContext }),
  };
}
