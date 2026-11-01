// Fixtures shared by `scrubbing-exporter.spec.ts` and `scrubbing-exporter.transport.spec.ts`:
// one hand-built hostile span, the recorder the wrapper is driven through, and the resource/scope
// builders the validation cases need. Every hostile field is deliberate, so a scrubbing rule that
// stops applying shows up as a value on the wire.
import { SpanKind, SpanStatusCode } from '@opentelemetry/api';
import type { SpanContext } from '@opentelemetry/api';
import { resourceFromAttributes } from '@opentelemetry/resources';
import type { ReadableSpan, SpanExporter } from '@opentelemetry/sdk-trace';

import { ScrubbingSpanExporter } from './scrubbing-exporter';
import { INSTRUMENTATION_NAME } from './telemetry-names';

export const EMAIL = ['julia.moreau', 'example.test'].join('@');
export const RUN_ID = ['3f2b9d54', '0d7e', '4c1a', '9a2e', '6b0f5c7d81c4'].join('-');
export const CREDENTIAL = 'abcdef1234567890';

export const SPAN_CONTEXT: SpanContext = {
  traceId: '0123456789abcdef0123456789abcdef',
  spanId: '0123456789abcdef',
  traceFlags: 1,
};
export const PARENT_CONTEXT: SpanContext = {
  traceId: 'fedcba9876543210fedcba9876543210',
  spanId: 'fedcba9876543210',
  traceFlags: 0,
};

export type ExportCallback = Parameters<SpanExporter['export']>[1];

/** The exporter a batch processor would hand spans to; records every call it receives. */
export class RecordingExporter implements SpanExporter {
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
    name: 'api.request',
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
      // No attribute carries message text any more (D-S69-Q2): a span that still presents one
      // must leave without it.
      'error.message': `boom "${EMAIL}" for ${CREDENTIAL}`,
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
    // A scope this app never creates: the name is a URL that also carries an e-mail address, and
    // `schemaUrl` is a URL, so both are content the receiver must not see.
    instrumentationScope: {
      name: `https://collector.example.test/v1/traces?user=${EMAIL}`,
      version: '1.0.0',
      schemaUrl: 'https://opentelemetry.io/schemas/1.25.0',
    },
    droppedAttributesCount: 1,
    droppedEventsCount: 2,
    droppedLinksCount: 3,
  };
}

/** A span with one hostile value in every field the exporter has to clean. */
export function hostileSpan(overrides: Partial<ReadableSpan> = {}): ReadableSpan {
  return { ...spanFields(), parentSpanContext: PARENT_CONTEXT, ...overrides };
}

/** The same span as a root span: the parent context is absent, not merely `undefined`. */
export function hostileRootSpan(): ReadableSpan {
  return { ...spanFields() };
}

export interface Recorded {
  readonly exporter: RecordingExporter;
  readonly input: ReadableSpan;
  readonly output: ReadableSpan;
}

/** Exports one span through the wrapper and returns the input and the emitted copy. */
export function exportOne(input: ReadableSpan): Recorded {
  const exporter = new RecordingExporter();
  new ScrubbingSpanExporter(exporter).export([input], () => undefined);
  const [batch] = exporter.batches;
  const [output] = batch ?? [];
  if (output === undefined) {
    throw new Error('the inner exporter received no span');
  }
  return { exporter, input, output };
}

/** Exports a batch through the wrapper and returns every emitted copy. */
export function exportBatch(inputs: readonly ReadableSpan[]): readonly ReadableSpan[] {
  const exporter = new RecordingExporter();
  new ScrubbingSpanExporter(exporter).export([...inputs], () => undefined);
  return exporter.batches[0] ?? [];
}

/** A resource whose schema URL is exactly the recorded value under test. */
export function schemaUrlResource(schemaUrl: string) {
  return resourceFromAttributes({ 'service.name': 'evidgate-web' }, { schemaUrl });
}

/**
 * A scope whose recorded version is not text: the SDK types `version` as `string`, but a stale or
 * hostile instrumentation can record anything there, so the spec sets the field past that type.
 */
export function scopeWithVersion(recorded: unknown): ReadableSpan['instrumentationScope'] {
  const scope: ReadableSpan['instrumentationScope'] = { name: INSTRUMENTATION_NAME };
  Object.defineProperty(scope, 'version', { value: recorded, enumerable: true });
  return scope;
}
