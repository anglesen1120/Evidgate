// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: PII/secret scrubbing, telemetry
// loss must never break the app): the exporter is the boundary a receiver sees, so this spec
// pins the properties the plan asks of it — the recorded span is never mutated, the inner
// exporter gets scrubbed copies, the name/event/scope sets are closed (an unlisted span name is
// `unknown`, an unlisted event is dropped, a foreign scope is replaced), and
// `shutdown`/`forceFlush` are the inner exporter's own — with a plain hand-built `ReadableSpan`
// so every hostile field is deliberate. What the wire carries is asserted in
// `scrubbing-exporter.transport.spec.ts`.
import { SpanStatusCode } from '@opentelemetry/api';
import { resourceFromAttributes } from '@opentelemetry/resources';
import type { ReadableSpan, SpanExporter } from '@opentelemetry/sdk-trace';

import { ALLOWED_ATTRIBUTE_KEYS, ALLOWED_RESOURCE_KEYS } from './attribute-allowlist';
import {
  CREDENTIAL,
  EMAIL,
  PARENT_CONTEXT,
  RUN_ID,
  RecordingExporter,
  SPAN_CONTEXT,
  type ExportCallback,
  exportBatch,
  exportOne,
  hostileRootSpan,
  hostileSpan,
  schemaUrlResource,
  scopeWithVersion,
} from './scrubbing-exporter.testing';
import { ScrubbingSpanExporter } from './scrubbing-exporter';
import {
  EVENT_NAMES,
  INSTRUMENTATION_NAME,
  SPAN_NAMES,
  UNKNOWN_SPAN_NAME,
} from './telemetry-names';

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

  it('keeps an allowlisted span name, scrubs the attributes and drops an error message', () => {
    const { output } = exportOne(hostileSpan());

    expect(output.name).toBe('api.request');
    expect(output.attributes).toEqual({
      'http.request.method': 'POST',
      'http.route': '/v1/projects/:id/runs',
    });
    // The key is not allowlisted at all, so this is a drop on the key, not a scrub of its value.
    expect(output.attributes['error.message']).toBeUndefined();
  });

  it('exports a span name the fixed constants do not cover as unknown', () => {
    const { output } = exportOne(hostileSpan({ name: `GET /v1/runs?email=${EMAIL}` }));

    expect(output.name).toBe(UNKNOWN_SPAN_NAME);
    expect(output.name).toBe('unknown');
    expect(JSON.stringify(output)).not.toContain(EMAIL);
  });

  it('admits exactly the five span names and the one event name', () => {
    expect([...SPAN_NAMES].sort()).toEqual(
      ['api.request', 'client.error', 'client.event', 'route.change', 'web_vital'].sort(),
    );
    expect([...EVENT_NAMES]).toEqual(['audit.export_capped']);
  });

  it('drops an event whose name this app did not write, and the span links', () => {
    const { input, output } = exportOne(hostileSpan());

    // The hostile event name carries an e-mail address and is not a constant: it is dropped, not
    // scrubbed, so the receiver never sees a name it cannot trust.
    expect(input.events).toHaveLength(1);
    expect(output.events).toEqual([]);
    expect(output.links).toEqual([]);
    // The event also carried an `error.message` attribute: nowhere in the emitted span does its
    // text survive, because the event that held it was dropped whole.
    expect(JSON.stringify(output)).not.toContain('boom');
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

  it('copies the context, the timings and the dropped counters', () => {
    const { input, output } = exportOne(hostileSpan());

    expect(output.kind).toBe(input.kind);
    expect(output.parentSpanContext).toEqual(PARENT_CONTEXT);
    expect(output.startTime).toEqual(input.startTime);
    expect(output.endTime).toEqual(input.endTime);
    expect(output.duration).toEqual(input.duration);
    expect(output.ended).toBe(true);
    expect(output.droppedAttributesCount).toBe(1);
    expect(output.droppedEventsCount).toBe(2);
    expect(output.droppedLinksCount).toBe(3);
  });

  it('pins a foreign instrumentation scope to the one app scope and drops schemaUrl', () => {
    const { output } = exportOne(hostileSpan());

    expect(output.instrumentationScope).toEqual({
      name: INSTRUMENTATION_NAME,
      version: '1.0.0',
    });
    expect('schemaUrl' in output.instrumentationScope).toBe(false);
    const serialized = JSON.stringify(output.instrumentationScope);
    expect(serialized).not.toContain(EMAIL);
    expect(serialized).not.toContain('collector.example.test');
    expect(serialized).not.toContain('opentelemetry.io');
  });

  it('keeps the app’s own scope name and version as they are', () => {
    const { output } = exportOne(
      hostileSpan({ instrumentationScope: { name: INSTRUMENTATION_NAME, version: '2.4.1' } }),
    );

    expect(output.instrumentationScope).toEqual({
      name: INSTRUMENTATION_NAME,
      version: '2.4.1',
    });
  });

  it('omits the scope version when the recorded scope has none', () => {
    const { output } = exportOne(
      hostileSpan({ instrumentationScope: { name: `foreign-${EMAIL}` } }),
    );

    expect(output.instrumentationScope).toEqual({ name: INSTRUMENTATION_NAME });
    expect('version' in output.instrumentationScope).toBe(false);
  });

  it('omits a scope version that is free text instead of a version', () => {
    const { output } = exportOne(
      hostileSpan({ instrumentationScope: { name: INSTRUMENTATION_NAME, version: EMAIL } }),
    );

    expect(output.instrumentationScope).toEqual({ name: INSTRUMENTATION_NAME });
    expect(JSON.stringify(output)).not.toContain(EMAIL);
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
    expect(serialized).not.toContain('collector.example.test');
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

describe('ScrubbingSpanExporter, one scrubbed resource per recorded resource', () => {
  it('shares one scrubbed resource between the spans of a batch', () => {
    const resource = resourceFromAttributes({
      'service.name': 'evidgate-web',
      'user.email': EMAIL,
    });

    const outputs = exportBatch([
      hostileSpan({ resource }),
      hostileSpan({ resource, name: 'client.error' }),
    ]);

    expect(outputs).toHaveLength(2);
    expect(outputs[0]?.resource).toBe(outputs[1]?.resource);
    expect(outputs[0]?.resource.attributes).toEqual({ 'service.name': 'evidgate-web' });
  });

  it('keeps a scrubbed copy per input resource instead of one for every span', () => {
    const outputs = exportBatch([
      hostileSpan({ resource: resourceFromAttributes({ 'service.name': 'evidgate-web' }) }),
      hostileSpan({ resource: resourceFromAttributes({ 'service.name': 'evidgate-web' }) }),
    ]);

    expect(outputs[0]?.resource).not.toBe(outputs[1]?.resource);
    expect(outputs[0]?.resource.attributes).toEqual(outputs[1]?.resource.attributes);
  });

  // Built through URL so the spec carries no credential-shaped literal.
  const withUserinfo = (base: string, password: string): string => {
    const url = new URL(base);
    url.username = 'user';
    url.password = password;
    return url.href;
  };

  it('keeps a plain https schemaUrl and drops one that is anything else', () => {
    const kept = 'https://opentelemetry.io/schemas/1.25.0';
    expect(
      exportOne(hostileSpan({ resource: schemaUrlResource(kept) })).output.resource,
    ).toHaveProperty('schemaUrl', kept);

    for (const recorded of [
      'https://10.0.0.1/schemas/1.25.0',
      `https://collector.example.test/v1/traces?user=${EMAIL}`,
      `https://collector.example.test/v1/traces#${CREDENTIAL}`,
      withUserinfo('https://collector.example.test/schemas/1.25.0', CREDENTIAL),
      'http://collector.example.test/schemas/1.25.0',
      'javascript:alert(1)',
    ]) {
      const { output } = exportOne(hostileSpan({ resource: schemaUrlResource(recorded) }));
      expect(output.resource.schemaUrl).toBeUndefined();
    }
  });

  it('drops a resource schemaUrl that is not a string and still exports the batch', () => {
    const notText = resourceFromAttributes({ 'service.name': 'evidgate-web' });
    Object.defineProperty(notText, 'schemaUrl', { value: null, enumerable: true });
    const kept = 'https://opentelemetry.io/schemas/1.25.0';

    const outputs = exportBatch([
      hostileSpan({ resource: notText }),
      hostileSpan({ resource: schemaUrlResource(kept) }),
    ]);

    // The batch survives: a schema URL the exporter cannot read costs that one field, not the spans.
    expect(outputs).toHaveLength(2);
    expect(outputs[0]?.resource.schemaUrl).toBeUndefined();
    expect(outputs[0]?.resource.attributes).toEqual({ 'service.name': 'evidgate-web' });
    expect(outputs[1]?.resource.schemaUrl).toBe(kept);
  });

  it('drops a scope version that is not a string instead of exporting it', () => {
    const { output } = exportOne(hostileSpan({ instrumentationScope: scopeWithVersion(2) }));

    expect('version' in output.instrumentationScope).toBe(false);
  });
});
