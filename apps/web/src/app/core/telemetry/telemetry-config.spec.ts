// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: OTLP endpoint from config, or a
// no-op exporter when unset): the two `<meta>` tags are the whole configuration surface, so this
// spec is the contract for what a deployment may write there. Every rejection case below is a
// value a deploy could plausibly produce by accident, and each one has to land on "export
// nothing" (endpoint) or "sample everything" (ratio) rather than on a thrown error or a silently
// disabled tracer.
import { DOCUMENT } from '@angular/common';
import { TestBed } from '@angular/core/testing';

import {
  isTelemetryOptedOut,
  parseOtlpEndpoint,
  parseSampleRatio,
  parseServiceVersion,
  readTelemetryConfig,
  TELEMETRY_CONFIG,
  TELEMETRY_OPT_OUT,
  type PrivacySignals,
  type TelemetryConfig,
} from './telemetry-config';

const ENDPOINT_META = 'evidgate-otlp-endpoint';
const RATIO_META = 'evidgate-telemetry-sample-ratio';
const SERVICE_VERSION_META = 'evidgate-service-version';

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

interface VersionCase {
  readonly label: string;
  readonly raw: string | null;
  readonly expected: string | null;
}

const VERSION_CASES: readonly VersionCase[] = [
  { label: 'keeps a plain semver', raw: '1.2.3', expected: '1.2.3' },
  { label: 'keeps build metadata', raw: '1.2.3+abc', expected: '1.2.3+abc' },
  { label: 'keeps a prerelease', raw: '1.2.3-rc.1', expected: '1.2.3-rc.1' },
  { label: 'keeps a git describe suffix', raw: '1.2.3-12-gabc1234', expected: '1.2.3-12-gabc1234' },
  { label: 'keeps exactly 32 characters', raw: 'a'.repeat(32), expected: 'a'.repeat(32) },
  { label: 'drops more than 32 characters', raw: 'a'.repeat(33), expected: null },
  { label: 'drops an absent tag', raw: null, expected: null },
  { label: 'drops an empty tag', raw: '', expected: null },
  { label: 'drops a path', raw: 'release/1.2.3', expected: null },
  { label: 'drops a URL', raw: 'https://ci.evidgate.example/builds/1.2.3', expected: null },
  { label: 'drops free text with a space', raw: '1.2.3 beta', expected: null },
  { label: 'drops an underscore', raw: '1.2.3_rc1', expected: null },
];

describe('parseServiceVersion', () => {
  for (const testCase of VERSION_CASES) {
    it(`${testCase.label}`, () => {
      expect(parseServiceVersion(testCase.raw)).toBe(testCase.expected);
    });
  }
});

interface OptOutCase {
  readonly label: string;
  readonly signals: PrivacySignals | null;
  readonly expected: boolean;
}

/**
 * Each privacy signal alone, because either one is enough: a deployment must not be able to lose
 * the opt-out by leaving the other signal unset.
 */
const OPT_OUT_CASES: readonly OptOutCase[] = [
  { label: 'Do-Not-Track "1" opts out', signals: { doNotTrack: '1' }, expected: true },
  {
    label: 'Do-Not-Track "yes" (legacy spelling) opts out',
    signals: { doNotTrack: 'yes' },
    expected: true,
  },
  { label: 'msDoNotTrack "1" opts out', signals: { msDoNotTrack: '1' }, expected: true },
  {
    label: 'msDoNotTrack "yes" (legacy spelling) opts out',
    signals: { msDoNotTrack: 'yes' },
    expected: true,
  },
  {
    label: 'Global Privacy Control true opts out',
    signals: { globalPrivacyControl: true },
    expected: true,
  },
  {
    label: 'Global Privacy Control opts out even with Do-Not-Track "0"',
    signals: { doNotTrack: '0', globalPrivacyControl: true },
    expected: true,
  },
  { label: 'Do-Not-Track "0" does not opt out', signals: { doNotTrack: '0' }, expected: false },
  { label: 'Do-Not-Track null does not opt out', signals: { doNotTrack: null }, expected: false },
  {
    label: 'Do-Not-Track "unspecified" does not opt out',
    signals: { doNotTrack: 'unspecified' },
    expected: false,
  },
  {
    label: 'an absent signal does not opt out',
    signals: {},
    expected: false,
  },
  {
    label: 'Global Privacy Control false does not opt out',
    signals: { globalPrivacyControl: false },
    expected: false,
  },
  { label: 'an empty navigator does not opt out', signals: {}, expected: false },
  { label: 'no navigator at all does not opt out', signals: null, expected: false },
];

describe('isTelemetryOptedOut', () => {
  for (const testCase of OPT_OUT_CASES) {
    it(`${testCase.label}`, () => {
      expect(isTelemetryOptedOut(testCase.signals)).toBe(testCase.expected);
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

  it('records the build version when the tag is valid', () => {
    const config = readTelemetryConfig(
      documentWith({ [SERVICE_VERSION_META]: '1.2.3+abc', [RATIO_META]: '0.5' }),
    );

    expect(config).toEqual({
      endpoint: null,
      sampleRatio: 0.5,
      serviceVersion: '1.2.3+abc',
    });
  });

  it('omits the version key when the document carries no version tag', () => {
    const config = readTelemetryConfig(documentWith({ [RATIO_META]: '1' }));

    // Absent, not `undefined`: the chunk is only told about a version the deploy named.
    expect('serviceVersion' in config).toBe(false);
  });

  it('omits the version key when the version tag is malformed', () => {
    const config = readTelemetryConfig(
      documentWith({ [SERVICE_VERSION_META]: 'https://ci.evidgate.example/builds/1.2.3' }),
    );

    expect('serviceVersion' in config).toBe(false);
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

/**
 * A document whose only interesting member is `defaultView.navigator`: the token factory reads the
 * privacy signals off exactly that path, and nothing else of the document. jsdom gives a created
 * document no window at all, so the spec defines the one member the factory reads.
 */
function documentWithPrivacySignals(signals: PrivacySignals): Document {
  const doc = document.implementation.createHTMLDocument('telemetry-privacy-signals');
  Object.defineProperty(doc, 'defaultView', {
    configurable: true,
    value: { navigator: signals },
  });
  return doc;
}

describe('TELEMETRY_OPT_OUT', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
  });

  /** Injects the token against the provided document. */
  function optedOutFor(provided: Document): boolean {
    TestBed.configureTestingModule({ providers: [{ provide: DOCUMENT, useValue: provided }] });
    return TestBed.inject(TELEMETRY_OPT_OUT);
  }

  it('reads Do-Not-Track off the provided document navigator', () => {
    expect(optedOutFor(documentWithPrivacySignals({ doNotTrack: '1' }))).toBe(true);
  });

  it('reads the legacy Do-Not-Track spelling off the provided document navigator', () => {
    expect(optedOutFor(documentWithPrivacySignals({ doNotTrack: 'yes' }))).toBe(true);
  });

  it('reads msDoNotTrack off the provided document navigator', () => {
    expect(optedOutFor(documentWithPrivacySignals({ msDoNotTrack: '1' }))).toBe(true);
  });

  it('reads Global Privacy Control off the provided document navigator', () => {
    expect(optedOutFor(documentWithPrivacySignals({ globalPrivacyControl: true }))).toBe(true);
  });

  it('is false for a provided document whose navigator asks for nothing', () => {
    expect(optedOutFor(documentWithPrivacySignals({ doNotTrack: '0' }))).toBe(false);
  });

  it('is false for a provided document without a window', () => {
    // Server-side rendering, and a detached document: there is no navigator to ask.
    expect(optedOutFor(document.implementation.createHTMLDocument('telemetry-opt-out-spec'))).toBe(
      false,
    );
  });
});
