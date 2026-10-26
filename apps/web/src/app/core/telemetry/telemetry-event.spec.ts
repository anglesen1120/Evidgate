// 09.1.1-T9.2 (plan 09.7.1 §8 "audit export cap" error-funnel row; plans/features/09-web-ui §10
// T9.2): the one named client event this app records. Every assertion below reads the exported
// copy — the real `startTelemetrySdk`, the real `ScrubbingSpanExporter`, an `InMemorySpanExporter`
// at the end of the chain — so what is pinned is what a receiver gets, not what the facade
// intended: the event survives by name, it carries no attribute at all, the span around it holds
// only allowlisted keys (route and locale), and a name or an event this app did not write is still
// dropped.
import { SpanKind, SpanStatusCode } from '@opentelemetry/api';
import type { SpanContext } from '@opentelemetry/api';
import { resourceFromAttributes } from '@opentelemetry/resources';
import { InMemorySpanExporter, type ReadableSpan } from '@opentelemetry/sdk-trace';

import { ALLOWED_ATTRIBUTE_KEYS } from './attribute-allowlist';
import { ScrubbingSpanExporter } from './scrubbing-exporter';
import {
  AUDIT_EXPORT_CAPPED_EVENT,
  CLIENT_EVENT_SPAN_NAME,
  EVENT_NAMES,
  INSTRUMENTATION_NAME,
  SPAN_NAMES,
  UNKNOWN_SPAN_NAME,
} from './telemetry-names';
import { startTelemetrySdk } from './telemetry-sdk';
import type { TelemetryRuntime } from './telemetry-sdk';

/** The route the audit tab matches: a template, which is what the span is allowed to carry. */
const ROUTE = '/projects/:projectId/audit';

const SPAN_CONTEXT: SpanContext = {
  traceId: '0123456789abcdef0123456789abcdef',
  spanId: '0123456789abcdef',
  traceFlags: 1,
};

/** A runtime whose exporter the spec reads; the scrubbing gate is the production one. */
function startEventSdk(locale?: string): {
  readonly runtime: TelemetryRuntime;
  readonly exporter: InMemorySpanExporter;
} {
  const exporter = new InMemorySpanExporter();
  const runtime = startTelemetrySdk(
    { endpoint: null, sampleRatio: 1 },
    { exporter, ...(locale === undefined ? {} : { locale }) },
  );
  return { runtime, exporter };
}

function onlySpanNamed(exporter: InMemorySpanExporter, name: string): ReadableSpan {
  const [span] = exporter.getFinishedSpans().filter((finished) => finished.name === name);
  if (span === undefined) {
    const exported = exporter
      .getFinishedSpans()
      .map((finished) => finished.name)
      .join(', ');
    throw new Error(`expected an exported "${name}" span; exported: ${exported}`);
  }
  return span;
}

/** A hand-built span, for the cases the facade itself refuses to produce. */
function readableSpan(overrides: Partial<ReadableSpan> = {}): ReadableSpan {
  return {
    name: CLIENT_EVENT_SPAN_NAME,
    kind: SpanKind.INTERNAL,
    spanContext: () => SPAN_CONTEXT,
    startTime: [1_700_000_000, 0],
    endTime: [1_700_000_000, 0],
    status: { code: SpanStatusCode.UNSET },
    attributes: {},
    links: [],
    events: [],
    duration: [0, 0],
    ended: true,
    resource: resourceFromAttributes({ 'service.name': 'evidgate-web' }),
    instrumentationScope: { name: INSTRUMENTATION_NAME, version: '1.2.3' },
    droppedAttributesCount: 0,
    droppedEventsCount: 0,
    droppedLinksCount: 0,
    ...overrides,
  };
}

/** One span through the wrapper, as the processor would hand it over. */
function scrubOne(input: ReadableSpan): ReadableSpan {
  const inner = new InMemorySpanExporter();
  new ScrubbingSpanExporter(inner).export([input], () => undefined);
  const [output] = inner.getFinishedSpans();
  if (output === undefined) {
    throw new Error('the inner exporter received no span');
  }
  return output;
}

describe('client.event (09.1.1-T9.2)', () => {
  it('exports audit.export_capped with the route and the locale, and no other attribute', () => {
    const { runtime, exporter } = startEventSdk('vi');
    const change = runtime.beginRouteChange();
    runtime.setRoute(ROUTE);
    change.end('ok', ROUTE);
    runtime.reportEvent(AUDIT_EXPORT_CAPPED_EVENT);

    const span = onlySpanNamed(exporter, CLIENT_EVENT_SPAN_NAME);

    expect(span.name).toBe('client.event');
    expect(span.attributes).toEqual({ 'evidgate.route': ROUTE, 'evidgate.locale': 'vi' });
    // Every surviving key is on the allowlist: the event did not widen it.
    for (const key of Object.keys(span.attributes)) {
      expect(ALLOWED_ATTRIBUTE_KEYS.has(key)).toBe(true);
    }
    // One event, by name, with no attribute of its own: the name is the whole payload.
    expect(span.events).toHaveLength(1);
    expect(span.events[0]?.name).toBe(AUDIT_EXPORT_CAPPED_EVENT);
    expect(span.events[0]?.attributes).toEqual({});
  });

  it('is a zero-duration span inside the route trace, carrying no count and no filter', () => {
    const { runtime, exporter } = startEventSdk();
    const change = runtime.beginRouteChange();
    runtime.setRoute(ROUTE);
    change.end('ok', ROUTE);
    runtime.reportEvent(AUDIT_EXPORT_CAPPED_EVENT);

    const route = onlySpanNamed(exporter, 'route.change');
    const span = onlySpanNamed(exporter, CLIENT_EVENT_SPAN_NAME);

    // A point-in-time event: start and end are the same instant, so the span has no duration.
    expect(span.startTime).toEqual(span.endTime);
    expect(span.duration).toEqual([0, 0]);
    // It continues the route trace, like every other span of the same navigation.
    expect(span.spanContext().traceId).toBe(route.spanContext().traceId);
    expect(span.parentSpanContext?.spanId).toBe(route.spanContext().spanId);
    // "ids only" (00 §9.1): the cap is a number nobody exports, and the event has no attributes
    // where a row count, an org id or a filter value could hide.
    expect(Object.values(span.attributes).filter((value) => typeof value === 'number')).toEqual([]);
    expect(span.events.map((event) => Object.keys(event.attributes ?? {}))).toEqual([[]]);
  });

  it('keeps the one allowlisted event name and drops an event this app did not write', () => {
    const output = scrubOne(
      readableSpan({
        events: [
          { time: [1_700_000_000, 0], name: AUDIT_EXPORT_CAPPED_EVENT, attributes: {} },
          {
            time: [1_700_000_000, 0],
            name: 'failed for ops@example.test',
            attributes: { 'error.message': 'boom' },
          },
        ],
      }),
    );

    expect(output.events).toHaveLength(1);
    expect(output.events[0]?.name).toBe(AUDIT_EXPORT_CAPPED_EVENT);
    // The event's own attributes go through the same allowlist: an unlisted key never survives.
    expect(output.events[0]?.attributes).toEqual({});
    expect(JSON.stringify(output.events)).not.toContain('boom');
    expect(JSON.stringify(output.events)).not.toContain('example.test');
  });

  it('still exports a span name outside the closed set as unknown', () => {
    const output = scrubOne(readableSpan({ name: 'GET /v1/audit?email=ops@example.test' }));

    // The two new names are in the closed sets the exporter admits.
    expect(SPAN_NAMES.has(CLIENT_EVENT_SPAN_NAME)).toBe(true);
    expect(EVENT_NAMES.has(AUDIT_EXPORT_CAPPED_EVENT)).toBe(true);
    expect(output.name).toBe(UNKNOWN_SPAN_NAME);
    expect(output.name).toBe('unknown');
  });
});
