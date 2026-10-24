// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: PII/secret scrubbing, telemetry
// loss must never break the app): the exporter is the boundary a receiver sees, so this spec
// pins the three properties the plan asks of it — the recorded span is never mutated, the inner
// exporter gets scrubbed copies, and `shutdown`/`forceFlush` are the inner exporter's own —
// with a plain hand-built `ReadableSpan` so every hostile field is deliberate.
import { SpanKind, SpanStatusCode } from '@opentelemetry/api';
import type { SpanContext } from '@opentelemetry/api';
import { resourceFromAttributes } from '@opentelemetry/resources';
import type { ReadableSpan, SpanExporter } from '@opentelemetry/sdk-trace';

import { ALLOWED_ATTRIBUTE_KEYS, ALLOWED_RESOURCE_KEYS } from './attribute-allowlist';
import { ScrubbingSpanExporter } from './scrubbing-exporter';

const EMAIL = ['julia.moreau', 'example.test'].join('@');
const RUN_ID = ['3f2b9d54', '0d7e', '4c1a', '9a2e', '6b0f5c7d81c4'].join('-');
const CREDENTIAL = 'abcdef1234567890';

const SPAN_CONTEXT: SpanContext = {
  traceId: '0123456789abcdef0123456789abcdef',
  spanId: '0123456789abcdef',
  traceFlags: 1,
};
const PARENT_CONTEXT: SpanContext = {
  traceId: 'fedcba9876543210fedcba9876543210',
  spanId: 'fedcba9876543210',
  traceFlags: 0,
};

type ExportCallback = Parameters<SpanExporter['export']>[1];

/** The exporter a batch processor would hand spans to; records every call it receives. */
class RecordingExporter implements SpanExporter {
  readonly batches: ReadableSpan[][] = [];
  readonly callbacks: ExportCallback[] = [];
  shutdownCalls = 0;
  forceFlushCalls = 0;

  export(spans: ReadableSpan[], resultCallback: ExportCallback): void {
    this.batches.push(spans);
    this.callbacks.push(resultCallback);
  }

  shutdown(): Promise<void> {
    this.shutdownCalls += 1;
    return Promise.resolve();
  }

  forceFlush(): Promise<void> {
    this.forceFlushCalls += 1;
    return Promise.resolve();
  }
}

/** Every field of the hostile span except the optional parent context. */
function spanFields(): Omit<ReadableSpan, 'parentSpanContext'> {
  return {
    name: `api.request for ${EMAIL}`,
    kind: SpanKind.CLIENT,
    spanContext: () => SPAN_CONTEXT,
    startTime: [1_700_000_000, 0],
    endTime: [1_700_000_001, 500],
    status: { code: SpanStatusCode.ERROR, message: `Bearer ${CREDENTIAL} rejected` },
    attributes: {
      'http.request.method': 'POST',
      'http.route': `/v1/projects/${RUN_ID}/runs`,
      'http.url': `https://acme.example.test/v1/runs?token=${CREDENTIAL}`,
      'user.email': EMAIL,
    },
    links: [{ context: SPAN_CONTEXT, attributes: { 'user.email': EMAIL } }],
    events: [
      {
        time: [1_700_000_000, 100],
        name: `failed for ${EMAIL}`,
        attributes: { 'error.message': `boom "${EMAIL}"`, cookie: `session=${CREDENTIAL}` },
        droppedAttributesCount: 0,
      },
    ],
    duration: [1, 500],
    ended: true,
    resource: resourceFromAttributes({
      'service.name': 'evidgate-web',
      'service.version': '1.2.3',
      'deployment.environment': 'acme-prod',
      'user.email': EMAIL,
    }),
    instrumentationScope: { name: 'evidgate-web-test', version: '1.0.0' },
    droppedAttributesCount: 1,
    droppedEventsCount: 2,
    droppedLinksCount: 3,
  };
}

/** A span with one hostile value in every field the exporter has to clean. */
function hostileSpan(overrides: Partial<ReadableSpan> = {}): ReadableSpan {
  return { ...spanFields(), parentSpanContext: PARENT_CONTEXT, ...overrides };
}

/** The same span as a root span: the parent context is absent, not merely `undefined`. */
function hostileRootSpan(): ReadableSpan {
  return { ...spanFields() };
}

interface Recorded {
  readonly exporter: RecordingExporter;
  readonly input: ReadableSpan;
  readonly output: ReadableSpan;
}

/** Exports one span through the wrapper and returns the input and the emitted copy. */
function exportOne(input: ReadableSpan): Recorded {
  const exporter = new RecordingExporter();
  new ScrubbingSpanExporter(exporter).export([input], () => undefined);
  const [batch] = exporter.batches;
  const [output] = batch ?? [];
  if (output === undefined) {
    throw new Error('the inner exporter received no span');
  }
  return { exporter, input, output };
}

describe('ScrubbingSpanExporter', () => {
  it('hands the inner exporter a new object, never the recorded span', () => {
    const { input, output } = exportOne(hostileSpan());

    expect(output).not.toBe(input);
    expect(output.attributes).not.toBe(input.attributes);
    expect(output.spanContext()).toEqual(SPAN_CONTEXT);
  });

  it('never mutates the span it was given', () => {
    const input = hostileSpan();
    // The whole span, with the resource projected to its attribute values: the SDK's `Resource`
    // memoises its attributes in a private field on first read, which is a cache, not a mutation.
    const snapshot = (span: ReadableSpan): string =>
      JSON.stringify({ ...span, resource: span.resource.attributes });
    const before = snapshot(input);

    const { output } = exportOne(input);

    expect(snapshot(input)).toBe(before);
    // The copy was scrubbed, not the original: every hostile value is still on the recorded span.
    expect(input.attributes['user.email']).toBe(EMAIL);
    expect(input.attributes['http.url']).toBe(
      `https://acme.example.test/v1/runs?token=${CREDENTIAL}`,
    );
    expect(input.status.message).toBe(`Bearer ${CREDENTIAL} rejected`);
    expect(input.events[0]?.name).toBe(`failed for ${EMAIL}`);
    expect(input.links).toHaveLength(1);
    expect(output).not.toBe(input);
  });

  it('passes the export callback through untouched', () => {
    const exporter = new RecordingExporter();
    const callback: ExportCallback = () => undefined;

    new ScrubbingSpanExporter(exporter).export([hostileSpan()], callback);

    expect(exporter.callbacks[0]).toBe(callback);
  });

  it('scrubs the span name and the span attributes', () => {
    const { output } = exportOne(hostileSpan());

    expect(output.name).toBe('api.request for [email]');
    expect(output.attributes).toEqual({
      'http.request.method': 'POST',
      'http.route': '/v1/projects/:id/runs',
    });
  });

  it('scrubs event names and attributes and drops the events’ links', () => {
    const { output } = exportOne(hostileSpan());

    expect(output.links).toEqual([]);
    expect(output.events).toHaveLength(1);
    expect(output.events[0]?.name).toBe('failed for [email]');
    expect(output.events[0]?.attributes).toEqual({ 'error.message': 'boom [quoted]' });
    expect(output.events[0]?.droppedAttributesCount).toBe(0);
  });

  it('keeps the status code and drops the status message', () => {
    const { output } = exportOne(hostileSpan());

    expect(output.status).toEqual({ code: SpanStatusCode.ERROR });
  });

  it('filters the resource attributes to the allowlist', () => {
    const { output } = exportOne(hostileSpan());

    expect(output.resource.attributes).toEqual({
      'service.name': 'evidgate-web',
      'service.version': '1.2.3',
    });
    for (const key of Object.keys(output.resource.attributes)) {
      expect(ALLOWED_RESOURCE_KEYS.has(key)).toBe(true);
    }
  });

  it('copies the context, the timings, the scope and the dropped counters', () => {
    const { input, output } = exportOne(hostileSpan());

    expect(output.kind).toBe(input.kind);
    expect(output.parentSpanContext).toEqual(PARENT_CONTEXT);
    expect(output.startTime).toEqual(input.startTime);
    expect(output.endTime).toEqual(input.endTime);
    expect(output.duration).toEqual(input.duration);
    expect(output.ended).toBe(true);
    expect(output.instrumentationScope).toEqual(input.instrumentationScope);
    expect(output.droppedAttributesCount).toBe(1);
    expect(output.droppedEventsCount).toBe(2);
    expect(output.droppedLinksCount).toBe(3);
  });

  it('keeps only allowlisted attribute keys and no identifier anywhere', () => {
    const { output } = exportOne(hostileSpan());

    for (const key of Object.keys(output.attributes)) {
      expect(ALLOWED_ATTRIBUTE_KEYS.has(key)).toBe(true);
    }
    const serialized = JSON.stringify(output);
    expect(serialized).not.toContain(EMAIL);
    expect(serialized).not.toContain(RUN_ID);
    expect(serialized).not.toContain(CREDENTIAL);
  });

  it('omits the parent span context when the input has none', () => {
    const { output } = exportOne(hostileRootSpan());

    expect('parentSpanContext' in output).toBe(false);
  });

  it('delegates shutdown and forceFlush to the inner exporter', async () => {
    const exporter = new RecordingExporter();
    const wrapper = new ScrubbingSpanExporter(exporter);

    await wrapper.shutdown();
    await wrapper.forceFlush();

    expect(exporter.shutdownCalls).toBe(1);
    expect(exporter.forceFlushCalls).toBe(1);
  });

  it('resolves forceFlush when the inner exporter has none', async () => {
    const minimal: SpanExporter = {
      export: () => undefined,
      shutdown: () => Promise.resolve(),
    };

    await expect(new ScrubbingSpanExporter(minimal).forceFlush()).resolves.toBeUndefined();
  });

  it('exports an empty batch as an empty batch', () => {
    const exporter = new RecordingExporter();

    new ScrubbingSpanExporter(exporter).export([], () => undefined);

    expect(exporter.batches).toEqual([[]]);
  });
});
