// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1 — the row that names this spec: "attributes
// carry ids only, no PII or content"; owner brief: client error reporting with PII/secret
// scrubbing): the allowlist is the contract, so it is asserted exactly, and one hostile span
// (credentials, an e-mail address, a UUID, a concrete API URL, an off-list resource) goes
// through a real `TracerProvider` -> `ScrubbingSpanExporter` -> `InMemorySpanExporter` chain to
// prove what a receiver would actually see.
//
// The span is built with the real SDK rather than a hand-made object, so the test also covers
// what the SDK itself records (`status.message`, event attributes, its default resource).
// Nothing here touches the global tracer provider: the provider is local to the test.
import { ROOT_CONTEXT, SpanStatusCode } from '@opentelemetry/api';
import { resourceFromAttributes } from '@opentelemetry/resources';
import {
  AlwaysOnSampler,
  InMemorySpanExporter,
  SimpleSpanProcessor,
  TracerProvider,
} from '@opentelemetry/sdk-trace';
import type { ReadableSpan } from '@opentelemetry/sdk-trace';

import { ALLOWED_ATTRIBUTE_KEYS, ALLOWED_RESOURCE_KEYS } from './attribute-allowlist';
import { scrubAttributes } from './scrub';
import { ScrubbingSpanExporter } from './scrubbing-exporter';

/** PII: never on a span, under any key. */
const EMAIL = ['julia.moreau', 'example.test'].join('@');
/** The id a customer's request path names: it must disappear with the path it came from. */
const PATH_ID = ['9c1d7e2a', '5b3f', '4a8c', 'b7d1', '20e4f6a8c0b3'].join('-');
/** The id the API echoes in `X-Request-Id`: kept on purpose — an id carries no content. */
const REQUEST_ID = ['3f2b9d54', '0d7e', '4c1a', '9a2e', '6b0f5c7d81c4'].join('-');
const CREDENTIAL = 'abcdef1234567890';

/** Every attribute key a web span may carry, spelled out: the allowlist is a contract. */
const EXPECTED_ATTRIBUTE_KEYS: readonly string[] = [
  'error.message',
  'error.type',
  'evidgate.api.error_code',
  'evidgate.api.error_kind',
  'evidgate.api.retryable',
  'evidgate.error.source',
  'evidgate.locale',
  'evidgate.outcome',
  'evidgate.request_id',
  'evidgate.route',
  'http.request.method',
  'http.response.status_code',
  'http.route',
  'web_vital.name',
  'web_vital.navigation_type',
  'web_vital.rating',
  'web_vital.value',
];

/** The resource keys kept: the OTel SDK identity plus this service's name and version. */
const EXPECTED_RESOURCE_KEYS: readonly string[] = [
  'service.name',
  'service.version',
  'telemetry.sdk.language',
  'telemetry.sdk.name',
  'telemetry.sdk.version',
];

/** One valid value per allowlisted key: a key with no rule in `scrub.ts` would drop out here. */
const VALID_ATTRIBUTES = {
  'http.request.method': 'GET',
  'http.response.status_code': 200,
  'http.route': '/v1/projects/:id/runs',
  'evidgate.route': '/projects/:projectId/runs',
  'evidgate.outcome': 'ok',
  'evidgate.request_id': REQUEST_ID,
  'evidgate.locale': 'en',
  'evidgate.api.error_code': 'csrf_failed',
  'evidgate.api.error_kind': 'validation',
  'evidgate.api.retryable': false,
  'evidgate.error.source': 'angular',
  'error.type': 'TypeError',
  'error.message': 'The request did not reach the server.',
  'web_vital.name': 'INP',
  'web_vital.value': 187.5,
  'web_vital.rating': 'good',
  'web_vital.navigation_type': 'navigate',
};

/** The single span the test exporter received, or a failure that says why there is none. */
function onlySpan(spans: readonly ReadableSpan[]): ReadableSpan {
  expect(spans).toHaveLength(1);
  const span = spans[0];
  if (span === undefined) {
    throw new Error('expected exactly one exported span');
  }
  return span;
}

describe('the attribute allowlist', () => {
  it('allows exactly the seventeen attribute keys the plan names', () => {
    expect([...ALLOWED_ATTRIBUTE_KEYS].sort()).toEqual([...EXPECTED_ATTRIBUTE_KEYS].sort());
  });

  it('allows exactly the five resource keys the plan names', () => {
    expect([...ALLOWED_RESOURCE_KEYS].sort()).toEqual([...EXPECTED_RESOURCE_KEYS].sort());
  });

  it('keeps a valid value for every allowlisted key, so no key is allowlisted without a rule', () => {
    const scrubbed = scrubAttributes(VALID_ATTRIBUTES);

    expect(Object.keys(scrubbed).sort()).toEqual([...EXPECTED_ATTRIBUTE_KEYS].sort());
    expect(scrubbed).toEqual(VALID_ATTRIBUTES);
  });
});

describe('a span exported through the scrubbing exporter', () => {
  interface HostileRun {
    readonly exported: ReadableSpan;
    readonly serviceName: unknown;
  }

  /** Runs one hostile span through the real SDK chain and returns what the exporter emitted. */
  async function exportHostileSpan(): Promise<HostileRun> {
    const inner = new InMemorySpanExporter();
    const provider = new TracerProvider({
      sampler: new AlwaysOnSampler(),
      resource: resourceFromAttributes({
        'service.name': 'evidgate-web',
        'service.version': '1.2.3',
        'deployment.environment': 'acme-prod',
        'user.email': EMAIL,
        'host.name': 'julia-macbook',
      }),
      spanProcessors: [new SimpleSpanProcessor({ exporter: new ScrubbingSpanExporter(inner) })],
    });
    const tracer = provider.getTracer('evidgate-web-test');
    const span = tracer.startSpan(
      'api.request',
      {
        attributes: {
          'http.request.method': 'POST',
          'http.route': `/v1/projects/${PATH_ID}/runs`,
          'http.url': `https://acme.example.test/v1/runs?token=${CREDENTIAL}`,
          'evidgate.request_id': REQUEST_ID,
          'user.email': EMAIL,
        },
      },
      ROOT_CONTEXT,
    );
    span.addEvent(`failed for ${EMAIL}`, {
      'error.message': `boom "${EMAIL}"`,
      'http.url': `https://acme.example.test/v1/runs?token=${CREDENTIAL}`,
    });
    span.setStatus({ code: SpanStatusCode.ERROR, message: `Bearer ${CREDENTIAL} rejected` });
    span.end();

    await provider.forceFlush();
    const exported = onlySpan(inner.getFinishedSpans());
    await provider.shutdown();
    return { exported, serviceName: exported.resource.attributes['service.name'] };
  }

  it('keeps only allowlisted, scrubbed attributes', async () => {
    const { exported } = await exportHostileSpan();

    expect(exported.attributes).toEqual({
      'http.request.method': 'POST',
      'http.route': '/v1/projects/:id/runs',
      'evidgate.request_id': REQUEST_ID,
    });
    for (const key of Object.keys(exported.attributes)) {
      expect(ALLOWED_ATTRIBUTE_KEYS.has(key)).toBe(true);
    }
  });

  it('scrubs the event name and drops the event attributes it cannot allow', async () => {
    const { exported } = await exportHostileSpan();
    const [event] = exported.events;

    expect(exported.events).toHaveLength(1);
    expect(event?.name).toBe('failed for [email]');
    expect(event?.attributes).toEqual({ 'error.message': 'boom [quoted]' });
  });

  it('drops the status message and the links, keeping the status code', async () => {
    const { exported } = await exportHostileSpan();

    expect(exported.status).toEqual({ code: SpanStatusCode.ERROR });
    expect(exported.links).toEqual([]);
  });

  it('filters the resource down to the allowlist, keeping the service identity', async () => {
    const { exported, serviceName } = await exportHostileSpan();

    expect(serviceName).toBe('evidgate-web');
    expect(exported.resource.attributes).not.toHaveProperty('deployment.environment');
    expect(exported.resource.attributes).not.toHaveProperty('user.email');
    expect(exported.resource.attributes).not.toHaveProperty('host.name');
    for (const key of Object.keys(exported.resource.attributes)) {
      expect(ALLOWED_RESOURCE_KEYS.has(key)).toBe(true);
    }
  });

  it('leaves no e-mail address, path id or credential anywhere on the exported span', async () => {
    const { exported } = await exportHostileSpan();
    const serialized = JSON.stringify(exported);

    expect(serialized).not.toContain(EMAIL);
    expect(serialized).not.toContain(PATH_ID);
    expect(serialized).not.toContain(CREDENTIAL);
    expect(serialized).not.toContain('acme-prod');
    expect(serialized).not.toContain('julia-macbook');
    expect(serialized).not.toContain('token=');
    // The one identifier that is meant to survive: it is an opaque id, not content.
    expect(serialized).toContain(REQUEST_ID);
  });
});
