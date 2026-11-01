// What actually leaves the page: the real OTLP exporter, the real serializer and the production
// batch processor run unchanged, and the only substitution is the collector — a loopback socket in
// this process — so every assertion reads the serialized request body a receiver would see. The
// second spec proves the flush that empties the queue comes from a real `pagehide` on the
// production facade, not from the batch timer.
import { SpanKind } from '@opentelemetry/api';
import { OTLPTraceExporter } from '@opentelemetry/exporter-trace-otlp-http';
import { resourceFromAttributes } from '@opentelemetry/resources';
import { BatchSpanProcessor, TracerProvider } from '@opentelemetry/sdk-trace';
import { TestBed } from '@angular/core/testing';
import { createServer, type Server } from 'node:http';

import { ALLOWED_ATTRIBUTE_KEYS, ALLOWED_RESOURCE_KEYS } from './attribute-allowlist';
import { ScrubbingSpanExporter } from './scrubbing-exporter';
import { CREDENTIAL, EMAIL, RUN_ID } from './scrubbing-exporter.testing';
import { TELEMETRY_CONFIG, TELEMETRY_OPT_OUT } from './telemetry-config';
import { INSTRUMENTATION_NAME } from './telemetry-names';
import { TelemetryService } from './telemetry.service';

/** The path the OTLP exporter appends for traces; the loopback receiver answers it. */
const ENDPOINT_PATH = '/v1/traces';

/** An hour: far past any test, so the batch timer can never be what exports the spans. */
const ONE_HOUR_MS = 3_600_000;

/** The OTLP/JSON members this spec reads back out of a posted export. */
interface OtlpAttributeBody {
  readonly key: string;
  readonly value: { readonly stringValue?: string };
}

interface OtlpTraceRequestBody {
  readonly resourceSpans: readonly {
    readonly resource: {
      readonly attributes: readonly OtlpAttributeBody[];
      readonly schemaUrl?: string;
    };
    readonly scopeSpans: readonly {
      readonly scope: { readonly name: string; readonly version?: string };
      readonly spans: readonly {
        readonly name: string;
        readonly attributes: readonly OtlpAttributeBody[];
      }[];
    }[];
  }[];
}

/** One request the exporter made: where it went and the body it sent. */
interface RecordedPost {
  readonly url: string;
  readonly text: string;
}

/** The OTLP/JSON this process produced; the cast names the members the specs below read. */
function parseTraces(text: string): OtlpTraceRequestBody {
  return JSON.parse(text) as OtlpTraceRequestBody;
}

/**
 * A loopback receiver for the exporter's POST: the real exporter, serializer and transport run
 * unchanged, and the spec only reads the body that reached the socket.
 */
function listen(posted: RecordedPost[]): Promise<Server> {
  const server = createServer((request, response) => {
    const chunks: Buffer[] = [];
    request.on('data', (chunk: Buffer) => chunks.push(chunk));
    request.on('end', () => {
      posted.push({ url: request.url ?? '', text: Buffer.concat(chunks).toString('utf8') });
      response.writeHead(200, { 'Content-Type': 'application/json' });
      response.end('{}');
    });
  });
  // `Promise.withResolvers` is outside this app's `lib` target, so the executor form stays.
  return new Promise((resolve) => {
    server.listen(0, '127.0.0.1', () => resolve(server));
  });
}

describe('ScrubbingSpanExporter over the real OTLP transport', () => {
  let processor: BatchSpanProcessor;
  let provider: TracerProvider;
  let server: Server;
  let posted: RecordedPost[];

  beforeEach(async () => {
    posted = [];
    server = await listen(posted);
    const address = server.address();
    if (address === null || typeof address === 'string') {
      throw new Error('the loopback receiver has no port');
    }
    // The production wiring of `startTelemetrySdk`: the scrubbing wrapper around the real OTLP
    // exporter, inside the batch processor. Only the delay differs (an hour), so a span leaves
    // the queue exactly once, on the flush a page hide triggers.
    processor = new BatchSpanProcessor({
      exporter: new ScrubbingSpanExporter(
        new OTLPTraceExporter({ url: `http://127.0.0.1:${address.port}${ENDPOINT_PATH}` }),
      ),
      scheduledDelayMillis: ONE_HOUR_MS,
    });
    provider = new TracerProvider({
      resource: resourceFromAttributes({
        'service.name': 'evidgate-web',
        'service.version': '1.2.3',
        'user.email': EMAIL,
      }),
      spanProcessors: [processor],
    });
  });

  afterEach(async () => {
    await processor.shutdown();
    await new Promise((resolve) => {
      server.close(() => resolve(undefined));
    });
  });

  it('sends one resourceSpans entry for a batch whose spans share a resource', async () => {
    const tracer = provider.getTracer(INSTRUMENTATION_NAME, '1.2.3');
    tracer
      .startSpan('api.request', {
        kind: SpanKind.CLIENT,
        attributes: {
          'http.request.method': 'POST',
          'http.route': `/v1/projects/${RUN_ID}/runs`,
          'http.url': `https://acme.example.test/v1/runs?token=${CREDENTIAL}`,
          'user.email': EMAIL,
        },
      })
      .end();
    tracer.startSpan(`GET /v1/runs?email=${EMAIL}`, { kind: SpanKind.CLIENT }).end();

    expect(posted).toEqual([]);

    // The same call `TelemetryService.flushVitalsNow` makes; the lifecycle wiring that reaches it
    // is asserted in the facade describe below.
    await processor.forceFlush();

    expect(posted).toHaveLength(1);
    const [request] = posted;
    if (request === undefined) {
      throw new Error('the exporter posted nothing');
    }
    expect(request.url).toBe(ENDPOINT_PATH);
    const body = parseTraces(request.text);
    // Both spans carry the provider's resource, so they share its scrub and one resourceSpans.
    expect(body.resourceSpans).toHaveLength(1);
    const [resourceSpans] = body.resourceSpans;
    expect(resourceSpans?.resource.attributes.map(({ key }) => key)).toEqual([
      'service.name',
      'service.version',
    ]);
    expect(resourceSpans?.resource.schemaUrl).toBeUndefined();
    const [scopeSpans] = resourceSpans?.scopeSpans ?? [];
    expect(scopeSpans?.scope).toEqual({ name: INSTRUMENTATION_NAME, version: '1.2.3' });
    expect(scopeSpans?.spans.map((span) => span.name)).toEqual(['api.request', 'unknown']);
    expect(scopeSpans?.spans[0]?.attributes.map(({ key }) => key)).toEqual([
      'http.request.method',
      'http.route',
    ]);
    expect(scopeSpans?.spans[0]?.attributes[1]?.value.stringValue).toBe('/v1/projects/:id/runs');
    expect(scopeSpans?.spans[1]?.attributes).toEqual([]);
    expect(request.text).not.toContain(EMAIL);
    expect(request.text).not.toContain(RUN_ID);
    expect(request.text).not.toContain(CREDENTIAL);
  });
});

describe('TelemetryService page-hide flush over the real OTLP transport', () => {
  let server: Server;
  let posted: RecordedPost[];
  let service: TelemetryService;

  beforeEach(async () => {
    posted = [];
    server = await listen(posted);
    const address = server.address();
    if (address === null || typeof address === 'string') {
      throw new Error('the loopback receiver has no port');
    }
    TestBed.configureTestingModule({
      providers: [
        {
          provide: TELEMETRY_CONFIG,
          useValue: {
            endpoint: `http://127.0.0.1:${address.port}${ENDPOINT_PATH}`,
            sampleRatio: 1,
          },
        },
        { provide: TELEMETRY_OPT_OUT, useValue: false },
      ],
    });
    service = TestBed.inject(TelemetryService);
  });

  afterEach(async () => {
    await service.runtime?.shutdown();
    TestBed.resetTestingModule();
    await new Promise((resolve) => {
      server.close(() => resolve(undefined));
    });
  });

  it('exports the scrubbed batch when the window fires pagehide', async () => {
    service.start();
    await vi.waitFor(() => expect(service.runtime).not.toBeNull(), { interval: 1 });
    const call = service.startApiCall({ method: 'POST', route: `/v1/projects/${RUN_ID}/runs` });
    if (call === null) {
      throw new Error('the facade had no runtime');
    }
    call.end({ status: 500, outcome: 'error' });

    // Production holds spans for five seconds; only the page-hide flush can export them here.
    expect(posted).toEqual([]);

    window.dispatchEvent(new Event('pagehide'));
    await vi.waitFor(() => expect(posted).toHaveLength(1), { interval: 1 });

    const [request] = posted;
    if (request === undefined) {
      throw new Error('the exporter posted nothing');
    }
    expect(request.url).toBe(ENDPOINT_PATH);
    const body = parseTraces(request.text);
    expect(body.resourceSpans).toHaveLength(1);
    const [resourceSpans] = body.resourceSpans;
    for (const attribute of resourceSpans?.resource.attributes ?? []) {
      expect(ALLOWED_RESOURCE_KEYS.has(attribute.key)).toBe(true);
    }
    expect(resourceSpans?.resource.schemaUrl).toBeUndefined();
    const [scopeSpans] = resourceSpans?.scopeSpans ?? [];
    // No build version in this config, so the scope carries a name and drops the version.
    expect(scopeSpans?.scope).toEqual({ name: INSTRUMENTATION_NAME });
    expect(scopeSpans?.spans.map((span) => span.name)).toEqual(['api.request']);
    const spanAttributes = scopeSpans?.spans[0]?.attributes ?? [];
    for (const attribute of spanAttributes) {
      expect(ALLOWED_ATTRIBUTE_KEYS.has(attribute.key)).toBe(true);
    }
    const route = spanAttributes.find((attribute) => attribute.key === 'http.route');
    expect(route?.value.stringValue).toBe('/v1/projects/:id/runs');
    expect(request.text).not.toContain(RUN_ID);
  });
});
