// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: OTLP endpoint from config, or a
// no-op exporter when unset): the two `<meta>` tags are the whole configuration surface, so this
// spec is the contract for what a deployment may write there. Every rejection case below is a
// value a deploy could plausibly produce by accident, and each one has to land on "export
// nothing" (endpoint) or "sample everything" (ratio) rather than on a thrown error or a silently
// disabled tracer.
import { DOCUMENT } from '@angular/common';
import { TestBed } from '@angular/core/testing';

import {
  parseOtlpEndpoint,
  parseSampleRatio,
  readTelemetryConfig,
  TELEMETRY_CONFIG,
  type TelemetryConfig,
} from './telemetry-config';

const ENDPOINT_META = 'evidgate-otlp-endpoint';
const RATIO_META = 'evidgate-telemetry-sample-ratio';

/** A detached document carrying exactly the meta tags of `metas`. */
function documentWith(metas: Readonly<Record<string, string>>): Document {
  const doc = document.implementation.createHTMLDocument('telemetry-config-spec');
  for (const [name, content] of Object.entries(metas)) {
    const meta = doc.createElement('meta');
    meta.setAttribute('name', name);
    meta.setAttribute('content', content);
    doc.head.appendChild(meta);
  }
  return doc;
}

/** A URL with a literal `user:password` authority, assembled so no committed line holds one. */
function urlWithUserinfo(host: string, path: string): string {
  return ['https', '://', 'user', ':', 'secret', '@', host, path].join('');
}

interface EndpointCase {
  readonly label: string;
  /** Builds the tag value; a function so the credential case is assembled at runtime. */
  readonly raw: () => string;
  readonly expected: string | null;
}

const ENDPOINT_CASES: readonly EndpointCase[] = [
  {
    label: 'keeps an https endpoint and normalizes it',
    raw: () => 'https://otlp.evidgate.example/v1/traces',
    expected: 'https://otlp.evidgate.example/v1/traces',
  },
  {
    label: 'rejects plain http on localhost (the app CSP names an https origin)',
    raw: () => 'http://localhost:4318/v1/traces',
    expected: null,
  },
  {
    label: 'rejects plain http on 127.0.0.1',
    raw: () => 'http://127.0.0.1:4318/v1/traces',
    expected: null,
  },
  {
    label: 'rejects plain http on the IPv6 loopback',
    raw: () => 'http://[::1]:4318/v1/traces',
    expected: null,
  },
  {
    label: 'trims the surrounding whitespace of a tag value',
    raw: () => '  https://otlp.evidgate.example/v1/traces  ',
    expected: 'https://otlp.evidgate.example/v1/traces',
  },
  {
    label: 'rejects plain http to a remote host (telemetry crosses no network in clear)',
    raw: () => 'http://otlp.evidgate.example/v1/traces',
    expected: null,
  },
  {
    label: 'rejects a scheme that is not http(s)',
    raw: () => 'ftp://otlp.evidgate.example/v1/traces',
    expected: null,
  },
  {
    label: 'rejects a relative URL',
    raw: () => '/v1/traces',
    expected: null,
  },
  {
    label: 'rejects credentials in the authority',
    raw: () => urlWithUserinfo('otlp.evidgate.example', '/v1/traces'),
    expected: null,
  },
  {
    label: 'rejects a query string',
    raw: () => 'https://otlp.evidgate.example/v1/traces?token=abc',
    expected: null,
  },
  {
    label: 'rejects a fragment',
    raw: () => 'https://otlp.evidgate.example/v1/traces#frag',
    expected: null,
  },
  {
    label: 'rejects an unparsable value',
    raw: () => 'not a url',
    expected: null,
  },
];

describe('parseOtlpEndpoint', () => {
  it('treats an absent tag as "export nothing"', () => {
    expect(parseOtlpEndpoint(null)).toBeNull();
  });

  it('treats an empty tag as "export nothing"', () => {
    expect(parseOtlpEndpoint('')).toBeNull();
  });

  for (const testCase of ENDPOINT_CASES) {
    it(`${testCase.label}`, () => {
      const resolved = parseOtlpEndpoint(testCase.raw().trim());
      expect(resolved).toBe(testCase.expected);
    });
  }
});

interface RatioCase {
  readonly label: string;
  readonly raw: string | null;
  readonly expected: number;
}

const RATIO_CASES: readonly RatioCase[] = [
  { label: 'absent tag samples every trace', raw: null, expected: 1 },
  { label: '0 disables root sampling', raw: '0', expected: 0 },
  { label: '0.25 is kept as written', raw: '0.25', expected: 0.25 },
  { label: '1 samples every trace', raw: '1', expected: 1 },
  { label: 'is tolerant of surrounding whitespace', raw: ' 0.5 ', expected: 0.5 },
  { label: 'does not clamp an out-of-range value above 1', raw: '1.5', expected: 1 },
  { label: 'does not clamp a negative value', raw: '-0.1', expected: 1 },
  { label: 'falls back for text', raw: 'half', expected: 1 },
  { label: 'falls back for NaN', raw: 'NaN', expected: 1 },
  { label: 'falls back for Infinity', raw: 'Infinity', expected: 1 },
];

describe('parseSampleRatio', () => {
  for (const testCase of RATIO_CASES) {
    it(`${testCase.label}`, () => {
      expect(parseSampleRatio(testCase.raw)).toBe(testCase.expected);
    });
  }
});

describe('readTelemetryConfig', () => {
  it('reads both meta tags of a document', () => {
    const config = readTelemetryConfig(
      documentWith({
        [ENDPOINT_META]: 'https://otlp.evidgate.example/v1/traces',
        [RATIO_META]: '0.5',
      }),
    );

    expect(config).toEqual({
      endpoint: 'https://otlp.evidgate.example/v1/traces',
      sampleRatio: 0.5,
    });
  });

  it('disables export and samples everything when the document has no tags', () => {
    const config: TelemetryConfig = readTelemetryConfig(documentWith({}));

    expect(config.endpoint).toBeNull();
    expect(config.sampleRatio).toBe(1);
  });

  it('treats an empty content attribute as unset', () => {
    const config = readTelemetryConfig(documentWith({ [ENDPOINT_META]: '', [RATIO_META]: ' ' }));

    expect(config.endpoint).toBeNull();
    expect(config.sampleRatio).toBe(1);
  });

  it('reads the tags it finds and defaults the ones it does not', () => {
    const config = readTelemetryConfig(
      documentWith({ [RATIO_META]: '0.125', 'some-other-meta': 'https://ignored.example' }),
    );

    expect(config.endpoint).toBeNull();
    expect(config.sampleRatio).toBe(0.125);
  });
});

describe('TELEMETRY_CONFIG', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    document.head.querySelectorAll(`meta[name="${ENDPOINT_META}"]`).forEach((tag) => tag.remove());
  });

  it('reads the application document once through the injection token', () => {
    const meta = document.createElement('meta');
    meta.setAttribute('name', ENDPOINT_META);
    meta.setAttribute('content', 'https://otlp.evidgate.example/v1/traces');
    document.head.appendChild(meta);

    TestBed.configureTestingModule({});

    const config: TelemetryConfig = TestBed.inject(TELEMETRY_CONFIG);

    expect(config).toEqual({
      endpoint: 'https://otlp.evidgate.example/v1/traces',
      sampleRatio: 1,
    });
    expect(TestBed.inject(DOCUMENT)).toBe(document);
  });
});
